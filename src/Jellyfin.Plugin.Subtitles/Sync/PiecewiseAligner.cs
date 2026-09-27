using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.Subtitles.Discrepancy;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.SpeechToText;

namespace Jellyfin.Plugin.Subtitles.Sync;

/// <summary>
/// One stretch of a subtitle file with its own timing: every line starting at or after <see cref="From"/> (in the file's
/// time), up to the next section, is moved by <see cref="Offset"/> (after the frame-rate ratio shared by all sections).
/// </summary>
/// <param name="From">Where the section starts, in the file's time (seconds); 0 for the first. It lies in the gap between
/// two lines, so a line is never split.</param>
/// <param name="Offset">The offset in seconds: audio time = ratio × file time + offset.</param>
/// <param name="Anchors">How many matched words agree on it.</param>
public sealed record TimingSection(double From, double Offset, int Anchors)
{
    /// <summary>Gets where the section's first line shows in the video (seconds), for people; 0 for the first section.</summary>
    public double ShowsAt { get; init; }
}

/// <summary>
/// What fitting a subtitle to a full transcript found.
/// </summary>
public enum PiecewiseStatus
{
    /// <summary>The whole file agrees on one timing (see <see cref="PiecewiseFit.Sections"/>, which has one).</summary>
    OneTiming = 0,

    /// <summary>The timing jumps part-way: the subtitle was made for a different cut of the video.</summary>
    Sections,

    /// <summary>No trustworthy fit: too few matched words, or they don't agree in long stretches (another episode or
    /// language, or timing too irregular); the subtitle is left alone.</summary>
    Rejected,
}

/// <summary>
/// A piecewise timing correction: one frame-rate ratio for the whole file and an offset per section.
/// </summary>
/// <param name="Status">The outcome.</param>
/// <param name="Scale">The frame-rate ratio (audio time per file time).</param>
/// <param name="Sections">The sections, in time order (empty when rejected).</param>
/// <param name="NotInVideo">Lines (by position) covering content the video doesn't have: flagged, never moved.</param>
/// <param name="Anchors">How many words were matched between the subtitle and the transcript.</param>
/// <param name="Agreeing">How many of them agree with their section.</param>
/// <param name="Explanation">A plain-language summary.</param>
public sealed record PiecewiseFit(PiecewiseStatus Status, double Scale, IReadOnlyList<TimingSection> Sections, IReadOnlyList<int> NotInVideo, int Anchors, int Agreeing, string Explanation)
{
    /// <summary>Gets where each jump shows in the video (seconds): the first line after it appears then.</summary>
    public IReadOnlyList<double> JumpsAt { get; init; } = [];

    /// <summary>
    /// The section a line starting at a file time belongs to.
    /// </summary>
    /// <param name="sections">The sections, in time order.</param>
    /// <param name="fileTime">The line's start in the file (seconds).</param>
    /// <returns>The section.</returns>
    public static TimingSection SectionAt(IReadOnlyList<TimingSection> sections, double fileTime)
    {
        ArgumentNullException.ThrowIfNull(sections);
        if (sections.Count == 0)
        {
            throw new ArgumentException("No sections.", nameof(sections));
        }

        var s = sections[0];
        foreach (var x in sections)
        {
            if (x.From <= fileTime)
            {
                s = x;
            }
        }

        return s;
    }

