using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.Generation;
using Jellyfin.Plugin.Subtitles.SpeechToText;

namespace Jellyfin.Plugin.Subtitles.Discrepancy;

/// <summary>
/// How a subtitle is compared with a full transcript.
/// </summary>
public sealed record DiscrepancyOptions
{
    /// <summary>Gets how far (seconds) a line's timing may be off and its words still be found in what is heard.</summary>
    public double Tolerance { get; init; } = 3.0;

    /// <summary>Gets the word confidence below which what was heard isn't trusted enough to flag a line (0 = trust all).</summary>
    public double MinConfidence { get; init; }

    /// <summary>Gets the two-letter language, if known (English rules for contractions and numbers apply to English).</summary>
    public string? Language { get; init; }

    /// <summary>Gets the shortest heard stretch, in seconds, that counts as a missing line.</summary>
    public double MissingMinSeconds { get; init; } = 1.5;

    /// <summary>Gets the fewest heard words that count as a missing line.</summary>
    public int MissingMinWords { get; init; } = 4;

    /// <summary>Gets the share of a line's heard words missing from it above which its words differ.</summary>
    public double MissingWordRatio { get; init; } = 0.5;

    /// <summary>Gets the fewest heard words missing from a line for its words to differ.</summary>
    public int MissingWordsMin { get; init; } = 4;

    /// <summary>Gets the fewest spoken words a line needs to be flagged as having nothing heard (shorter ones are interjections).</summary>
    public int MinExtraWords { get; init; } = 3;

    /// <summary>Gets the share of the subtitle's words that must be found in the transcript for any comparison to count.</summary>
    public double MinShared { get; init; } = 0.25;

    /// <summary>Gets the share of lines with nothing heard above which the transcript, not the subtitle, is taken to be at fault.</summary>
    public double MaxExtraShare { get; init; } = 0.25;

    /// <summary>Gets the stretches of audio the transcript covers (seconds), or <c>null</c> for the whole video.</summary>
    public IReadOnlyList<(double Start, double End)>? Coverage { get; init; }
}

/// <summary>
/// One difference between a subtitle and what is heard.
/// </summary>
/// <param name="Kind">What differs (see <see cref="DiscrepancyFinder"/>'s kinds).</param>
/// <param name="Cue">The line's position in the file, or -1 for a line heard but missing.</param>
/// <param name="AudioStart">Where it starts, in seconds of audio.</param>
/// <param name="AudioEnd">Where it ends, in seconds of audio.</param>
/// <param name="Heard">What was heard there (empty when nothing was).</param>
/// <param name="Suggestion">The suggested fix: the line's new text, or the text of a missing line; <c>null</c> for a line
/// with nothing heard (the fix is removing it).</param>
/// <param name="Reason">Why, in one sentence.</param>
/// <param name="Confidence">The speech-to-text confidence of the words it rests on, if the service gives one.</param>
public sealed record LineDiscrepancy(string Kind, int Cue, double AudioStart, double AudioEnd, string Heard, string? Suggestion, string Reason, double? Confidence);

/// <summary>
/// A heard word matched to a subtitle line, for calibrating confidence thresholds.
/// </summary>
/// <param name="Confidence">Its confidence.</param>
/// <param name="Flagged">Whether it differs from the line in a way that would be flagged (a name, number or negation).</param>
public readonly record struct WordSample(double Confidence, bool Flagged);

/// <summary>
/// What comparing a subtitle with a transcript found.
/// </summary>
/// <param name="Findings">The differences, in time order.</param>
/// <param name="Samples">The heard words matched to lines, with confidence (for calibration).</param>
/// <param name="Suppressed">Differences dropped because what was heard had too little confidence.</param>
/// <param name="Shared">The share of the subtitle's words found in the transcript.</param>
/// <param name="Problem">Why nothing could be compared, if so.</param>
public sealed record DiscrepancyReport(IReadOnlyList<LineDiscrepancy> Findings, IReadOnlyList<WordSample> Samples, int Suppressed, double Shared, string? Problem)
{
    /// <summary>Gets the number of findings of each kind.</summary>
    public IReadOnlyDictionary<string, int> Counts => Findings.GroupBy(f => f.Kind).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
}

