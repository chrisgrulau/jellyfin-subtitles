using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Subtitles.Discrepancy;
using Jellyfin.Plugin.Subtitles.Pipeline;

namespace Jellyfin.Plugin.Subtitles.Api;

/// <summary>
/// A result with its view, as one row of the results list.
/// </summary>
/// <param name="Result">The result (the page acts on its id and shows its findings and examples in the detail area).</param>
/// <param name="View">How it is shown.</param>
public sealed record ResultRow(SubtitleResult Result, ResultView View);

/// <summary>
/// Counts over all results, for the filter and the summary line.
/// </summary>
/// <param name="All">Every result.</param>
/// <param name="ByStatus">Results per status (by name).</param>
/// <param name="Waiting">Results waiting for review.</param>
/// <param name="WholeFile">Results the whole-file check flagged.</param>
/// <param name="Queued">Results queued for a whole-file check.</param>
/// <param name="FellBack">Results whose speech-to-text fell back or failed.</param>
public sealed record ResultTally(int All, IReadOnlyDictionary<string, int> ByStatus, int Waiting, int WholeFile, int Queued, int FellBack);

/// <summary>
/// One page of results.
/// </summary>
/// <param name="Items">The rows.</param>
/// <param name="Total">How many results match the filter and search.</param>
/// <param name="Next">The offset of the next page, or <c>null</c> at the end.</param>
/// <param name="Tally">Counts over all results.</param>
public sealed record ResultsPage(IReadOnlyList<ResultRow> Items, int Total, int? Next, ResultTally Tally);

/// <summary>
/// Filtering, searching and paging the results list, on the server (the list can hold a whole library).
/// </summary>
public static class ResultQuery
{
    /// <summary>The first page's size on the settings page.</summary>
    public const int DefaultPage = 15;

    /// <summary>The largest page.</summary>
    public const int MaxPage = 100;

    /// <summary>The filters besides a status name.</summary>
    public static readonly IReadOnlyList<string> Filters = ["waiting", "wholefile", "queued", "fellback"];

    /// <summary>
    /// Whether a result is shown for a filter and a search.
    /// </summary>
    /// <param name="r">The result.</param>
    /// <param name="filter">Empty for everything; <c>waiting</c>, <c>wholefile</c>, <c>queued</c>, <c>fellback</c>, or a status name.</param>
    /// <param name="search">Text to find in the name or the files' paths (any case), or empty.</param>
    /// <returns><c>true</c> if it is shown.</returns>
    public static bool Matches(SubtitleResult r, string? filter, string? search)
    {
        ArgumentNullException.ThrowIfNull(r);
        var shown = (filter ?? string.Empty) switch
        {
            "" => true,
            "waiting" => r.PendingReview,
            "wholefile" => r.Findings.Any(DiscrepancyReview.IsWholeFile),
            "queued" => r.WholeFileRequested || r.RerunWith is not null,
            "fellback" => r.SpeechFallback is not null,
            var status => string.Equals(r.Status.ToString(), status, StringComparison.Ordinal),
        };
        var text = search?.Trim();
        return shown && (string.IsNullOrEmpty(text)
            || r.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
            || r.SubtitlePath.Contains(text, StringComparison.OrdinalIgnoreCase)
            || (r.VideoPath?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false));
    }

    /// <summary>
    /// A page of results, in the order given (waiting for review first, then newest).
    /// </summary>
    /// <param name="ordered">All results, in order.</param>
    /// <param name="filter">The filter (see <see cref="Matches"/>).</param>
    /// <param name="search">The search text.</param>
    /// <param name="offset">How many matching results to skip.</param>
    /// <param name="limit">How many to return (1 to <see cref="MaxPage"/>).</param>
    /// <param name="present">Builds a row's view.</param>
    /// <returns>The page.</returns>
    public static ResultsPage Page(IReadOnlyList<SubtitleResult> ordered, string? filter, string? search, int offset, int limit, Func<SubtitleResult, ResultView> present)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        ArgumentNullException.ThrowIfNull(present);
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, MaxPage);
        var matching = ordered.Where(r => Matches(r, filter, search)).ToList();
        var items = matching.Skip(offset).Take(limit).Select(r => new ResultRow(r, present(r))).ToList();
        var next = offset + items.Count < matching.Count ? offset + items.Count : (int?)null;
        return new ResultsPage(items, matching.Count, next, Tally(ordered));
    }

    /// <summary>
    /// Counts over all results.
    /// </summary>
    /// <param name="all">The results.</param>
    /// <returns>The counts.</returns>
    public static ResultTally Tally(IReadOnlyList<SubtitleResult> all)
    {
        ArgumentNullException.ThrowIfNull(all);
        return new ResultTally(
            all.Count,
            all.GroupBy(r => r.Status.ToString()).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            all.Count(r => r.PendingReview),
            all.Count(r => r.Findings.Any(DiscrepancyReview.IsWholeFile)),
            all.Count(r => r.WholeFileRequested || r.RerunWith is not null),
            all.Count(r => r.SpeechFallback is not null));
    }
}

/// <summary>
/// Remembers what videos are (from Jellyfin's library) for a while, so a page of results doesn't ask the library again
/// for every row on every refresh.
/// </summary>
public sealed class VideoIdentityCache
{
    private readonly ConcurrentDictionary<string, (VideoIdentity? Identity, DateTimeOffset At)> _known = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly TimeSpan _keepFor;
    private readonly int _max;

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoIdentityCache"/> class.
    /// </summary>
    /// <param name="clock">Clock.</param>
    /// <param name="keepFor">How long an answer is kept (default 30 minutes).</param>
    /// <param name="max">The most answers kept.</param>
    public VideoIdentityCache(TimeProvider? clock = null, TimeSpan? keepFor = null, int max = 5000)
    {
        _clock = clock ?? TimeProvider.System;
        _keepFor = keepFor ?? TimeSpan.FromMinutes(30);
        _max = Math.Max(1, max);
    }

    /// <summary>
    /// What a video is: remembered, or looked up (a <c>null</c> answer is remembered too).
    /// </summary>
    /// <param name="key">The item id, or the video's path.</param>
    /// <param name="lookUp">Asks the library.</param>
    /// <returns>The identity, or <c>null</c>.</returns>
    public VideoIdentity? Get(string key, Func<VideoIdentity?> lookUp)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(lookUp);
        var now = _clock.GetUtcNow();
        if (_known.TryGetValue(key, out var hit) && now - hit.At < _keepFor)
        {
            return hit.Identity;
        }

        if (_known.Count >= _max)
        {
            _known.Clear();
        }

        var identity = lookUp();
        _known[key] = (identity, now);
        return identity;
    }
}
