using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.SpeechToText;

namespace Jellyfin.Plugin.Subtitles.Sync;

/// <summary>
/// A phrase heard in the audio.
/// </summary>
/// <param name="Index">Its position in the list sent.</param>
/// <param name="Start">When it starts, in seconds of audio.</param>
/// <param name="Text">What was heard.</param>
public sealed record HeardPhrase(int Index, double Start, string Text);

/// <summary>
/// A subtitle line.
/// </summary>
/// <param name="Index">Its position in the list sent.</param>
/// <param name="Start">When it starts, in seconds of subtitle time.</param>
/// <param name="Text">Its text (plain).</param>
public sealed record CueLine(int Index, double Start, string Text);

/// <summary>
/// A heard phrase and a subtitle line that say the same thing.
/// </summary>
/// <param name="Phrase">The phrase's index.</param>
/// <param name="Cue">The line's index.</param>
public sealed record LinePair(int Phrase, int Cue);

/// <summary>
/// What a line matcher concluded.
/// </summary>
public enum LineVerdict
{
    /// <summary>It couldn't tell (or wasn't asked).</summary>
    Unsure = 0,

    /// <summary>The subtitles say what is said, in other words or another language.</summary>
    SameContent,

    /// <summary>The subtitles are for something else.</summary>
    Different,
}

/// <summary>
/// A line matcher's answer.
/// </summary>
/// <param name="Verdict">The verdict.</param>
/// <param name="Pairs">Phrases and lines that say the same thing (only with <see cref="LineVerdict.SameContent"/>).</param>
/// <param name="Note">Why, in words for the results list (empty when there is nothing worth saying).</param>
/// <param name="By">Who decided (for example the model), if anyone did.</param>
public sealed record LineMatch(LineVerdict Verdict, IReadOnlyList<LinePair> Pairs, string Note, string? By);

/// <summary>
/// Pairs heard phrases with subtitle lines by meaning rather than by exact words (for example the AI plugin), for
/// subtitles that are a translation or paraphrase of what is said, or dense dialogue exact words couldn't settle.
/// </summary>
public interface ILineMatcher
{
    /// <summary>
    /// Pairs phrases with lines.
    /// </summary>
    /// <param name="phrases">What was heard.</param>
    /// <param name="cues">The subtitle lines around the same stretches.</param>
    /// <param name="language">The subtitle's language (two letters), if known.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The answer; never throws for an unavailable or failed matcher.</returns>
    Task<LineMatch> MatchAsync(IReadOnlyList<HeardPhrase> phrases, IReadOnlyList<CueLine> cues, string? language, CancellationToken cancellationToken);
}

/// <summary>
/// Turns a line matcher's pairs into timing: each pair is an anchor (the line's start against the phrase's start), and
/// the usual solver must find one shift (and frame-rate ratio) most of them agree on.
/// </summary>
public static class MeaningAligner
{
    /// <summary>The fewest agreeing pairs that settle the timing.</summary>
    public const int MinimumPairs = 6;

    /// <summary>A pause this long (seconds) between heard words starts a new phrase.</summary>
    public const double PhraseGap = 0.7;

    /// <summary>The most words in one phrase.</summary>
    public const int MaxPhraseWords = 14;

    /// <summary>How far (seconds) either side of each heard stretch subtitle lines are offered, for shifted subtitles.</summary>
    public const double Margin = 150;

    /// <summary>The most lines offered.</summary>
    public const int MaxCues = 300;

    /// <summary>The longest line or phrase text sent.</summary>
    public const int MaxText = 150;