/// <summary>
/// Compares a whole subtitle with a transcript of its whole video, deterministically (no AI):
/// <list type="bullet">
/// <item><b>Alignment.</b> Every subtitle word is looked for among the heard words within <see cref="DiscrepancyOptions.Tolerance"/>
/// of its line (the subtitle's timing is taken to be roughly right, after any known correction); the longest run of
/// matches in order on both sides wins (Hunt–Szymanski). Heard words between a line's matches are its words; others go to
/// the line whose time (moved by the offset of nearby matches) they fall in.</item>
/// <item><b>Missing line</b> (<see cref="MissingLine"/>): heard speech of at least 4 words and 1.5 s that no line is shown during.</item>
/// <item><b>Extra</b> (<see cref="Extra"/>): a line of at least 3 spoken words with nothing heard around it; sound
/// descriptions, music and short interjections are left alone.</item>
/// <item><b>Differs</b>: a line whose numbers (<see cref="Number"/>), negations (<see cref="Negation"/>) or names
/// (<see cref="Name"/>, capitalised words heard as something else) differ from what is heard, or more than half of whose
/// heard words are missing from it (<see cref="Words"/>). A number, name or negation found in the neighbouring lines or
/// heard next to them doesn't count (a line break in a different place).</item>
/// <item><b>Confidence.</b> A difference resting on heard words below <see cref="DiscrepancyOptions.MinConfidence"/> is dropped.</item>
/// </list>
/// If too few words are shared, or too many lines have nothing heard, the transcript rather than the subtitle is taken to
/// be at fault and nothing is flagged.
/// </summary>
public static partial class DiscrepancyFinder
{
    /// <summary>A line heard but missing from the subtitle.</summary>
    public const string MissingLine = "missing-line";

    /// <summary>A line with nothing heard around it.</summary>
    public const string Extra = "extra";

    /// <summary>A name differs.</summary>
    public const string Name = "name";

    /// <summary>A number differs.</summary>
    public const string Number = "number";

    /// <summary>A negation differs.</summary>
    public const string Negation = "negation";

    /// <summary>Most of the heard words are missing from the line.</summary>
    public const string Words = "words";

    /// <summary>The kinds, most important first.</summary>
    public static readonly IReadOnlyList<string> Kinds = [Negation, Number, Name, Words, MissingLine, Extra];

    private const double Pad = 0.5;
    private const double RunGap = 1.0;
    private const double OffsetWindow = 60;
    private const double LongestLine = 30;

