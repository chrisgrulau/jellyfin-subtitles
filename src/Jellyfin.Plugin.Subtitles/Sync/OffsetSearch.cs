using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Subtitles.Sync;

/// <summary>
/// The best offset for one stretch of audio.
/// </summary>
/// <param name="AudioTime">The middle of the stretch, in seconds from the start of the video.</param>
/// <param name="Offset">Seconds to add to subtitle times (after scaling) to line them up with the audio.</param>
/// <param name="Score">How well line starts and speech starts agree at that offset (correlation, −1 to 1).</param>
/// <param name="Margin">How far ahead of the best clearly different offset it is.</param>
public sealed record WindowMatch(double AudioTime, double Offset, double Score, double Margin);

/// <summary>
/// One stretch of audio compared with the subtitles at every offset: the match and the whole score curve, so curves from
/// several stretches can be added up.
/// </summary>
/// <param name="Match">The stretch's own best offset.</param>
/// <param name="Scores">Score at each offset: index k is offset <see cref="OffsetSearch.OffsetAt"/>(k).</param>
public sealed record WindowCurve(WindowMatch Match, IReadOnlyList<double> Scores);

/// <summary>
/// Scores every offset (10 ms steps) for one stretch of audio, by correlating where speech starts in the audio with where
/// subtitle lines start.
/// </summary>
public static class OffsetSearch
{
    /// <summary>Frames per second of the signals compared.</summary>
    public const int FramesPerSecond = 100;

    /// <summary>Offsets within this distance of the best count as the same answer when judging the margin.</summary>
    public const double SameAnswer = 2.0;

    /// <summary>
    /// The offset for a score index.
    /// </summary>
    /// <param name="index">Index into <see cref="WindowCurve.Scores"/>.</param>
    /// <param name="maxOffset">The largest offset searched.</param>
    /// <returns>Offset in seconds.</returns>
    public static double OffsetAt(int index, double maxOffset) => maxOffset - (index / (double)FramesPerSecond);

    /// <summary>
    /// Scores every offset for one stretch.
    /// </summary>
    /// <param name="onsets">Speech starts in the stretch, 100 frames per second (<see cref="Audio.VoiceActivity.Onsets"/>).</param>
    /// <param name="start">Where the stretch starts in the video, in seconds.</param>
    /// <param name="timeline">The subtitles' speech timeline.</param>
    /// <param name="scale">Audio time per subtitle time (1, or a frame-rate ratio such as 25/23.976).</param>
    /// <param name="maxOffset">Largest offset tried either way, in seconds.</param>
    /// <returns>The curve and the stretch's best offset.</returns>
    public static WindowCurve Find(ReadOnlySpan<float> onsets, double start, SpeechTimeline timeline, double scale, double maxOffset)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var lags = (2 * (int)Math.Round(maxOffset * FramesPerSecond)) + 1;
        var step = 1.0 / FramesPerSecond;

        // Subtitle signal in "audio time before the offset": frame j is at audio time start − maxOffset + j·step
        var subtitles = timeline.SampleStarts((start - maxOffset) / scale, step, onsets.Length + lags - 1, scale);
        var scores = Correlation.Curve(onsets, subtitles, lags);

        var best = 0;
        for (var k = 1; k < scores.Length; k++)
        {
            if (scores[k] > scores[best])
            {
                best = k;
            }
        }

        var match = new WindowMatch(start + (onsets.Length * step / 2), OffsetAt(best, maxOffset), scores.Length == 0 ? 0 : scores[best], Margin(scores, best));
        return new WindowCurve(match, scores);
    }

    /// <summary>
    /// How far a peak is ahead of the best score at a clearly different offset.
    /// </summary>
    /// <param name="scores">The curve.</param>
    /// <param name="best">Index of the peak.</param>
    /// <returns>The margin (0 when nothing else is scored).</returns>
    public static double Margin(IReadOnlyList<double> scores, int best)
    {
        ArgumentNullException.ThrowIfNull(scores);
        var apart = (int)(SameAnswer * FramesPerSecond);
        var runnerUp = double.MinValue;
        for (var k = 0; k < scores.Count; k++)
        {
            if (Math.Abs(k - best) > apart)
            {
                runnerUp = Math.Max(runnerUp, scores[k]);
            }
        }

        return runnerUp == double.MinValue || scores.Count == 0 ? 0 : Math.Max(0, scores[best] - runnerUp);
    }
}
