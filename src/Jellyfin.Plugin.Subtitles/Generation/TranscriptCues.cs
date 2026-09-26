using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.SpeechToText;

namespace Jellyfin.Plugin.Subtitles.Generation;

/// <summary>
/// The rules for subtitle lines built from a transcript. The defaults follow common subtitling guidance: at most two
/// lines of 42 characters, shown for 1 to 7 seconds, at most about 20 characters a second, with a short gap between lines.
/// </summary>
public sealed record CueRules
{
    /// <summary>Gets the default rules.</summary>
    public static CueRules Default { get; } = new();

    /// <summary>Gets the longest line, in characters (a cue has at most two lines).</summary>
    public int MaxLineLength { get; init; } = 42;

    /// <summary>Gets the shortest time a cue is shown, in seconds, where the next cue leaves room.</summary>
    public double MinDuration { get; init; } = 1.0;

    /// <summary>Gets the longest time a cue is shown, in seconds.</summary>
    public double MaxDuration { get; init; } = 7.0;

    /// <summary>Gets the pause between words, in seconds, that always starts a new cue.</summary>
    public double PauseBreak { get; init; } = 0.6;

    /// <summary>Gets the fastest reading speed, in characters a second, that cues are lengthened towards where there is room.</summary>
    public double MaxCharsPerSecond { get; init; } = 20;

    /// <summary>Gets the shortest gap between one cue's end and the next one's start, in seconds.</summary>
    public double MinGap { get; init; } = 0.08;

    /// <summary>Gets how many characters a cue needs before a sentence end closes it (so "Yes. I know." can share one).</summary>
    public int MinSentenceCue { get; init; } = 12;
}

/// <summary>
/// Turns timed words from speech-to-text into subtitle cues (a pure function, so it is tested on its own). Words are
/// grouped greedily: a pause, a sentence end, the two-line limit or the longest duration starts a new cue; a cue cut by
/// length or duration is split after a comma or similar in its second half where there is one. Each cue starts with its
/// first word and ends with its last, then is lengthened into the following silence towards the minimum duration and the
/// reading speed, never closer than the minimum gap to the next cue.
/// </summary>
public static class TranscriptCues
{
    private const int MinShownMs = 250;

    /// <summary>
    /// The words that are speech: sound descriptions (<c>[Music]</c>, <c>(laughs)</c>, several words in brackets), music
    /// notes and anything without a letter or digit are dropped. Times are made to run forwards.
    /// </summary>
    /// <param name="words">The words, in order.</param>
    /// <returns>The spoken words.</returns>
    public static IReadOnlyList<TranscribedWord> SpeechWords(IReadOnlyList<TranscribedWord> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        var result = new List<TranscribedWord>(words.Count);
        var inside = false;
        foreach (var w in words)
        {
            var text = (w.Text ?? string.Empty).Trim();
            var opens = text.StartsWith('[') || text.StartsWith('(');
            var closes = text.Contains(']', StringComparison.Ordinal) || text.Contains(')', StringComparison.Ordinal);
            var described = inside || opens;
            inside = described && !closes;
            if (described || text.Contains('♪', StringComparison.Ordinal) || text.Contains('♫', StringComparison.Ordinal) || !text.Any(char.IsLetterOrDigit)
                || !double.IsFinite(w.Start) || !double.IsFinite(w.End))
            {
                continue;
            }

            var start = Math.Max(Math.Max(0, w.Start), result.Count > 0 ? result[^1].Start : 0);
            result.Add(w with { Text = text, Start = start, End = Math.Max(start, w.End) });
        }

        return result;
    }

    /// <summary>
    /// Builds cues from timed words.
    /// </summary>
    /// <param name="words">The words, in order (sound descriptions are dropped, see <see cref="SpeechWords"/>).</param>
    /// <param name="rules">The rules, or <c>null</c> for <see cref="CueRules.Default"/>.</param>
    /// <returns>The cues, in time order, never overlapping.</returns>
    public static IReadOnlyList<SubtitleCue> Build(IReadOnlyList<TranscribedWord> words, CueRules? rules = null)
    {
        var r = rules ?? CueRules.Default;
        var groups = Group(SpeechWords(words), r);
        return Time(groups, r);
    }

