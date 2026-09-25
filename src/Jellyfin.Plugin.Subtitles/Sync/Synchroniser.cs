using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.Audio;
using Jellyfin.Plugin.Subtitles.Formats;

namespace Jellyfin.Plugin.Subtitles.Sync;

/// <summary>
/// Lines subtitles up with a video's audio. A few stretches of audio spread across the video are read (not the whole
/// soundtrack), the points where speech starts are found in each, and they are compared with where subtitle lines start,
/// at each common frame-rate ratio. Stretches avoid the first and last few percent, where titles and credits are.
/// </summary>
public sealed class Synchroniser
{
    /// <summary>How many stretches of audio are read.</summary>
    public const int Stretches = 6;

    /// <summary>How long each stretch is.</summary>
    public static readonly TimeSpan StretchLength = TimeSpan.FromMinutes(2);

    /// <summary>The largest offset looked for, either way.</summary>
    public static readonly TimeSpan MaxOffset = TimeSpan.FromSeconds(90);

    private readonly IAudioSource _audio;
    private readonly double _detectorLag;

    /// <summary>
    /// Initializes a new instance of the <see cref="Synchroniser"/> class.
    /// </summary>
    /// <param name="audio">The video's audio.</param>
    /// <param name="detectorLag">The speech-start detector's lag in seconds (<see cref="SyncSolver.DetectorLag"/> for real
    /// audio; 0 for synthetic test audio with instant starts).</param>
    public Synchroniser(IAudioSource audio, double detectorLag = SyncSolver.DetectorLag)
    {
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _detectorLag = detectorLag;
    }

    /// <summary>
    /// Where the stretches go in a video of the given length.
    /// </summary>
    /// <param name="duration">The video's length.</param>
    /// <returns>Start and length of each stretch.</returns>
    public static IReadOnlyList<(TimeSpan Start, TimeSpan Length)> Plan(TimeSpan duration)
    {
        var usable = duration.TotalSeconds * 0.84;
        if (usable < 60)
        {
            return duration.TotalSeconds < 20 ? [] : [(TimeSpan.Zero, duration)];
        }

        var count = Math.Clamp((int)(usable / StretchLength.TotalSeconds), 1, Stretches);
        var length = Math.Min(StretchLength.TotalSeconds, usable / count);
        var first = duration.TotalSeconds * 0.08;
        var gap = count == 1 ? 0 : (usable - length) / (count - 1);
        return [.. Enumerable.Range(0, count).Select(i => (TimeSpan.FromSeconds(first + (i * gap)), TimeSpan.FromSeconds(length)))];
    }

    /// <summary>
    /// Works out the timing correction for subtitles.
    /// </summary>
    /// <param name="subtitles">The subtitles.</param>
    /// <param name="duration">The video's length.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The correction (apply it with <see cref="SubtitleDocument.Retime"/> and <see cref="SyncModel.Map"/>).</returns>
    public async Task<SyncModel> SolveAsync(SubtitleDocument subtitles, TimeSpan duration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subtitles);
        var timeline = SpeechTimeline.From(subtitles);
        var byScale = SyncSolver.Scales.ToDictionary(s => s, _ => new List<WindowCurve>());
        if (timeline.Count > 0)
        {
            foreach (var (start, length) in Plan(duration))
            {
                var samples = await _audio.ReadAsync(start, length, cancellationToken).ConfigureAwait(false);
                var onsets = VoiceActivity.Onsets(samples);
                foreach (var scale in SyncSolver.Scales)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    byScale[scale].Add(OffsetSearch.Find(onsets, start.TotalSeconds, timeline, scale, MaxOffset.TotalSeconds));
                }
            }
        }

        return SyncSolver.Solve(byScale.ToDictionary(p => p.Key, p => (IReadOnlyList<WindowCurve>)p.Value), MaxOffset.TotalSeconds, _detectorLag);
    }
}