    /// <summary>
    /// Moves every line by its section's timing: a line's start decides its section, and its end moves with it, so a line
    /// is never split; lines are then kept in time order.
    /// </summary>
    /// <param name="document">The subtitle.</param>
    /// <param name="scale">The frame-rate ratio.</param>
    /// <param name="sections">The sections.</param>
    /// <returns>The retimed subtitle.</returns>
    public static SubtitleDocument Retime(SubtitleDocument document, double scale, IReadOnlyList<TimingSection> sections)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(sections);
        static TimeSpan Clamp(double t) => TimeSpan.FromSeconds(Math.Max(0, Math.Round(t, 3)));
        var cues = document.Cues.Select(c =>
        {
            var s = SectionAt(sections, c.Start.TotalSeconds);
            var start = Clamp((scale * c.Start.TotalSeconds) + s.Offset);
            var end = Clamp((scale * c.End.TotalSeconds) + s.Offset);
            return c with { Start = start, End = end < start ? start : end };
        });
        return document with { Cues = [.. cues.OrderBy(c => c.Start)] };
    }

    /// <summary>
    /// The file's clock mapped onto the audio's, and back (for comparing a file whose sectioned timing waits for review).
    /// </summary>
    /// <param name="scale">The frame-rate ratio.</param>
    /// <param name="sections">The sections.</param>
    /// <returns>File to audio, and audio to file (seconds).</returns>
    public static (Func<double, double> ToAudio, Func<double, double> ToFile) Clocks(double scale, IReadOnlyList<TimingSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        var list = sections.ToList();
        double ToAudio(double t) => (scale * t) + SectionAt(list, t).Offset;
        double ToFile(double a)
        {
            // The section whose stretch of audio holds this time; otherwise the nearest one
            var best = list[0];
            var bestDistance = double.MaxValue;
            for (var i = 0; i < list.Count; i++)
            {
                var from = i == 0 ? double.NegativeInfinity : (scale * list[i].From) + list[i].Offset;
                var to = i + 1 < list.Count ? (scale * list[i + 1].From) + list[i].Offset : double.PositiveInfinity;
                var distance = a < from ? from - a : a > to ? a - to : 0;
                if (distance < bestDistance)
                {
                    best = list[i];
                    bestDistance = distance;
                }
            }

            return (a - best.Offset) / scale;
        }

        return (ToAudio, ToFile);
    }
}

/// <summary>
/// Settings of <see cref="PiecewiseAligner"/> (the defaults were calibrated on real videos).
/// </summary>
public sealed record PiecewiseOptions
{
    /// <summary>Gets how far (seconds) a matched word's offset may be from its section's to agree with it.</summary>
    public double Agreement { get; init; } = 0.5;

    /// <summary>Gets the fewest matched words for any fit.</summary>
    public int MinAnchors { get; init; } = 20;

    /// <summary>Gets the fewest agreeing matched words a section needs.</summary>
    public int MinSectionAnchors { get; init; } = 8;

    /// <summary>Gets the shortest stretch of the file (seconds, first to last agreeing word) a section needs.</summary>
    public double MinSectionSeconds { get; init; } = 20;

    /// <summary>Gets the smallest change of offset between neighbouring sections that counts as a jump (seconds).</summary>
    public double MinJump { get; init; } = 1;

    /// <summary>Gets what a jump costs, in matched words that don't agree (the fit's "complexity penalty").</summary>
    public double SwitchPenalty { get; init; } = 16;

    /// <summary>
    /// Gets the smallest fall in offset (seconds) for which lines are flagged as not in the video; smaller falls (a trimmed
    /// pause at an ad break) leave the lines either side to overlap a little, which the clean-up tidies.
    /// </summary>
    public double MinRemoved { get; init; } = 5;

    /// <summary>Gets the most sections.</summary>
    public int MaxSections { get; init; } = 8;

    /// <summary>Gets the smallest share of all matched words that must agree with their section.</summary>
    public double MinShare { get; init; } = 0.5;

    /// <summary>Gets the smallest share of the matched words within each section that must agree with it.</summary>
    public double MinSectionShare { get; init; } = 0.5;

    /// <summary>Gets the most candidate offsets considered per frame-rate ratio.</summary>
    public int MaxLevels { get; init; } = 16;

    /// <summary>Gets how far (seconds) around a line, on the audio's clock, its words are looked for in the transcript.</summary>
    public double LineWindow { get; init; } = 1.5;

    /// <summary>Gets how much (seconds) lines either side of a jump may overlap after moving.</summary>
    public double OverlapTolerance { get; init; } = 0.5;

    /// <summary>
    /// Gets what keeping a spoken line next to a part the video doesn't have costs, against flagging it, when little of
    /// it is heard (a share of its words): lines from the missing part rarely match what is heard.
    /// </summary>
    public double KeepCost { get; init; } = 0.25;
}

