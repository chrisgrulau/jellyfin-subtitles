using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Subtitles.Pipeline;

/// <summary>
/// An entry for Jellyfin's Activity log (Dashboard → Activity).
/// </summary>
/// <param name="Name">The headline.</param>
/// <param name="ShortOverview">One line.</param>
/// <param name="Overview">The details.</param>
/// <param name="Severity">How it is shown.</param>
public sealed record ActivityNote(string Name, string ShortOverview, string Overview, LogLevel Severity);

/// <summary>
/// Copies what needs attention (something waiting for review, a folder that can't be written, a subtitle search the
/// provider stopped) and subtitles that were added or generated to Jellyfin's Activity log, so they're seen without opening the
/// plugin page (FAM-05). The same subtitle and outcome is written at most once a day.
/// </summary>
public sealed class SubtitleActivity
{
    /// <summary>The Activity log type of these entries.</summary>
    public const string Type = "ShoalSubtitles";

    /// <summary>How long the same subtitle and outcome isn't written again.</summary>
    public static readonly TimeSpan RepeatAfter = TimeSpan.FromDays(1);

    private readonly Func<ActivityNote, Task> _write;
    private readonly TimeProvider _clock;
    private readonly Func<bool> _enabled;
    private readonly Dictionary<string, DateTimeOffset> _written = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleActivity"/> class.
    /// </summary>
    /// <param name="write">Writes an entry to Jellyfin's Activity log.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="enabled">Whether the settings allow writing.</param>
    public SubtitleActivity(Func<ActivityNote, Task> write, TimeProvider clock, Func<bool>? enabled = null)
    {
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _enabled = enabled ?? (() => true);
    }

    /// <summary>
    /// The Activity log entry for a result, if it is one worth writing.
    /// </summary>
    /// <param name="r">The result.</param>
    /// <returns>The note, or <c>null</c> (routine checks stay on the plugin page).</returns>
    public static ActivityNote? NoteFor(SubtitleResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var (headline, severity) = r.PendingReview && r.Findings.Any(Discrepancy.DiscrepancyReview.IsWholeFile) ? ("Lines of a subtitle differ from what is said (whole file): ", LogLevel.Warning)
            : r.PendingReview ? ("A subtitle is waiting for review: ", LogLevel.Warning)
            : r.Status switch
            {
                ResultStatus.Added => ("Shoal Subtitles added a subtitle: ", LogLevel.Information),
                ResultStatus.Generated => ("Shoal Subtitles generated a subtitle: ", LogLevel.Information),
                ResultStatus.CantWrite => ("Shoal Subtitles can't write beside: ", LogLevel.Warning),
                _ => ((string?)null, LogLevel.None),
            };
        return headline is null ? null : new ActivityNote(headline + r.Name, Short(r.Explanation), string.Join('\n', r.Examples.Take(12)), severity);
    }

    /// <summary>
    /// Writes a result to the Activity log unless it isn't worth writing, is switched off, or was written in the last
    /// day. Never throws.
    /// </summary>
    /// <param name="r">The result.</param>
    /// <returns>Whether it was written.</returns>
    public Task<bool> NotifyAsync(SubtitleResult r)
        => NoteFor(r) is { } note ? WriteOnceAsync(r.Id + "|" + r.Status + "|" + r.PendingReview + "|" + r.WholeFile?.Time.ToUnixTimeSeconds(), note) : Task.FromResult(false);

    /// <summary>
    /// Writes that a subtitle provider stopped a search run (not signed in, daily allowance used up).
    /// </summary>
    /// <param name="why">The provider's reason.</param>
    /// <returns>Whether it was written.</returns>
    public Task<bool> NotifyStoppedAsync(string why)
    {
        ArgumentNullException.ThrowIfNull(why);
        return WriteOnceAsync("stopped|" + why, new ActivityNote("Shoal Subtitles stopped searching for today", Short(why), why, LogLevel.Warning));
    }

    private static string Short(string text) => text.Length > 250 ? text[..250] + "…" : text;

    private async Task<bool> WriteOnceAsync(string key, ActivityNote note)
    {
        if (!_enabled())
        {
            return false;
        }

        var now = _clock.GetUtcNow();
        lock (_lock)
        {
            if (_written.TryGetValue(key, out var at) && now - at < RepeatAfter)
            {
                return false;
            }

            _written[key] = now;
            if (_written.Count > 5000)
            {
                foreach (var old in _written.Where(w => now - w.Value >= RepeatAfter).Select(w => w.Key).ToList())
                {
                    _written.Remove(old);
                }
            }
        }

        try
        {
            await _write(note).ConfigureAwait(false);
            return true;
        }
#pragma warning disable CA1031 // The Activity log is a convenience: a failure there mustn't affect the run
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }
}