    /// <summary>
    /// Groups heard words into phrases (at pauses, sentence ends, or every <see cref="MaxPhraseWords"/> words).
    /// </summary>
    /// <param name="transcripts">The transcribed stretches and where each starts in the audio.</param>
    /// <returns>The phrases, in order.</returns>
    public static IReadOnlyList<HeardPhrase> Phrases(IEnumerable<(double Start, Transcript Transcript)> transcripts)
    {
        ArgumentNullException.ThrowIfNull(transcripts);
        var phrases = new List<HeardPhrase>();
        foreach (var (start, transcript) in transcripts.OrderBy(t => t.Start))
        {
            var words = new List<TranscribedWord>();
            void Flush()
            {
                var text = string.Join(' ', words.Select(w => w.Text.Trim()).Where(w => w.Length > 0));
                if (text.Length > 0)
                {
                    phrases.Add(new HeardPhrase(phrases.Count, Math.Round(start + words[0].Start, 2), Shorten(text)));
                }

                words.Clear();
            }

            foreach (var word in transcript.Words)
            {
                if (words.Count > 0 && (word.Start - words[^1].End > PhraseGap || words.Count >= MaxPhraseWords))
                {
                    Flush();
                }

                words.Add(word);
                if (word.Text.TrimEnd().EndsWith('.') || word.Text.TrimEnd().EndsWith('?') || word.Text.TrimEnd().EndsWith('!'))
                {
                    Flush();
                }
            }

            Flush();
        }

        return phrases;
    }

    /// <summary>
    /// The subtitle lines within <see cref="Margin"/> of the heard stretches (at most <see cref="MaxCues"/>).
    /// </summary>
    /// <param name="subtitles">The subtitles.</param>
    /// <param name="stretches">Where each heard stretch starts and how long it is, in seconds of audio.</param>
    /// <returns>The lines, in order.</returns>
    public static IReadOnlyList<CueLine> Cues(SubtitleDocument subtitles, IEnumerable<(double Start, double Length)> stretches)
    {
        ArgumentNullException.ThrowIfNull(subtitles);
        ArgumentNullException.ThrowIfNull(stretches);
        var windows = stretches.ToList();
        var lines = new List<CueLine>();
        foreach (var cue in subtitles.Cues.OrderBy(c => c.Start))
        {
            var at = cue.Start.TotalSeconds;
            if (!windows.Any(w => at >= w.Start - Margin && at <= w.Start + w.Length + Margin))
            {
                continue;
            }

            var text = SubtitleMarkup.ToPlainText(cue.Text).ReplaceLineEndings(" ").Trim();
            if (text.Length > 0)
            {
                lines.Add(new CueLine(lines.Count, Math.Round(at, 2), Shorten(text)));
                if (lines.Count == MaxCues)
                {
                    break;
                }
            }
        }

        return lines;
    }

    /// <summary>
    /// Anchors from pairs, ignoring any pair that names a phrase or line not offered, or reuses one.
    /// </summary>
    /// <param name="phrases">The phrases offered.</param>
    /// <param name="cues">The lines offered.</param>
    /// <param name="pairs">The matcher's pairs.</param>
    /// <returns>The anchors.</returns>
    public static IReadOnlyList<Anchor> Anchors(IReadOnlyList<HeardPhrase> phrases, IReadOnlyList<CueLine> cues, IEnumerable<LinePair> pairs)
    {
        ArgumentNullException.ThrowIfNull(phrases);
        ArgumentNullException.ThrowIfNull(cues);
        ArgumentNullException.ThrowIfNull(pairs);
        var usedPhrases = new HashSet<int>();
        var usedCues = new HashSet<int>();
        var anchors = new List<Anchor>();
        foreach (var pair in pairs)
        {
            if (pair.Phrase < 0 || pair.Phrase >= phrases.Count || pair.Cue < 0 || pair.Cue >= cues.Count
                || usedPhrases.Contains(pair.Phrase) || usedCues.Contains(pair.Cue))
            {
                continue;
            }

            usedPhrases.Add(pair.Phrase);
            usedCues.Add(pair.Cue);
            anchors.Add(new Anchor(cues[pair.Cue].Start, phrases[pair.Phrase].Start, true));
        }

        return anchors;
    }

    /// <summary>
    /// The timing the anchors agree on.
    /// </summary>
    /// <param name="anchors">Anchors from <see cref="Anchors"/>.</param>
    /// <param name="wordLag">Speech-to-text word-start lag.</param>
    /// <returns>The model (unreliable unless enough agree).</returns>
    public static SyncModel Solve(IReadOnlyList<Anchor> anchors, double wordLag = TranscriptAligner.WordLag)
        => TranscriptAligner.Solve(anchors, wordLag, MinimumPairs);

    private static string Shorten(string text) => text.Length > MaxText ? text[..MaxText] + "…" : text;
}
