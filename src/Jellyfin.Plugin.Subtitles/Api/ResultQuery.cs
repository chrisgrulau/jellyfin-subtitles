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
    /// The results the list shows: all but those that are stale (their video, or their subtitle file, was replaced or
    /// removed; see <see cref="StaleResults"/>), so they are hidden at once, before the library reports it or the next
    /// prune clears them.
    /// </summary>
    /// <param name="ordered">All results, in order.</param>
    /// <param name="gone">Whether a result is stale.</param>
    /// <returns>The results shown, in the same order.</returns>
    public static IReadOnlyList<SubtitleResult> Visible(IReadOnlyList<SubtitleResult> ordered, Func<SubtitleResult, bool> gone)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        ArgumentNullException.ThrowIfNull(gone);
        return [.. ordered.Where(r => !gone(r))];
    }

    /// <summary>
    /// The results the list shows, by the files as they are now (see <see cref="Visible(IReadOnlyList{SubtitleResult}, Func{SubtitleResult, bool})"/>).
    /// </summary>
    /// <param name="ordered">All results, in order.</param>
    /// <param name="fileExists">Whether a file exists.</param>
    /// <param name="folderExists">Whether a folder exists.</param>
    /// <returns>The results shown, in the same order.</returns>
    public static IReadOnlyList<SubtitleResult> Visible(IReadOnlyList<SubtitleResult> ordered, Func<string, bool> fileExists, Func<string, bool> folderExists)
        => Visible(ordered, r => StaleResults.IsGone(r, fileExists, folderExists));

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

/// <summary>
/// Remembers answers for a while (whether a result is stale, say), so a page of results doesn't look again for every row
/// on every refresh.
/// </summary>
/// <typeparam name="T">The answer.</typeparam>
public sealed class TimedAnswers<T>
{
    private readonly ConcurrentDictionary<string, (T Answer, DateTimeOffset At)> _known = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly TimeSpan _keepFor;
    private readonly int _max;

    /// <summary>
    /// Initializes a new instance of the <see cref="TimedAnswers{T}"/> class.
    /// </summary>
    /// <param name="clock">Clock.</param>
    /// <param name="keepFor">How long an answer is kept.</param>
    /// <param name="max">The most answers kept (past that, all are forgotten).</param>
    public TimedAnswers(TimeProvider? clock = null, TimeSpan? keepFor = null, int max = 5000)
    {
        _clock = clock ?? TimeProvider.System;
        _keepFor = keepFor ?? TimeSpan.FromMinutes(1);
        _max = Math.Max(1, max);
    }

    /// <summary>
    /// An answer: remembered, or found out.
    /// </summary>
    /// <param name="key">What it is about.</param>
    /// <param name="find">Finds it out.</param>
    /// <returns>The answer.</returns>
    public T Get(string key, Func<T> find)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(find);
        var now = _clock.GetUtcNow();
        if (_known.TryGetValue(key, out var hit) && now - hit.At < _keepFor)
        {
            return hit.Answer;
        }

        if (_known.Count >= _max)
        {
            _known.Clear();
        }

        var answer = find();
        _known[key] = (answer, now);
        return answer;
    }
}