    /// <summary>
    /// Wraps text into one line, or two lines of at most <paramref name="maxLine"/> characters split at a space (as even as
    /// possible, preferring a split after punctuation).
    /// </summary>
    /// <param name="text">The text, on one line.</param>
    /// <param name="maxLine">The longest line.</param>
    /// <returns>The text with its lines separated by <c>\n</c>, or <c>null</c> if it doesn't fit in two lines.</returns>
    public static string? Wrap(string text, int maxLine)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= maxLine)
        {
            return text;
        }

        int? best = null;
        var bestScore = double.MaxValue;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != ' ' || i > maxLine || text.Length - i - 1 > maxLine)
            {
                continue;
            }

            var score = Math.Abs(i - (text.Length - i - 1)) - (i > 0 && ",.;:?!".Contains(text[i - 1], StringComparison.Ordinal) ? 8 : 0);
            if (score < bestScore)
            {
                bestScore = score;
                best = i;
            }
        }

        return best is { } at ? text[..at] + "\n" + text[(at + 1)..] : null;
    }

    /// <summary>
    /// Joins words into one line of text.
    /// </summary>
    /// <param name="words">The words.</param>
    /// <returns>The text.</returns>
    public static string Join(IEnumerable<TranscribedWord> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        var sb = new StringBuilder();
        foreach (var w in words)
        {
            if (sb.Length > 0 && !(IsCjk(sb[^1]) && w.Text.Length > 0 && IsCjk(w.Text[0])))
            {
                sb.Append(' ');
            }

            sb.Append(w.Text);
        }

        return sb.ToString();
    }

    // Chinese, Japanese and Korean words are written without spaces between them
    private static bool IsCjk(char c) => c is (>= '぀' and <= 'ヿ') or (>= '㐀' and <= '鿿') or (>= '가' and <= '힯') or (>= '＀' and <= '￯');

    private static bool EndsSentence(string word)
    {
        var bare = word.TrimEnd('"', '\'', '\u201d', '\u2019', ')');
        return bare.EndsWith('.') || bare.EndsWith('?') || bare.EndsWith('!') || bare.EndsWith('\u2026');
    }

    private static bool EndsClause(string word) => word.EndsWith(',') || word.EndsWith(';') || word.EndsWith(':') || word.EndsWith('—') || word.EndsWith('–');

    private static List<List<TranscribedWord>> Group(IReadOnlyList<TranscribedWord> words, CueRules r)
    {
        var groups = new List<List<TranscribedWord>>();
        var cur = new List<TranscribedWord>();
        foreach (var w in words)
        {
            while (cur.Count > 0)
            {
                var prev = cur[^1];
                var soft = w.Start - prev.End > r.PauseBreak || (EndsSentence(prev.Text) && Join(cur).Length >= r.MinSentenceCue);
                var hard = w.End - cur[0].Start > r.MaxDuration || Wrap(Join([.. cur, w]), r.MaxLineLength) is null;
                if (!soft && !hard)
                {
                    break;
                }

                // Cut by length or duration: end the cue after a clause in its second half, and carry the rest on
                var clause = soft || cur.Count < 2 ? -1 : cur.FindLastIndex(cur.Count - 2, cur.Count - 1 - (cur.Count / 2), x => EndsClause(x.Text));
                if (clause >= 0)
                {
                    groups.Add(cur[..(clause + 1)]);
                    cur = cur[(clause + 1)..];
                }
                else
                {
                    groups.Add(cur);
                    cur = [];
                }
            }

            cur.Add(w);
        }

        if (cur.Count > 0)
        {
            groups.Add(cur);
        }

        return groups;
    }

    private static List<SubtitleCue> Time(List<List<TranscribedWord>> groups, CueRules r)
    {
        static long Ms(double seconds) => (long)Math.Round(seconds * 1000);
        var gap = Ms(r.MinGap);
        var starts = groups.Select(g => Ms(g[0].Start)).ToArray();
        var cues = new List<SubtitleCue>(groups.Count);
        for (var i = 0; i < groups.Count; i++)
        {
            var text = Join(groups[i]);
            var start = starts[i];
            var spoken = Math.Max(start, Ms(groups[i][^1].End));
            var limit = i + 1 < groups.Count ? starts[i + 1] - gap : long.MaxValue;

            // Towards the minimum duration and the reading speed, but never past the longest duration or the next cue
            var target = Math.Max(spoken, Math.Max(start + Ms(r.MinDuration), start + Ms(text.Length / r.MaxCharsPerSecond)));
            var end = Math.Min(Math.Min(target, start + Ms(r.MaxDuration)), limit);
            if (end < start + MinShownMs)
            {
                // Words packed closer than a readable cue: this one gets a moment, and the next starts after it
                end = start + MinShownMs;
                if (i + 1 < groups.Count)
                {
                    starts[i + 1] = Math.Max(starts[i + 1], end + gap);
                }
            }

            cues.Add(new SubtitleCue
            {
                Start = TimeSpan.FromMilliseconds(start),
                End = TimeSpan.FromMilliseconds(end),
                Text = Wrap(text, r.MaxLineLength) ?? text,
            });
        }

        return cues;
    }
}