/// <summary>
/// Fits a subtitle to a full transcript of its video piece by piece, for subtitles made for a different cut (a scene
/// added or removed, a recap or cold open, ad breaks), which drift or jump part-way so that no single offset fits.
/// <para>
/// Every run of three words that occurs once in the subtitle and once in the transcript is an anchor (see
/// <see cref="TranscriptAligner.Anchors"/>). For each common frame-rate ratio, the anchors' offsets (audio time − ratio ×
/// subtitle time) are grouped into candidate offsets (dense clusters), and the anchors, in subtitle order, are assigned
/// to candidates by dynamic programming (Viterbi): an anchor that disagrees with its candidate costs 1, a change of
/// candidate costs <see cref="PiecewiseOptions.SwitchPenalty"/>. Runs too short or too thin to trust are folded into a
/// neighbour; neighbours closer than <see cref="PiecewiseOptions.MinJump"/> are merged. The ratio with the lowest cost
/// wins (the simpler one on a tie). The fit is rejected (the subtitle left alone) when there are too few anchors, too many
/// sections, or too few anchors agree overall or within a section: another episode or language gives few anchors, and
/// those don't line up.
/// </para>
/// <para>
/// Each jump is placed between two lines, never inside one, where the lines either side match what is heard under their
/// section's timing. When the subtitle has more than the video (the offset falls), the lines covering the missing part
/// are flagged, not moved: after the jump they would overlap the lines that follow. When the video has more (the offset
/// rises), the gap is simply left without lines.
/// </para>
/// </summary>
public static class PiecewiseAligner
{
    /// <summary>
    /// Fits a subtitle to a full transcript.
    /// </summary>
    /// <param name="subtitles">The subtitle.</param>
    /// <param name="heard">The transcript's words, on the video's clock.</param>
    /// <param name="duration">The video's length in seconds, if known (lines moved past its end are flagged).</param>
    /// <param name="options">Settings, or <c>null</c> for the defaults.</param>
    /// <returns>The fit.</returns>
    public static PiecewiseFit Fit(SubtitleDocument subtitles, IReadOnlyList<TranscribedWord> heard, double? duration = null, PiecewiseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(subtitles);
        ArgumentNullException.ThrowIfNull(heard);
        var o = options ?? new PiecewiseOptions();
        var anchors = TranscriptAligner.Anchors(subtitles, [(0, new Transcript(heard, null, string.Empty, string.Empty, 0))])
            .OrderBy(a => a.SubtitleTime).ThenBy(a => a.AudioTime).ToList();
        return FitAnchors(subtitles, anchors, heard, duration, o);
    }

