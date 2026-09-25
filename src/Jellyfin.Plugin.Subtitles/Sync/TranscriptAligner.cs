using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.SpeechToText;

namespace Jellyfin.Plugin.Subtitles.Sync;

/// <summary>
/// A point where the subtitles and the audio are known to say the same thing.
/// </summary>
/// <param name="SubtitleTime">When the subtitles show the word, in seconds (a cue's start when the word begins a cue).</param>
/// <param name="AudioTime">When the word is spoken, in seconds from the start of the video.</param>
/// <param name="AtCueStart">Whether the word begins a cue (its subtitle time is exact, not estimated).</param>
public sealed record Anchor(double SubtitleTime, double AudioTime, bool AtCueStart);

/// <summary>
/// Lines subtitles up with a transcript of the audio. Runs of three words that occur exactly once in the subtitles are
/// found in the transcript; each gives an anchor (subtitle time, audio time). The anchors are fitted to one correction:
/// for each common frame-rate ratio the offsets are clustered, the densest cluster wins, and the precise offset comes
/// from anchors at cue starts (whose subtitle time is exact). This settles the cases the line-start stage can't (dense
/// dialogue over music) and pins timing down to about a tenth of a second.
/// </summary>
public static partial class TranscriptAligner
{
    /// <summary>Anchors within this many seconds of the cluster's middle count as agreeing.</summary>
    public const double Agreement = 0.5;

    /// <summary>The fewest agreeing anchors for a correction.</summary>
    public const int MinimumAnchors = 8;

    /// <summary>
    /// How much later the transcript's words start than well-timed cues (cues usually appear a moment before the first
    /// word is heard). Subtracted from every offset; measured on subtitles known to be in sync.
    /// </summary>
    public const double WordLag = 0.0;

    /// <summary>Offsets smaller than this (seconds) at the same frame rate mean the subtitles are already in sync.</summary>
    public const double InSyncWithin = 0.2;

    /// <summary>
    /// A simpler frame-rate ratio (earlier in <see cref="SyncSolver.Scales"/>) is kept if it fits at least this share of
    /// the anchors the best-fitting ratio does.
    /// </summary>
    public const double PreferSimplerWithin = 0.9;

    /// <summary>
    /// Finds anchors between subtitles and transcripts of stretches of the audio.
    /// </summary>
    /// <param name="subtitles">The subtitles.</param>
    /// <param name="transcripts">Each transcript with where its stretch starts in the video, in seconds.</param>
    /// <returns>The anchors.</returns>
    public static IReadOnlyList<Anchor> Anchors(SubtitleDocument subtitles, IEnumerable<(double Start, Transcript Transcript)> transcripts)
    {
        ArgumentNullException.ThrowIfNull(subtitles);
        ArgumentNullException.ThrowIfNull(transcripts);

        var subtitleWords = SubtitleWords(subtitles);
        var index = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i + 2 < subtitleWords.Count; i++)
        {
            var key = subtitleWords[i].Word + " " + subtitleWords[i + 1].Word + " " + subtitleWords[i + 2].Word;
            if (!index.TryGetValue(key, out var list))
            {
                index[key] = list = [];
            }

            list.Add(i);
        }

        var anchors = new List<Anchor>();
        foreach (var (start, transcript) in transcripts)
        {
            var heard = transcript.Words.Select(w => (Word: Normalise(w.Text), w.Start)).Where(w => w.Word.Length > 0).ToList();
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var j = 0; j + 2 < heard.Count; j++)
            {
                var key = heard[j].Word + " " + heard[j + 1].Word + " " + heard[j + 2].Word;
                seen[key] = seen.GetValueOrDefault(key) + 1;
            }