    /// <summary>
    /// Compares a subtitle with a transcript.
    /// </summary>
    /// <param name="file">The subtitle.</param>
    /// <param name="transcript">The heard words, on the video's clock.</param>
    /// <param name="toAudio">Maps the file's times (seconds) to the audio's clock (identity when in sync).</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <returns>The findings.</returns>
    public static DiscrepancyReport Find(SubtitleDocument file, IReadOnlyList<TranscribedWord> transcript, Func<double, double> toAudio, DiscrepancyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(toAudio);
        var o = options ?? new DiscrepancyOptions();
        var words = TranscriptCues.SpeechWords(transcript);
        var heard = SpokenText.Tokens([.. words.Select(w => w.Text)], o.Language, SpokenText.CapitalsMarkNames(words.Select(w => w.Text), o.Language));
        var h = new Heard(words, heard);
        var lines = Lines(file, toAudio, o.Language);

        // Subtitle words in time order, each with the line it's in
        var s = new List<(int Line, SpokenToken Token, double Time)>();
        for (var l = 0; l < lines.Count; l++)
        {
            var line = lines[l];
            for (var k = 0; k < line.Tokens.Count; k++)
            {
                s.Add((l, line.Tokens[k], line.A0 + ((line.A1 - line.A0) * k / Math.Max(1, line.Tokens.Count))));
            }
        }

        var matches = Align(s, lines, h, o.Tolerance);
        var assigned = Enumerable.Repeat(-1, heard.Count).ToArray();
        foreach (var (i, j) in matches)
        {
            assigned[j] = s[i].Line;
        }

        var covered = lines.Select(l => Covered(l, o.Coverage)).ToArray();
        var total = s.Count(x => covered[x.Line]);
        var shared = total == 0 ? 0 : matches.Count(m => covered[s[m.I].Line]) / (double)total;
        if (total >= 20 && shared < o.MinShared)
        {
            return new DiscrepancyReport([], [], 0, shared, string.Create(CultureInfo.InvariantCulture, $"Only {shared:P0} of the subtitle's words were heard, too few to compare it line by line (another version or language, or a poor transcript); nothing flagged."));
        }

        // Heard words between a line's matches are that line's; the offset of nearby matches moves each line's time
        FillBetween(lines, assigned);
        Offsets(lines, matches.Select(m => (s[m.I].Time, h.Start[m.J] - s[m.I].Time)).OrderBy(x => x.Time).ToList(), o.Tolerance);
        AssignGaps(lines, assigned, h, Pad, o.Tolerance);
        AttachToEmpty(lines, assigned, h, o.Tolerance);

        var findings = new List<LineDiscrepancy>();
        var samples = new List<WordSample>();
        var suppressed = 0;
        var byLine = Enumerable.Range(0, lines.Count).Select(_ => new List<int>()).ToArray();
        for (var j = 0; j < assigned.Length; j++)
        {
            if (assigned[j] >= 0)
            {
                byLine[assigned[j]].Add(j);
            }
        }

        var extras = new List<LineDiscrepancy>();
        var speechLines = 0;
        for (var l = 0; l < lines.Count; l++)
        {
            var line = lines[l];
            if (!covered[l] || line.Tokens.Count == 0 || line.Music)
            {
                continue;
            }

            speechLines++;
            if (byLine[l].Count == 0)
            {
                if (line.Words >= o.MinExtraWords)
                {
                    extras.Add(new LineDiscrepancy(Extra, line.Index, line.B0, line.B1, string.Empty, null, string.Create(CultureInfo.InvariantCulture, $"Nothing was heard during this line or within {o.Tolerance:0.#} s of it: it may be extra, or belong elsewhere."), null));
                }

                continue;
            }

            var (finding, flagged, dropped) = Differs(file, lines, l, byLine, h, o);
            suppressed += dropped;
            if (finding is not null)
            {
                findings.Add(finding);
            }

            foreach (var j in byLine[l])
            {
                if (h.Confidence(j) is { } c)
                {
                    samples.Add(new WordSample(c, flagged.Contains(j)));
                }
            }
        }

        if (extras.Count > Math.Max(3, o.MaxExtraShare * speechLines))
        {
            return new DiscrepancyReport([], [], 0, shared, string.Create(CultureInfo.InvariantCulture, $"{extras.Count} of {speechLines} lines had nothing heard at their time, which points to the transcript (music, noise, another audio track) rather than the subtitle; nothing flagged."));
        }

        findings.AddRange(extras);
        foreach (var missing in MissingRuns(lines, assigned, h, o))
        {
            if (missing.Confidence is { } c && c < o.MinConfidence)
            {
                suppressed++;
                continue;
            }

            findings.Add(missing);
        }

        return new DiscrepancyReport([.. findings.OrderBy(f => f.AudioStart)], samples, suppressed, shared, null);
    }

    /// <summary>
    /// Makes heard words into a subtitle line: joined, the first letter a capital, wrapped into two lines where long.
    /// </summary>
    /// <param name="words">The words.</param>
    /// <returns>The text.</returns>
    public static string AsLine(IEnumerable<TranscribedWord> words)
    {
        var text = TranscriptCues.Join(words).Trim();
        if (text.Length == 0)
        {
            return text;
        }

        text = char.ToUpper(text[0], CultureInfo.InvariantCulture) + text[1..];
        return TranscriptCues.Wrap(text, CueRules.Default.MaxLineLength) ?? text;
    }