    /// <summary>
    /// Fits a subtitle from anchors already found (see <see cref="Fit"/>).
    /// </summary>
    /// <param name="subtitles">The subtitle.</param>
    /// <param name="anchors">The anchors.</param>
    /// <param name="heard">The transcript's words (to place jumps between lines).</param>
    /// <param name="duration">The video's length in seconds, if known.</param>
    /// <param name="options">Settings.</param>
    /// <returns>The fit.</returns>
    public static PiecewiseFit FitAnchors(SubtitleDocument subtitles, IReadOnlyList<Anchor> anchors, IReadOnlyList<TranscribedWord> heard, double? duration, PiecewiseOptions options)
    {
        ArgumentNullException.ThrowIfNull(subtitles);
        ArgumentNullException.ThrowIfNull(anchors);
        ArgumentNullException.ThrowIfNull(heard);
        ArgumentNullException.ThrowIfNull(options);
        var o = options;
        var sorted = anchors.OrderBy(a => a.SubtitleTime).ThenBy(a => a.AudioTime).ToList();
        if (sorted.Count < o.MinAnchors)
        {
            return Rejected(sorted.Count, 0, string.Create(CultureInfo.InvariantCulture, $"Only {sorted.Count} words could be matched between the subtitle and a full transcript; it may be for something else."));
        }

        ScaleFit? best = null;
        foreach (var scale in SyncSolver.Scales)
        {
            var fit = FitScale(sorted, scale, o);

            // The simplest ratio (earlier in the list) is kept unless another clearly costs less
            if (best is null || fit.Cost < best.Cost - 1)
            {
                best = fit;
            }
        }

        var b = best!;
        var agreeing = b.Runs.Sum(r => r.Inliers.Count);
        if (b.Runs.Count == 0)
        {
            return Rejected(sorted.Count, agreeing, string.Create(CultureInfo.InvariantCulture, $"The {sorted.Count} matched words don't agree on any timing for long enough; the subtitle may be for something else."));
        }

        if (b.Runs.Count > o.MaxSections)
        {
            return Rejected(sorted.Count, agreeing, string.Create(CultureInfo.InvariantCulture, $"The timing changes too often ({b.Runs.Count} sections); left alone."));
        }

        if (agreeing < o.MinShare * sorted.Count)
        {
            return Rejected(sorted.Count, agreeing, string.Create(CultureInfo.InvariantCulture, $"Only {agreeing} of {sorted.Count} matched words agree with a timing; left alone."));
        }

        foreach (var r in b.Runs)
        {
            if (r.Inliers.Count < o.MinSectionShare * (r.Last - r.First + 1))
            {
                return Rejected(sorted.Count, agreeing, string.Create(CultureInfo.InvariantCulture, $"The matched words don't agree within a stretch ({r.Inliers.Count} of {r.Last - r.First + 1}); left alone."));
            }
        }

        // The precise offset of each section: cue starts are exact; otherwise every agreeing word
        var offsets = b.Runs.Select(r =>
        {
            var exact = r.Inliers.Where(i => sorted[i].AtCueStart).ToList();
            var basis = exact.Count >= 5 ? exact : r.Inliers;
            return Median(basis.Select(i => sorted[i].AudioTime - (b.Scale * sorted[i].SubtitleTime)));
        }).ToList();

        var cues = subtitles.Cues;
        if (b.Runs.Count == 1)
        {
            var one = new TimingSection(0, offsets[0], b.Runs[0].Inliers.Count);
            return new PiecewiseFit(PiecewiseStatus.OneTiming, b.Scale, [one], OutsideVideo(cues, b.Scale, [one], duration), sorted.Count, agreeing, string.Create(CultureInfo.InvariantCulture, $"The whole file agrees on one timing ({offsets[0]:+0.00;-0.00} s{Rate(b.Scale)}; {agreeing} of {sorted.Count} matched words agree)."));
        }

        var line = new LineMatcher(subtitles, heard, b.Scale, o.LineWindow);
        var sections = new List<TimingSection> { new(0, offsets[0], b.Runs[0].Inliers.Count) };
        var removed = new SortedSet<int>();
        var jumps = new List<double>();
        for (var k = 1; k < b.Runs.Count; k++)
        {
            var lastA = CueOf(cues, sorted[b.Runs[k - 1].Inliers[^1]].SubtitleTime);
            var firstB = CueOf(cues, sorted[b.Runs[k].Inliers[0]].SubtitleTime);
            var (i, j) = Boundary(cues, line, lastA, Math.Max(firstB, lastA + 1), offsets[k - 1], offsets[k], b.Scale, o);
            for (var x = i; x < j; x++)
            {
                removed.Add(x);
            }

            var from = j < cues.Count ? Between(cues, i) : cues[^1].End.TotalSeconds + 1;
            var shows = Math.Round(j < cues.Count ? (b.Scale * cues[j].Start.TotalSeconds) + offsets[k] : (b.Scale * from) + offsets[k], 3);
            sections.Add(new TimingSection(Math.Round(from, 3), offsets[k], b.Runs[k].Inliers.Count) { ShowsAt = shows });
            jumps.Add(shows);
        }

        foreach (var x in OutsideVideo(cues, b.Scale, sections, duration))
        {
            removed.Add(x);
        }

        var explanation = Explain(sections, jumps, b.Scale, removed.Count, agreeing, sorted.Count);
        return new PiecewiseFit(PiecewiseStatus.Sections, b.Scale, sections, [.. removed], sorted.Count, agreeing, explanation) { JumpsAt = jumps };
    }