            for (var j = 0; j + 2 < heard.Count; j++)
            {
                var key = heard[j].Word + " " + heard[j + 1].Word + " " + heard[j + 2].Word;

                // Unique on both sides, so a repeated phrase ("I don't know") can't pair the wrong lines
                if (seen[key] == 1 && index.TryGetValue(key, out var at) && at.Count == 1)
                {
                    var w = subtitleWords[at[0]];
                    anchors.Add(new Anchor(w.Time, start + heard[j].Start, w.AtCueStart));
                }
            }
        }

        return anchors;
    }

    /// <summary>
    /// Fits the anchors to one correction.
    /// </summary>
    /// <param name="anchors">The anchors.</param>
    /// <param name="wordLag">The lag to subtract (<see cref="WordLag"/> for real transcripts).</param>
    /// <returns>The correction.</returns>
    public static SyncModel Solve(IReadOnlyList<Anchor> anchors, double wordLag = WordLag)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        if (anchors.Count < MinimumAnchors)
        {
            return new SyncModel(SyncStatus.Unreliable, 1, 0, 0, [], string.Create(CultureInfo.InvariantCulture, $"Only {anchors.Count} words could be matched between the subtitles and the audio; they may be for something else."));
        }

        // For each frame-rate ratio, the densest cluster of offsets
        var candidates = new List<(double Scale, List<Anchor> Agreeing)>();
        foreach (var scale in SyncSolver.Scales)
        {
            var offsets = anchors.Select(a => (Anchor: a, Offset: a.AudioTime - (scale * a.SubtitleTime))).OrderBy(x => x.Offset).ToList();
            List<Anchor> densest = [];
            for (int i = 0, j = 0; i < offsets.Count; i++)
            {
                while (offsets[i].Offset - offsets[j].Offset > 2 * Agreement)
                {
                    j++;
                }

                if (i - j + 1 > densest.Count)
                {
                    var middle = offsets[(i + j) / 2].Offset;
                    densest = [.. offsets.Where(o => Math.Abs(o.Offset - middle) <= Agreement).Select(o => o.Anchor)];
                }
            }

            candidates.Add((scale, densest));
        }

        // Ratios close to each other (24 vs 23.976 fps differ by 0.1 %) can't be told apart over a short video: the
        // simplest one (same rate first, then PAL) wins unless another fits clearly better
        var most = candidates.Max(c => c.Agreeing.Count);
        var chosen = candidates.First(c => c.Agreeing.Count >= PreferSimplerWithin * most);
        (double Scale, List<Anchor> Agreeing)? best = chosen;

        var b = best!.Value;
        var share = b.Agreeing.Count / (double)anchors.Count;
        var confidence = Math.Clamp(share * Math.Min(1, b.Agreeing.Count / (3.0 * MinimumAnchors)), 0, 1);
        if (b.Agreeing.Count < MinimumAnchors || share < 0.4)
        {
            return new SyncModel(SyncStatus.Unreliable, 1, 0, confidence, [], string.Create(CultureInfo.InvariantCulture, $"The matched words don't agree on one timing ({b.Agreeing.Count} of {anchors.Count} agree); the subtitles may be for another cut."));
        }

        // Precise offset: cue starts are exact; fall back to every agreeing word
        var exact = b.Agreeing.Where(a => a.AtCueStart).ToList();
        var basis = exact.Count >= 5 ? exact : b.Agreeing;
        var offset = Median(basis.Select(a => a.AudioTime - (b.Scale * a.SubtitleTime))) - wordLag;
        var sameRate = Math.Abs(b.Scale - 1) < 1e-9;
        var rate = sameRate ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" with a frame-rate change (×{b.Scale:0.00000})");
        var found = string.Create(CultureInfo.InvariantCulture, $"{offset:+0.00;-0.00} s{rate}; {b.Agreeing.Count} of {anchors.Count} matched words agree");
        if (sameRate && Math.Abs(offset) < InSyncWithin)
        {
            return new SyncModel(SyncStatus.InSync, 1, 0, confidence, [], "Already in sync (" + found + ").");
        }

        return new SyncModel(SyncStatus.Corrected, b.Scale, offset, confidence, [], "Shift of " + found + ".");
    }

    /// <summary>
    /// Normalises a word for matching: lower case, letters and digits only (apostrophes dropped, accents kept).
    /// </summary>
    /// <param name="word">A word.</param>
    /// <returns>The normalised word (empty if nothing is left).</returns>
    public static string Normalise(string word)
    {
        var sb = new StringBuilder(word?.Length ?? 0);
        foreach (var c in word ?? string.Empty)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }

    // The subtitles' spoken words, each with an estimated time: a cue's first word at its start, the rest spread through
    // the cue by their position in the text
    private static List<(string Word, double Time, bool AtCueStart)> SubtitleWords(SubtitleDocument document)
    {
        var words = new List<(string, double, bool)>();
        foreach (var cue in document.Cues)
        {
            var text = NotSpoken().Replace(SubtitleMarkup.ToPlainText(cue.Text), " ");
            var tokens = WordPattern().Matches(text).Select(m => (m.Index, Word: Normalise(m.Value))).Where(t => t.Word.Length > 0).ToList();
            if (tokens.Count == 0)
            {
                continue;
            }

            // The first spoken word is at the cue's start (labels and descriptions before it aren't heard); the rest are
            // spread through the cue by their position in the spoken text
            var from = tokens[0].Index;
            var length = Math.Max(1, text.TrimEnd().Length - from);
            var duration = (cue.End - cue.Start).TotalSeconds;
            for (var i = 0; i < tokens.Count; i++)
            {
                words.Add((tokens[i].Word, cue.Start.TotalSeconds + (duration * (tokens[i].Index - from) / length), i == 0));
            }
        }

        return words;
    }

    private static double Median(IEnumerable<double> values)
    {
        var v = values.Order().ToList();
        return v.Count == 0 ? 0 : v.Count % 2 == 1 ? v[v.Count / 2] : (v[(v.Count / 2) - 1] + v[v.Count / 2]) / 2;
    }

    // Sound descriptions, music symbols and upper-case speaker labels ("JOHN:") aren't spoken
    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|[♪♫#]|^\s*-?\s*[A-Z][A-Z .'\-]{1,24}:", RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NotSpoken();

    [GeneratedRegex(@"[\p{L}\p{Nd}]+(?:['’][\p{L}]+)?", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WordPattern();
}