    /// <summary>
    /// Replaces a word (or words said together) in a line's text with another, keeping everything else (markup, line
    /// breaks) as it is.
    /// </summary>
    /// <param name="text">The line's text.</param>
    /// <param name="surface">The words to replace, as written (separated by spaces).</param>
    /// <param name="replacement">What to put instead.</param>
    /// <returns>The new text, or <c>null</c> if the words weren't found.</returns>
    public static string? ReplaceWords(string text, string surface, string replacement)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(surface);
        var pieces = surface.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape).ToList();
        if (pieces.Count == 0)
        {
            return null;
        }

        var pattern = new Regex(@"(?<![\p{L}\p{Nd}])" + string.Join(@"[\s\-]+", pieces) + @"(?![\p{L}\p{Nd}])", RegexOptions.None, TimeSpan.FromSeconds(1));
        var m = pattern.Match(text);
        return m.Success ? text[..m.Index] + replacement + text[(m.Index + m.Length)..] : null;
    }

    private static bool Covered(Line line, IReadOnlyList<(double Start, double End)>? coverage)
        => coverage is null || coverage.Any(r => line.A0 >= r.Start && line.A1 <= r.End);

    private static List<Line> Lines(SubtitleDocument file, Func<double, double> toAudio, string? language)
    {
        var lines = new List<Line>();
        foreach (var i in Enumerable.Range(0, file.Cues.Count).OrderBy(i => file.Cues[i].Start))
        {
            var cue = file.Cues[i];
            var spoken = SpokenText.SubtitleWords(cue.Text);
            var tokens = SpokenText.Tokens(spoken, language, SpokenText.CapitalsMarkNames(spoken, language));
            var a0 = toAudio(cue.Start.TotalSeconds);
            var a1 = Math.Max(a0, toAudio(cue.End.TotalSeconds));
            lines.Add(new Line(i, a0, a1, tokens, spoken.Count, SpokenText.IsMusic(cue.Text)));
        }

        return lines;
    }

    // The longest sequence of matches in order on both sides (Hunt–Szymanski: the pairs of equal words within the time
    // window, sorted by subtitle word and then heard word descending; the longest run increasing in heard word)
    private static List<(int I, int J)> Align(List<(int Line, SpokenToken Token, double Time)> s, List<Line> lines, Heard h, double tolerance)
    {
        var pairs = new List<(int I, int J)>();
        for (var i = 0; i < s.Count; i++)
        {
            var line = lines[s[i].Line];
            var from = h.FirstAtOrAfter(line.A0 - tolerance);
            var candidates = new List<int>();
            for (var j = from; j < h.Count && h.Start[j] <= line.A1 + tolerance; j++)
            {
                if (string.Equals(h.Tokens[j].Norm, s[i].Token.Norm, StringComparison.Ordinal))
                {
                    candidates.Add(j);
                }
            }

            candidates.Reverse();
            pairs.AddRange(candidates.Select(j => (i, j)));
        }

        var tails = new List<int>();
        var previous = new int[pairs.Count];
        for (var p = 0; p < pairs.Count; p++)
        {
            int lo = 0, hi = tails.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (pairs[tails[mid]].J < pairs[p].J)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            previous[p] = lo > 0 ? tails[lo - 1] : -1;
            if (lo == tails.Count)
            {
                tails.Add(p);
            }
            else
            {
                tails[lo] = p;
            }
        }

        var result = new List<(int I, int J)>();
        for (var p = tails.Count > 0 ? tails[^1] : -1; p >= 0; p = previous[p])
        {
            result.Add(pairs[p]);
        }

        result.Reverse();

        // Strictly increasing in heard word, and so in subtitle word (pairs of one subtitle word come in descending order)
        return result;
    }

    private static void FillBetween(List<Line> lines, int[] assigned)
    {
        var first = new int[lines.Count];
        var last = new int[lines.Count];
        Array.Fill(first, int.MaxValue);
        Array.Fill(last, -1);
        for (var j = 0; j < assigned.Length; j++)
        {
            if (assigned[j] >= 0)
            {
                first[assigned[j]] = Math.Min(first[assigned[j]], j);
                last[assigned[j]] = Math.Max(last[assigned[j]], j);
            }
        }

        for (var l = 0; l < lines.Count; l++)
        {
            for (var j = first[l] + 1; last[l] >= 0 && j < last[l]; j++)
            {
                if (assigned[j] < 0)
                {
                    assigned[j] = l;
                }
            }
        }
    }

    // Each line's offset: the median offset of the matches within a minute of it (clamped to the tolerance)
    private static void Offsets(List<Line> lines, List<(double Time, double Offset)> matched, double tolerance)
    {
        int from = 0, to = 0;
        foreach (var line in lines.OrderBy(l => l.A0))
        {
            while (from < matched.Count && matched[from].Time < line.A0 - OffsetWindow)
            {
                from++;
            }

            to = Math.Max(to, from);
            while (to < matched.Count && matched[to].Time <= line.A0 + OffsetWindow)
            {
                to++;
            }

            var offset = to > from ? Median(matched.Skip(from).Take(to - from).Select(m => m.Offset)) : 0;
            line.Offset = Math.Clamp(offset, -tolerance, tolerance);
        }
    }

    // Heard words left between lines' matches go to a line they are heard during (a little padding allowed), keeping order
    private static void AssignGaps(List<Line> lines, int[] assigned, Heard h, double pad, double tolerance)
    {
        var next = new int[assigned.Length + 1];
        next[assigned.Length] = lines.Count - 1;
        for (var j = assigned.Length - 1; j >= 0; j--)
        {
            next[j] = assigned[j] >= 0 ? assigned[j] : next[j + 1];
        }

        var before = -1;
        for (var j = 0; j < assigned.Length; j++)
        {
            if (assigned[j] >= 0)
            {
                before = assigned[j];
                continue;
            }

            var mid = h.Mid(j);
            var best = -1;
            var bestDistance = double.MaxValue;
            for (var l = Math.Max(Math.Max(0, before), FirstLineFrom(lines, mid - tolerance - LongestLine)); l <= next[j + 1]; l++)
            {
                var line = lines[l];
                if (line.A0 - tolerance - pad > mid)
                {
                    break;
                }

                var distance = Distance(line, mid);
                if (distance <= pad && distance < bestDistance && line.Tokens.Count > 0)
                {
                    best = l;
                    bestDistance = distance;
                }
            }

            if (best >= 0)
            {
                assigned[j] = best;
                before = best;
            }
        }
    }

    // A line with none of its words heard takes the unclaimed heard words within the tolerance that are nearer to it than
    // to its neighbours (its speech, heard differently)
    private static void AttachToEmpty(List<Line> lines, int[] assigned, Heard h, double tolerance)
    {
        // Only between the claimed words of the lines before and after it (order is kept)
        var lastBefore = new int[lines.Count + 1];
        var firstAfter = new int[lines.Count + 1];
        var first = new int[lines.Count];
        var last = new int[lines.Count];
        Array.Fill(first, int.MaxValue);
        Array.Fill(last, -1);
        for (var j = 0; j < assigned.Length; j++)
        {
            if (assigned[j] >= 0)
            {
                first[assigned[j]] = Math.Min(first[assigned[j]], j);
                last[assigned[j]] = Math.Max(last[assigned[j]], j);
            }
        }

        lastBefore[0] = -1;
        for (var l = 0; l < lines.Count; l++)
        {
            lastBefore[l + 1] = Math.Max(lastBefore[l], last[l]);
        }

        firstAfter[lines.Count] = assigned.Length;
        for (var l = lines.Count - 1; l >= 0; l--)
        {
            firstAfter[l] = Math.Min(firstAfter[l + 1], first[l]);
        }

        for (var l = 0; l < lines.Count; l++)
        {
            var line = lines[l];
            if (last[l] >= 0 || line.Tokens.Count == 0 || line.Music)
            {
                continue;
            }

            var hi = firstAfter[l + 1] - 1;
            for (var j = Math.Max(lastBefore[l] + 1, h.FirstAtOrAfter(line.B0 - tolerance - LongestLine)); j <= hi && j < assigned.Length; j++)
            {
                var mid = h.Mid(j);
                if (mid > line.B1 + tolerance)
                {
                    break;
                }

                if (assigned[j] >= 0 || mid < line.B0 - tolerance)
                {
                    continue;
                }

                var mine = Distance(line, mid);
                var nearer = false;
                for (var n = Math.Max(0, l - 2); n <= Math.Min(lines.Count - 1, l + 2); n++)
                {
                    nearer |= n != l && lines[n].Tokens.Count > 0 && Distance(lines[n], mid) < mine;
                }

                if (!nearer)
                {
                    assigned[j] = l;
                }
            }
        }
    }

    // The first line (in time order) starting at or after a time
    private static int FirstLineFrom(List<Line> lines, double time)
    {
        int lo = 0, hi = lines.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (lines[mid].A0 < time)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private static double Distance(Line line, double at) => at < line.B0 ? line.B0 - at : at > line.B1 ? at - line.B1 : 0;

    private static (LineDiscrepancy? Finding, HashSet<int> Flagged, int Suppressed) Differs(SubtitleDocument file, List<Line> lines, int l, List<int>[] byLine, Heard h, DiscrepancyOptions o)
    {
        var line = lines[l];
        var said = line.Tokens;
        var heardHere = byLine[l];
        var near = Enumerable.Range(Math.Max(0, l - 1), Math.Min(lines.Count, l + 2) - Math.Max(0, l - 1)).ToList();
        var nearSaid = near.SelectMany(n => lines[n].Tokens).Select(t => t.Norm).ToHashSet(StringComparer.Ordinal);
        var nearHeard = near.SelectMany(n => byLine[n]).Select(j => h.Tokens[j].Norm).ToHashSet(StringComparer.Ordinal);
        var (substitutions, inserted) = Compare(said, heardHere, h);
        var text = file.Cues[line.Index].Text;
        var kinds = new List<(string Kind, string Reason, double? Confidence, string? Suggestion, IReadOnlyList<int> Tokens)>();

        // Numbers
        var saidNumbers = said.Where(t => t.Number && !nearHeard.Contains(t.Norm)).ToList();
        var heardNumbers = heardHere.Where(j => h.Tokens[j].Number && !nearSaid.Contains(h.Tokens[j].Norm)).ToList();
        if (saidNumbers.Count + heardNumbers.Count > 0)
        {
            string? fix = saidNumbers.Count == 1 && heardNumbers.Count == 1 ? ReplaceWords(text, saidNumbers[0].Surface, h.Surface(heardNumbers[0])) : null;
            var reason = heardNumbers.Count > 0 && saidNumbers.Count > 0
                ? $"Heard {Quote(heardNumbers.Select(h.Surface))} where the line has {Quote(saidNumbers.Select(t => t.Surface))}."
                : heardNumbers.Count > 0 ? $"Heard {Quote(heardNumbers.Select(h.Surface))}, which the line doesn't have." : $"The line has {Quote(saidNumbers.Select(t => t.Surface))}, which wasn't heard.";
            kinds.Add((Number, reason, heardNumbers.Count > 0 ? heardNumbers.Min(h.Confidence) : h.Mean(heardHere), fix, heardNumbers));
        }

        // Negations (counted, here and with the neighbouring lines)
        int saidNot = said.Count(t => t.Negation), heardNot = heardHere.Count(j => h.Tokens[j].Negation);
        if (saidNot != heardNot
            && near.Sum(n => lines[n].Tokens.Count(t => t.Negation)) != near.Sum(n => byLine[n].Count(j => h.Tokens[j].Negation)))
        {
            var extraNot = inserted.Where(j => h.Tokens[j].Negation).ToList();
            var reason = string.Create(CultureInfo.InvariantCulture, $"The line has {saidNot} negation{(saidNot == 1 ? string.Empty : "s")} (not, never, no …) where {heardNot} {(heardNot == 1 ? "was" : "were")} heard.");
            kinds.Add((Negation, reason, heardNot > saidNot && extraNot.Count > 0 ? extraNot.Min(h.Confidence) : h.Mean(heardHere), null, extraNot));
        }

        // Names: a capitalised word heard as something else (not a spelling the neighbours have)
        var names = substitutions.Where(p => (p.Said.Name || h.Tokens[p.Heard].Name) && !p.Said.Number && !h.Tokens[p.Heard].Number
            && !nearHeard.Contains(p.Said.Norm) && !nearSaid.Contains(h.Tokens[p.Heard].Norm)).ToList();
        if (names.Count > 0)
        {
            string? fix = names.Count == 1 ? ReplaceWords(text, names[0].Said.Surface, h.Surface(names[0].Heard)) : null;
            var reason = "Heard " + string.Join(", ", names.Select(p => $"“{h.Surface(p.Heard)}” where the line has “{p.Said.Surface}”")) + ".";
            kinds.Add((Name, reason, names.Min(p => h.Confidence(p.Heard)), fix, [.. names.Select(p => p.Heard)]));
        }

        // Words: most of what was heard isn't in the line
        var missing = inserted.Where(j => !nearSaid.Contains(h.Tokens[j].Norm)).ToList();
        if (missing.Count >= o.MissingWordsMin && missing.Count > o.MissingWordRatio * heardHere.Count)
        {
            var reason = string.Create(CultureInfo.InvariantCulture, $"{missing.Count} of the {heardHere.Count} words heard aren't in the line.");
            kinds.Add((Words, reason, h.Mean(missing), null, []));
        }

        // Words behind names, numbers and negations flag the line (for calibration), whatever their confidence
        var flagged = kinds.Where(k => k.Kind != Words).SelectMany(k => k.Tokens).ToHashSet();
        var kept = kinds.Where(k => k.Confidence is not { } c || c >= o.MinConfidence).ToList();
        var dropped = kinds.Count - kept.Count;
        if (kept.Count == 0)
        {
            return (null, flagged, dropped);
        }

        var primary = kept.OrderBy(k => ((IList<string>)Kinds).IndexOf(k.Kind)).First();
        var heardText = h.Text(heardHere);
        var suggestion = kept.Count == 1 && primary.Suggestion is not null ? primary.Suggestion : heardText;
        var confidences = kept.Where(k => k.Confidence is not null).Select(k => k.Confidence!.Value).ToList();
        return (new LineDiscrepancy(primary.Kind, line.Index, line.B0, line.B1, heardText, string.Equals(suggestion, text, StringComparison.Ordinal) ? null : suggestion, string.Join(' ', kept.Select(k => k.Reason)), confidences.Count > 0 ? confidences.Min() : null), flagged, dropped);
    }

    // Aligns a line's words with the words heard for it (longest common subsequence): substitutions are the unmatched
    // words of a stretch paired in order; inserted are heard words not in the line
    private static (List<(SpokenToken Said, int Heard)> Substitutions, List<int> Inserted) Compare(IReadOnlyList<SpokenToken> said, List<int> heard, Heard h)
    {
        var n = said.Count;
        var m = heard.Count;
        var table = new int[n + 1][];
        for (var i = 0; i <= n; i++)
        {
            table[i] = new int[m + 1];
        }

        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                table[i][j] = string.Equals(said[i].Norm, h.Tokens[heard[j]].Norm, StringComparison.Ordinal) ? table[i + 1][j + 1] + 1 : Math.Max(table[i + 1][j], table[i][j + 1]);
            }
        }

        var substitutions = new List<(SpokenToken, int)>();
        var inserted = new List<int>();
        var dels = new List<SpokenToken>();
        var ins = new List<int>();
        void Close()
        {
            for (var k = 0; k < Math.Min(dels.Count, ins.Count); k++)
            {
                substitutions.Add((dels[k], ins[k]));
            }

            inserted.AddRange(ins);
            dels.Clear();
            ins.Clear();
        }

        int a = 0, b = 0;
        while (a < n || b < m)
        {
            if (a < n && b < m && string.Equals(said[a].Norm, h.Tokens[heard[b]].Norm, StringComparison.Ordinal))
            {
                Close();
                a++;
                b++;
            }
            else if (b < m && (a == n || table[a][b + 1] >= table[a + 1][b]))
            {
                ins.Add(heard[b++]);
            }
            else
            {
                dels.Add(said[a++]);
            }
        }

        Close();
        return (substitutions, inserted);
    }

    private static IEnumerable<LineDiscrepancy> MissingRuns(List<Line> lines, int[] assigned, Heard h, DiscrepancyOptions o)
    {
        var run = new List<int>();
        IEnumerable<LineDiscrepancy> Close()
        {
            if (run.Count > 0)
            {
                var first = h.Tokens[run[0]].Word;
                var last = h.Tokens[run[^1]].LastWord;
                var start = h.Words[first].Start;
                var end = h.Words[last].End;
                var count = last - first + 1;
                if (count >= o.MissingMinWords && end - start >= o.MissingMinSeconds && !lines.Any(l => l.Tokens.Count > 0 && l.B0 < end && l.B1 > start))
                {
                    var next = lines.Where(l => l.B0 >= end).Select(l => l.B0).DefaultIfEmpty(double.MaxValue).Min();
                    var shown = Math.Max(end, Math.Min(start + 1.0, next - 0.08));
                    var said = h.Text(run);
                    yield return new LineDiscrepancy(MissingLine, -1, start, shown, said, said, string.Create(CultureInfo.InvariantCulture, $"Heard {count} words over {end - start:0.0} s that no line shows."), h.Mean(run));
                }

                run.Clear();
            }
        }

        for (var j = 0; j < assigned.Length; j++)
        {
            var mid = h.Mid(j);
            var inside = lines.Any(l => l.Tokens.Count > 0 && mid >= l.B0 && mid <= l.B1);
            if (assigned[j] >= 0 || inside || (run.Count > 0 && h.Start[j] - h.End[run[^1]] > RunGap))
            {
                foreach (var f in Close())
                {
                    yield return f;
                }
            }

            if (assigned[j] < 0 && !inside)
            {
                run.Add(j);
            }
        }

        foreach (var f in Close())
        {
            yield return f;
        }
    }

    private static string Quote(IEnumerable<string> words) => string.Join(", ", words.Select(w => "“" + w + "”"));

    private static double Median(IEnumerable<double> values)
    {
        var v = values.Order().ToList();
        return v.Count == 0 ? 0 : v.Count % 2 == 1 ? v[v.Count / 2] : (v[(v.Count / 2) - 1] + v[v.Count / 2]) / 2;
    }

    // A subtitle line on the audio's clock
    private sealed class Line(int index, double a0, double a1, IReadOnlyList<SpokenToken> tokens, int words, bool music)
    {
        public int Index { get; } = index;

        public double A0 { get; } = a0;

        public double A1 { get; } = a1;

        public IReadOnlyList<SpokenToken> Tokens { get; } = tokens;

        public int Words { get; } = words;

        public bool Music { get; } = music;

        public double Offset { get; set; }

        public double B0 => A0 + Offset;

        public double B1 => A1 + Offset;
    }

    // The heard words and their tokens
    private sealed class Heard(IReadOnlyList<TranscribedWord> words, IReadOnlyList<SpokenToken> tokens)
    {
        public IReadOnlyList<TranscribedWord> Words { get; } = words;

        public IReadOnlyList<SpokenToken> Tokens { get; } = tokens;

        public double[] Start { get; } = [.. tokens.Select(t => words[t.Word].Start)];

        public double[] End { get; } = [.. tokens.Select(t => words[t.LastWord].End)];

        public int Count => Tokens.Count;

        public double Mid(int j) => (Start[j] + End[j]) / 2;

        public double? Confidence(int j)
        {
            double? least = null;
            for (var w = Tokens[j].Word; w <= Tokens[j].LastWord; w++)
            {
                if (Words[w].Confidence is { } c)
                {
                    least = least is { } l ? Math.Min(l, c) : c;
                }
            }

            return least;
        }

        public double? Mean(IEnumerable<int> tokens)
        {
            var known = tokens.Select(Confidence).OfType<double>().ToList();
            return known.Count > 0 ? known.Average() : null;
        }

        public string Surface(int j)
            => string.Join(' ', Enumerable.Range(Tokens[j].Word, Tokens[j].LastWord - Tokens[j].Word + 1).Select(w => Words[w].Text)).Trim().TrimEnd('.', ',', '!', '?', ';', ':', '"', '”').TrimStart('"', '“');

        public string Text(List<int> tokens)
        {
            if (tokens.Count == 0)
            {
                return string.Empty;
            }

            var first = tokens.Min(j => Tokens[j].Word);
            var last = tokens.Max(j => Tokens[j].LastWord);
            return AsLine(Words.Skip(first).Take(last - first + 1));
        }

        public int FirstAtOrAfter(double time)
        {
            int lo = 0, hi = Start.Length;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (Start[mid] < time)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            return lo;
        }
    }
}