    /// <summary>
    /// "12:40" or "1:02:40": a time in the video, for people.
    /// </summary>
    /// <param name="seconds">The time.</param>
    /// <returns>The words.</returns>
    public static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private static string Rate(double scale)
        => Math.Abs(scale - 1) < 1e-9 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" with a frame-rate change (×{scale:0.00000})");

    private static string Explain(List<TimingSection> sections, List<double> jumps, double scale, int removed, int agreeing, int anchors)
    {
        var sb = new StringBuilder("Timing jumps at ");
        for (var k = 1; k < sections.Count; k++)
        {
            if (k > 1)
            {
                sb.Append(k == sections.Count - 1 ? " and " : ", ");
            }

            var delta = sections[k].Offset - sections[k - 1].Offset;
            sb.Append(Clock(jumps[k - 1])).Append(string.Create(CultureInfo.InvariantCulture, $" ({(delta >= 0 ? "+" : "−")}{Math.Abs(delta):0.0} s)"));
        }

        sb.Append(": subtitle made for a different cut");
        sb.Append(Rate(scale)).Append('.');
        sb.Append(string.Create(CultureInfo.InvariantCulture, $" Sections: {string.Join("; ", sections.Select((s, k) => $"{(k == 0 ? "start" : "from " + Clock(s.ShowsAt))} {s.Offset:+0.00;-0.00} s ({s.Anchors} words)"))}; {agreeing} of {anchors} matched words agree."));
        if (removed > 0)
        {
            sb.Append(string.Create(CultureInfo.InvariantCulture, $" {removed} line{(removed == 1 ? " covers" : "s cover")} a part the video doesn't have: flagged for review (remove {(removed == 1 ? "it" : "them")}), not moved."));
        }

        return sb.ToString();
    }

    private static PiecewiseFit Rejected(int anchors, int agreeing, string why)
        => new(PiecewiseStatus.Rejected, 1, [], [], anchors, agreeing, why);

    // Lines moved before the video starts or past its end cover something it doesn't have
    private static List<int> OutsideVideo(IReadOnlyList<SubtitleCue> cues, double scale, IReadOnlyList<TimingSection> sections, double? duration)
    {
        var outside = new List<int>();
        for (var i = 0; i < cues.Count; i++)
        {
            var s = PiecewiseFit.SectionAt(sections, cues[i].Start.TotalSeconds);
            var start = (scale * cues[i].Start.TotalSeconds) + s.Offset;
            var end = (scale * cues[i].End.TotalSeconds) + s.Offset;
            if (end <= 0 || (duration is { } d && d > 0 && start >= d))
            {
                outside.Add(i);
            }
        }

        return outside;
    }

    // The line holding a word at a subtitle time: the last starting at or before it
    private static int CueOf(IReadOnlyList<SubtitleCue> cues, double time)
    {
        int lo = 0, hi = cues.Count - 1, found = 0;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (cues[mid].Start.TotalSeconds <= time + 1e-6)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found;
    }

    // Where a section starts: in the gap between the last line kept before the jump and the first after it (the flagged
    // lines, if any, go with the later section), never inside a line
    private static double Between(IReadOnlyList<SubtitleCue> cues, int i)
    {
        var next = cues[i].Start.TotalSeconds;
        var previous = i > 0 ? cues[i - 1].End.TotalSeconds : 0;
        var prevStart = i > 0 ? cues[i - 1].Start.TotalSeconds : -1;
        var mid = previous <= next ? (previous + next) / 2 : next;
        return Math.Max(mid, prevStart + 0.001);
    }

    // The jump between sections A (up to line lastA) and B (from line firstB): lines [lastA+1, i) go with A, [i, j) are
    // flagged, [j, firstB] go with B. Chosen so the lines either side match what is heard under their section's timing;
    // when the subtitle has more than the video (B's offset is lower), the flagged lines make room so that nothing
    // overlaps after moving. Ties: fewer flagged lines, then the longer pause.
    private static (int I, int J) Boundary(IReadOnlyList<SubtitleCue> cues, LineMatcher line, int lastA, int firstB, double offsetA, double offsetB, double scale, PiecewiseOptions o)
    {
        var lo = lastA + 1;
        var hi = firstB;
        var n = hi - lo;
        var sa = new double[n];
        var sb = new double[n];
        for (var x = 0; x < n; x++)
        {
            sa[x] = line.Score(lo + x, offsetA);
            sb[x] = line.Score(lo + x, offsetB);
        }

        var removing = offsetA - offsetB >= o.MinRemoved;
        if (removing)
        {
            // Where lines may be flagged, keeping one costs something unless it is heard
            for (var x = 0; x < n; x++)
            {
                if (line.Spoken(lo + x))
                {
                    sa[x] -= o.KeepCost;
                    sb[x] -= o.KeepCost;
                }
            }
        }

        // Prefix sums: A's score of lines [lo, lo+x), B's score of lines [lo+x, hi)
        var prefixA = new double[n + 1];
        for (var x = 0; x < n; x++)
        {
            prefixA[x + 1] = prefixA[x] + sa[x];
        }

        var suffixB = new double[n + 1];
        for (var x = n - 1; x >= 0; x--)
        {
            suffixB[x] = suffixB[x + 1] + sb[x];
        }

        (int I, int J) best = (hi, hi);
        var bestScore = double.NegativeInfinity;
        var bestRemoved = int.MaxValue;
        var bestPause = double.NegativeInfinity;
        for (var i = lo; i <= hi; i++)
        {
            // Never between two lines that start together
            if (i > 0 && i < cues.Count && i > lo && cues[i].Start <= cues[i - 1].Start)
            {
                continue;
            }

            var lastEnd = i > 0 ? cues[i - 1].End.TotalSeconds : double.NegativeInfinity;
            for (var j = i; j <= (removing ? hi : i); j++)
            {
                if (j < cues.Count && j > i && cues[j].Start <= cues[j - 1].Start)
                {
                    continue;
                }

                if (removing && j < cues.Count && i > 0 && (scale * cues[j].Start.TotalSeconds) + offsetB < (scale * lastEnd) + offsetA - o.OverlapTolerance)
                {
                    continue;
                }

                var score = prefixA[i - lo] + suffixB[j - lo];
                for (var x = i; x < j; x++)
                {
                    // Flagging a line that is heard costs what it matches
                    score -= Math.Max(0, Math.Max(sa[x - lo], sb[x - lo]) + (removing && line.Spoken(x) ? o.KeepCost : 0));
                }

                var pause = j < cues.Count && i > 0 ? cues[j].Start.TotalSeconds - lastEnd : 0;
                var better = score > bestScore + 1e-9
                    || (Math.Abs(score - bestScore) <= 1e-9 && (j - i < bestRemoved || (j - i == bestRemoved && pause > bestPause)));
                if (better)
                {
                    best = (i, j);
                    bestScore = score;
                    bestRemoved = j - i;
                    bestPause = pause;
                }
            }
        }

        return best;
    }

    private static ScaleFit FitScale(List<Anchor> anchors, double scale, PiecewiseOptions o)
    {
        var n = anchors.Count;
        var offsets = anchors.Select(a => a.AudioTime - (scale * a.SubtitleTime)).ToArray();
        var levels = Levels(offsets, o);
        if (levels.Count == 0)
        {
            return new ScaleFit(scale, [], n);
        }

        var states = Viterbi(offsets, levels, o);
        var runs = Runs(states, offsets, levels, o);

        // Runs too short or thin to trust are folded into a neighbour, the weakest first
        while (true)
        {
            var weak = runs.Where(r => !Valid(r, anchors, o)).OrderBy(r => r.Inliers.Count).FirstOrDefault();
            if (weak is null)
            {
                break;
            }

            var at = runs.IndexOf(weak);
            if (runs.Count == 1)
            {
                runs.Clear();
                break;
            }

            int Agree(int level) => Enumerable.Range(weak.First, weak.Last - weak.First + 1).Count(i => Math.Abs(offsets[i] - levels[level]) <= o.Agreement);
            var into = at == 0 ? runs[1].Level : at == runs.Count - 1 ? runs[at - 1].Level
                : Agree(runs[at - 1].Level) >= Agree(runs[at + 1].Level) ? runs[at - 1].Level : runs[at + 1].Level;
            for (var i = weak.First; i <= weak.Last; i++)
            {
                states[i] = into;
            }

            runs = Runs(states, offsets, levels, o);
        }

        // Neighbours closer than a jump are one section (the better-supported offset)
        var merged = true;
        while (merged && runs.Count > 1)
        {
            merged = false;
            for (var k = 1; k < runs.Count; k++)
            {
                if (Math.Abs(levels[runs[k].Level] - levels[runs[k - 1].Level]) < o.MinJump)
                {
                    var keep = runs[k].Inliers.Count > runs[k - 1].Inliers.Count ? runs[k].Level : runs[k - 1].Level;
                    for (var i = runs[k - 1].First; i <= runs[k].Last; i++)
                    {
                        states[i] = keep;
                    }

                    runs = Runs(states, offsets, levels, o);
                    merged = true;
                    break;
                }
            }
        }

        var cost = runs.Count == 0 ? n : n - runs.Sum(r => r.Inliers.Count) + (o.SwitchPenalty * (runs.Count - 1));
        return new ScaleFit(scale, runs, cost);
    }

    private static bool Valid(Run r, List<Anchor> anchors, PiecewiseOptions o)
        => r.Inliers.Count >= o.MinSectionAnchors && anchors[r.Inliers[^1]].SubtitleTime - anchors[r.Inliers[0]].SubtitleTime >= o.MinSectionSeconds;

    // Candidate offsets: the densest cluster (within ±Agreement of its median), then the densest of the rest, and so on
    private static List<double> Levels(double[] offsets, PiecewiseOptions o)
    {
        var remaining = offsets.Order().ToList();
        var levels = new List<double>();
        while (levels.Count < o.MaxLevels && remaining.Count >= o.MinSectionAnchors)
        {
            int bestFrom = 0, bestCount = 0;
            for (int i = 0, j = 0; i < remaining.Count; i++)
            {
                while (remaining[i] - remaining[j] > 2 * o.Agreement)
                {
                    j++;
                }

                if (i - j + 1 > bestCount)
                {
                    bestCount = i - j + 1;
                    bestFrom = j;
                }
            }

            if (bestCount < o.MinSectionAnchors)
            {
                break;
            }

            var middle = Median(remaining.Skip(bestFrom).Take(bestCount));
            levels.Add(middle);
            remaining = [.. remaining.Where(x => Math.Abs(x - middle) > o.Agreement)];
        }

        return levels;
    }

    // The cheapest assignment of anchors (in subtitle order) to candidate offsets: 1 per anchor that disagrees, the
    // switch penalty per change
    private static int[] Viterbi(double[] offsets, List<double> levels, PiecewiseOptions o)
    {
        var n = offsets.Length;
        var l = levels.Count;
        var back = new int[n][];
        back[0] = new int[l];
        var cost = new double[l];
        for (var k = 0; k < l; k++)
        {
            cost[k] = Math.Abs(offsets[0] - levels[k]) <= o.Agreement ? 0 : 1;
        }

        for (var i = 1; i < n; i++)
        {
            var bestPrev = 0;
            for (var k = 1; k < l; k++)
            {
                if (cost[k] < cost[bestPrev])
                {
                    bestPrev = k;
                }
            }

            var next = new double[l];
            back[i] = new int[l];
            for (var k = 0; k < l; k++)
            {
                var stay = cost[k];
                var jump = cost[bestPrev] + o.SwitchPenalty;
                var here = Math.Abs(offsets[i] - levels[k]) <= o.Agreement ? 0 : 1;
                if (stay <= jump)
                {
                    next[k] = stay + here;
                    back[i][k] = k;
                }
                else
                {
                    next[k] = jump + here;
                    back[i][k] = bestPrev;
                }
            }

            cost = next;
        }

        var states = new int[n];
        var last = 0;
        for (var k = 1; k < l; k++)
        {
            if (cost[k] < cost[last])
            {
                last = k;
            }
        }

        states[n - 1] = last;
        for (var i = n - 1; i > 0; i--)
        {
            states[i - 1] = back[i][states[i]];
        }

        return states;
    }

    private static List<Run> Runs(int[] states, double[] offsets, List<double> levels, PiecewiseOptions o)
    {
        var runs = new List<Run>();
        var start = 0;
        for (var i = 1; i <= states.Length; i++)
        {
            if (i == states.Length || states[i] != states[start])
            {
                var level = states[start];
                var inliers = Enumerable.Range(start, i - start).Where(x => Math.Abs(offsets[x] - levels[level]) <= o.Agreement).ToList();

                // A run is where its agreeing words are: its disagreeing ends belong to no one
                if (inliers.Count > 0)
                {
                    runs.Add(new Run(level, inliers[0], inliers[^1], inliers));
                }

                start = i;
            }
        }

        // Runs of the same offset separated only by disagreeing words are one
        for (var k = runs.Count - 1; k > 0; k--)
        {
            if (runs[k].Level == runs[k - 1].Level)
            {
                runs[k - 1] = new Run(runs[k].Level, runs[k - 1].First, runs[k].Last, [.. runs[k - 1].Inliers, .. runs[k].Inliers]);
                runs.RemoveAt(k);
            }
        }

        return runs;
    }

    private static double Median(IEnumerable<double> values)
    {
        var v = values.Order().ToList();
        return v.Count == 0 ? 0 : v.Count % 2 == 1 ? v[v.Count / 2] : (v[(v.Count / 2) - 1] + v[v.Count / 2]) / 2;
    }

    private sealed record Run(int Level, int First, int Last, List<int> Inliers);

    private sealed record ScaleFit(double Scale, List<Run> Runs, double Cost);

    // How well a line matches what is heard under a timing: the share of its different words heard around it
    private sealed class LineMatcher
    {
        private readonly IReadOnlyList<SubtitleCue> _cues;
        private readonly double[] _starts;
        private readonly string[] _words;
        private readonly double _scale;
        private readonly double _window;
        private readonly Dictionary<int, HashSet<string>> _lineWords = [];

        public LineMatcher(SubtitleDocument subtitles, IReadOnlyList<TranscribedWord> heard, double scale, double window)
        {
            _cues = subtitles.Cues;
            var sorted = heard.Select(w => (w.Start, Word: TranscriptAligner.Normalise(w.Text))).Where(w => w.Word.Length > 0).OrderBy(w => w.Start).ToList();
            _starts = [.. sorted.Select(w => w.Start)];
            _words = [.. sorted.Select(w => w.Word)];
            _scale = scale;
            _window = window;
        }

        public bool Spoken(int cue) => WordsOf(cue).Count > 0;

        public double Score(int cue, double offset)
        {
            var words = WordsOf(cue);

            if (words.Count == 0)
            {
                return 0;
            }

            var from = (_scale * _cues[cue].Start.TotalSeconds) + offset - _window;
            var to = (_scale * _cues[cue].End.TotalSeconds) + offset + _window;
            var i = Array.BinarySearch(_starts, from);
            i = i < 0 ? ~i : i;
            var found = new HashSet<string>(StringComparer.Ordinal);
            for (; i < _starts.Length && _starts[i] <= to; i++)
            {
                if (words.Contains(_words[i]))
                {
                    found.Add(_words[i]);
                }
            }

            return found.Count / (double)words.Count;
        }

        private HashSet<string> WordsOf(int cue)
        {
            if (!_lineWords.TryGetValue(cue, out var words))
            {
                words = [.. SpokenText.SubtitleWords(_cues[cue].Text).Select(TranscriptAligner.Normalise).Where(w => w.Length > 0)];
                _lineWords[cue] = words;
            }

            return words;
        }
    }
}
