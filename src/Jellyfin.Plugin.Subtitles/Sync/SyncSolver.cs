using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.Subtitles.Sync;

/// <summary>
/// The outcome of synchronisation.
/// </summary>
public enum SyncStatus
{
    /// <summary>The subtitles already match the audio closely enough; nothing to change.</summary>
    InSync = 0,

    /// <summary>A clear correction was found (an offset, and a frame-rate ratio if needed).</summary>
    Corrected,

    /// <summary>No clear answer (another cut of the film, dense dialogue over music, little speech); the subtitles are
    /// left alone for a closer check.</summary>
    Unreliable,
}

/// <summary>
/// A timing correction: audio time = <see cref="Scale"/> × subtitle time + <see cref="Offset"/>.
/// </summary>
/// <param name="Status">The outcome.</param>
/// <param name="Scale">Frame-rate ratio (1 when the frame rates match).</param>
/// <param name="Offset">Offset in seconds.</param>
/// <param name="Confidence">0 to 1: how clearly the answer stands out, and how many stretches agree.</param>
/// <param name="Windows">Every stretch's own best match at the chosen ratio, for review.</param>
/// <param name="Explanation">A plain-language summary.</param>
public sealed record SyncModel(SyncStatus Status, double Scale, double Offset, double Confidence, IReadOnlyList<WindowMatch> Windows, string Explanation)
{
    /// <summary>Gets how far the combined peak stands out from the rest of the curve, in standard deviations.</summary>
    public double Distinctness { get; init; }

    /// <summary>
    /// Maps a subtitle time to the corrected time.
    /// </summary>
    /// <param name="subtitleTime">The original time.</param>
    /// <returns>The corrected time (unchanged unless <see cref="Status"/> is <see cref="SyncStatus.Corrected"/>).</returns>
    public TimeSpan Map(TimeSpan subtitleTime)
        => Status == SyncStatus.Corrected ? TimeSpan.FromSeconds((Scale * subtitleTime.TotalSeconds) + Offset) : subtitleTime;
}

/// <summary>
/// Combines stretches into one correction. For each common frame-rate ratio (the same rate, and PAL speed-up or slow-down
/// between 23.976, 24 and 25 fps) the stretches' score curves are added up, so every stretch's evidence counts even when
/// none is decisive on its own; the ratio with the highest combined peak wins. A correction is only made when that peak
/// clearly stands out (see <see cref="MinimumMargin"/> and <see cref="MinimumDistinctness"/>); anything less is left alone
/// for speech-to-text to settle, rather than risk moving good subtitles.
/// </summary>
public static class SyncSolver
{
    /// <summary>Frame-rate ratios tried: audio time per subtitle time.</summary>
    public static readonly IReadOnlyList<double> Scales =
        [1.0, 25 / (24000 / 1001.0), (24000 / 1001.0) / 25, 25 / 24.0, 24 / 25.0, 24 / (24000 / 1001.0), (24000 / 1001.0) / 24];

    /// <summary>A stretch agrees when its own best offset is within this many seconds of the combined answer.</summary>
    public const double Agreement = 0.3;

    /// <summary>
    /// How far the combined peak must stand out from any clearly different offset (average score). Calibrated on real
    /// videos: subtitles for a different episode or film never reached 0.02; correctly paired ones that cleared 0.03
    /// were always right.
    /// </summary>
    public const double MinimumMargin = 0.03;

    /// <summary>
    /// How many standard deviations the combined peak must stand above the rest of the curve. Subtitles for a different
    /// episode or film reached at most 7 together with a margin under 0.02; this and <see cref="MinimumMargin"/> must both hold.
    /// </summary>
    public const double MinimumDistinctness = 6;

    /// <summary>
    /// How much later the audio's speech starts register than subtitle lines start, in seconds, for subtitles known to be
    /// in sync: the detector reacts to the rise in loudness a little after speech begins. Measured as the median over real
    /// subtitles; subtracted from every offset found.
    /// </summary>
    public const double DetectorLag = 0.24;

