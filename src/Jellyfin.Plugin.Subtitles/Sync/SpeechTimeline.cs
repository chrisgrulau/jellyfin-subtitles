using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Subtitles.Formats;

namespace Jellyfin.Plugin.Subtitles.Sync;

/// <summary>
/// When a subtitle says someone is speaking: the times of its cues that carry dialogue, as a signal that can be compared
/// with the speech found in the audio. Cues that are only sound descriptions (<c>[door slams]</c>) or music (<c>♪</c>)
/// aren't speech and are left out.
/// </summary>
public sealed partial class SpeechTimeline
{
    private readonly (double Start, double End)[] _spans;

    private SpeechTimeline((double Start, double End)[] spans)
    {
        _spans = spans;
        End = spans.Length == 0 ? 0 : spans.Max(s => s.End);
    }

    /// <summary>Gets the end of the last speech cue, in seconds.</summary>
    public double End { get; }

    /// <summary>Gets the number of speech cues.</summary>
    public int Count => _spans.Length;

    /// <summary>
    /// Builds the timeline of a subtitle document.
    /// </summary>
    /// <param name="document">The subtitles.</param>
    /// <returns>The timeline.</returns>
    public static SpeechTimeline From(SubtitleDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new SpeechTimeline([.. document.Cues
            .Where(c => c.End > c.Start && IsSpeech(c.Text))
            .Select(c => (Start: c.Start.TotalSeconds, End: c.End.TotalSeconds))
            .OrderBy(s => s.Start)]);
    }

    /// <summary>
    /// Whether cue text is dialogue rather than only a sound description or music.
    /// </summary>
    /// <param name="text">Cue text (with markup).</param>
    /// <returns><c>true</c> for dialogue.</returns>
    public static bool IsSpeech(string text)
    {
        var plain = NotSpoken().Replace(SubtitleMarkup.ToPlainText(text ?? string.Empty), " ");
        return plain.Any(char.IsLetterOrDigit);
    }

    /// <summary>
    /// Samples the timeline: for each frame, the share of it covered by speech, with subtitle time taken as
    /// <paramref name="origin"/> + frame × <paramref name="step"/> ÷ <paramref name="scale"/>.
    /// </summary>
    /// <param name="origin">Subtitle time of the first frame, in seconds.</param>
    /// <param name="step">Frame length in (audio) seconds.</param>
    /// <param name="count">Number of frames.</param>
    /// <param name="scale">How much faster the audio runs than the subtitles (1 for the same frame rate).</param>
    /// <returns>Values in [0, 1].</returns>
    public float[] Sample(double origin, double step, int count, double scale = 1)
    {
        var values = new float[Math.Max(0, count)];

        // First span that ends after the first frame starts (spans are sorted and don't overlap much)
        int lo = 0, hi = _spans.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_spans[mid].End <= origin)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        var span = Math.Max(0, lo - 8);
        for (var i = 0; i < values.Length; i++)
        {
            var a = origin + (i * step / scale);
            var b = origin + ((i + 1) * step / scale);
            while (span < _spans.Length && _spans[span].End <= a)
            {
                span++;
            }

            double covered = 0;
            for (var j = span; j < _spans.Length && _spans[j].Start < b; j++)
            {
                covered += Math.Max(0, Math.Min(b, _spans[j].End) - Math.Max(a, _spans[j].Start));
            }

            values[i] = (float)Math.Min(1, covered / (b - a));
        }

        return values;
    }

    /// <summary>
    /// Samples where lines start: a peak of 1 at each speech cue's start, falling to 0 over <paramref name="spread"/>
    /// seconds either side, with subtitle time taken as <paramref name="origin"/> + frame × <paramref name="step"/> ÷
    /// <paramref name="scale"/>.
    /// </summary>
    /// <param name="origin">Subtitle time of the first frame, in seconds.</param>
    /// <param name="step">Frame length in (audio) seconds.</param>
    /// <param name="count">Number of frames.</param>
    /// <param name="scale">How much faster the audio runs than the subtitles (1 for the same frame rate).</param>
    /// <param name="spread">How far either side of a start the peak reaches, in audio seconds.</param>
    /// <returns>Values in [0, 1].</returns>
    public float[] SampleStarts(double origin, double step, int count, double scale = 1, double spread = 0.09)
    {
        var values = new float[Math.Max(0, count)];
        foreach (var (start, _) in _spans)
        {
            // Frame position of this start: subtitle time s is at frame (s - origin) × scale / step
            var centre = (start - origin) * scale / step;
            var reach = spread / step;
            for (var f = (int)Math.Max(0, Math.Ceiling(centre - reach)); f <= Math.Min(values.Length - 1, Math.Floor(centre + reach)); f++)
            {
                values[f] = Math.Max(values[f], (float)(1 - (Math.Abs(f - centre) / (reach + 1))));
            }
        }

        return values;
    }

    // Sound descriptions, speaker labels' brackets and music symbols
    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|[♪♫#]", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NotSpoken();
}