    /// <summary>
    /// Offsets smaller than this (seconds) at the same frame rate are left alone. Line starts pin timing down to a few
    /// tenths of a second; finer corrections come from speech-to-text.
    /// </summary>
    public const double InSyncWithin = 0.5;

    /// <summary>
    /// Picks the correction.
    /// </summary>
    /// <param name="byScale">For each ratio in <see cref="Scales"/>, every stretch's curve at that ratio.</param>
    /// <param name="maxOffset">The largest offset searched (to turn curve positions into offsets).</param>
    /// <param name="detectorLag">The detector's lag to subtract (<see cref="DetectorLag"/> for real audio).</param>
    /// <returns>The correction.</returns>
    public static SyncModel Solve(IReadOnlyDictionary<double, IReadOnlyList<WindowCurve>> byScale, double maxOffset, double detectorLag = DetectorLag)
    {
        ArgumentNullException.ThrowIfNull(byScale);

        (double Scale, double Offset, double Peak, double Margin, IReadOnlyList<WindowCurve> Curves)? best = null;
        double z = 0;
        foreach (var (scale, curves) in byScale)
        {
            if (curves.Count == 0 || curves[0].Scores.Count == 0)
            {
                continue;
            }

            var sum = new double[curves[0].Scores.Count];
            foreach (var c in curves)
            {
                for (var k = 0; k < sum.Length && k < c.Scores.Count; k++)
                {
                    sum[k] += c.Scores[k] / curves.Count;
                }
            }

            var peak = Array.IndexOf(sum, sum.Max());
            if (best is null || sum[peak] > best.Value.Peak)
            {
                var mean = sum.Average();
                var sd = Math.Sqrt(sum.Average(v => (v - mean) * (v - mean)));
                best = (scale, OffsetSearch.OffsetAt(peak, maxOffset) - detectorLag, sum[peak], OffsetSearch.Margin(sum, peak), curves);
                z = sd <= 1e-12 ? 0 : (sum[peak] - mean) / sd;
            }
        }

        if (best is not { } b)
        {
            return new SyncModel(SyncStatus.Unreliable, 1, 0, 0, [], "No audio could be compared.");
        }

        var windows = b.Curves.Select(c => c.Match with { Offset = c.Match.Offset - detectorLag }).ToList();
        var agreeing = windows.Count(w => Math.Abs(w.Offset - b.Offset) <= Agreement);
        var confidence = Math.Clamp(Math.Min(1, b.Margin / (2 * MinimumMargin)) * Math.Min(1, z / (2 * MinimumDistinctness)), 0, 1);
        var sameRate = Math.Abs(b.Scale - 1) < 1e-9;
        var rate = sameRate ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" with a frame-rate change (×{b.Scale:0.00000})");
        var found = string.Create(CultureInfo.InvariantCulture, $"{b.Offset:+0.00;-0.00} s{rate}; {agreeing} of {windows.Count} stretches agree, margin {b.Margin:0.000}, z {z:0.0}");

        // Both must hold: a peak that stands out from the whole curve, and clearly beats every other offset
        if (b.Margin < MinimumMargin || z < MinimumDistinctness)
        {
            return new SyncModel(SyncStatus.Unreliable, 1, 0, confidence, windows, "No clear timing from the audio (best guess " + found + "); left for a closer check.") { Distinctness = z };
        }

        if (sameRate && Math.Abs(b.Offset) < InSyncWithin)
        {
            return new SyncModel(SyncStatus.InSync, 1, 0, confidence, windows, "Already in sync (" + found + ").") { Distinctness = z };
        }

        return new SyncModel(SyncStatus.Corrected, b.Scale, b.Offset, confidence, windows, "Shift of " + found + ".") { Distinctness = z };
    }
}
