using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Subtitles.Formats;

namespace Jellyfin.Plugin.Subtitles.Discrepancy;

/// <summary>
/// One spoken word after normalisation, for comparing a subtitle with a transcript.
/// </summary>
/// <param name="Norm">The word compared: lower case, letters and digits only; a number is its digits (<c>twenty-five</c> and
/// <c>25</c> are both <c>25</c>); a contraction is split (<c>don't</c> is <c>do</c>, <c>not</c>).</param>
/// <param name="Surface">The word as written or heard (without surrounding punctuation).</param>
/// <param name="Word">The position of the (first) source word it came from.</param>
/// <param name="LastWord">The position of the last source word it came from (a number said in several words).</param>
/// <param name="Name">Whether it is written with a capital where a sentence doesn't start (a name, most likely).</param>
/// <param name="Number">Whether it is a number.</param>
/// <param name="Negation">Whether it is a negation (<c>not</c>, <c>never</c>, <c>no</c> …).</param>
public sealed record SpokenToken(string Norm, string Surface, int Word, int LastWord, bool Name, bool Number, bool Negation);

/// <summary>
/// Normalises subtitle text and transcript words the same way, so the comparison sees differences that matter (names,
/// numbers, negations, missing words) rather than spelling conventions: case and punctuation are ignored, English
/// contractions are split, numbers said in words become digits, and sound descriptions, music and speaker labels in a
/// subtitle aren't spoken. Deterministic and language-light: the English rules apply to English (or an unknown language);
/// other languages get case, punctuation and digits only. Names aren't told apart in German (every noun has a capital)
/// or in text that is all one case.
/// </summary>
public static partial class SpokenText
{
    /// <summary>The negations.</summary>
    public static readonly IReadOnlySet<string> Negations = new HashSet<string>(StringComparer.Ordinal)
    {
        "not", "no", "never", "nothing", "nobody", "none", "neither", "nor", "nowhere",
    };

    private static readonly Dictionary<string, string[]> Whole = new(StringComparer.Ordinal)
    {
        ["won't"] = ["will", "not"],
        ["can't"] = ["can", "not"],
        ["cannot"] = ["can", "not"],
        ["shan't"] = ["shall", "not"],
        ["ain't"] = ["is", "not"],
        ["let's"] = ["let", "us"],
        ["gonna"] = ["going", "to"],
        ["wanna"] = ["want", "to"],
        ["gotta"] = ["got", "to"],
        ["y'all"] = ["you", "all"],
        ["o'clock"] = ["oclock"],
    };

    private static readonly (string Suffix, string Word)[] Suffixes =
    [
        ("n't", "not"), ("'re", "are"), ("'ll", "will"), ("'ve", "have"), ("'m", "am"), ("'d", "would"), ("'s", "is"),
    ];

    private static readonly Dictionary<string, int> Units = new(StringComparer.Ordinal)
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9,
    };

    private static readonly Dictionary<string, int> Teens = new(StringComparer.Ordinal)
    {
        ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16,
        ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19,
    };

    private static readonly Dictionary<string, int> Tens = new(StringComparer.Ordinal)
    {
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50, ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
    };

    private static readonly Dictionary<string, long> Scales = new(StringComparer.Ordinal)
    {
        ["thousand"] = 1_000, ["million"] = 1_000_000, ["billion"] = 1_000_000_000,
    };

    private enum Part
    {
        None,
        Unit,
        TensUnit,
        Teen,
        Tens,
        Hundred,
        Scale,
        And,
    }

    /// <summary>
    /// Whether a subtitle line is music or a song (music notes or <c>#</c>): not compared with what is heard.
    /// </summary>
    /// <param name="text">The line's text (markup allowed).</param>
    /// <returns><c>true</c> for music.</returns>
    public static bool IsMusic(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Contains('♪', StringComparison.Ordinal) || text.Contains('♫', StringComparison.Ordinal) || text.TrimStart().StartsWith('#');
    }

    /// <summary>
    /// The spoken words of a subtitle line, as written: markup, sound descriptions (<c>[door slams]</c>,
    /// <c>(laughs)</c>), music notes and upper-case speaker labels (<c>JOHN:</c>) are removed; dialogue dashes too.
    /// </summary>
    /// <param name="text">The line's text (markup allowed).</param>
    /// <returns>The words, with the punctuation attached to them.</returns>
    public static IReadOnlyList<string> SubtitleWords(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var plain = NotSpoken().Replace(SubtitleMarkup.ToPlainText(text), " ");
        return [.. plain.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim('-', '–', '—')).Where(w => w.Any(char.IsLetterOrDigit))];
    }

    /// <summary>
    /// Whether names can be told by their capitals in these words: some are written in capitals and some aren't, and the
    /// language doesn't capitalise every noun (German).
    /// </summary>
    /// <param name="words">The words.</param>
    /// <param name="language">The two-letter language, if known.</param>
    /// <returns><c>true</c> if a capital can mark a name.</returns>
    public static bool CapitalsMarkNames(IEnumerable<string> words, string? language)
    {
        ArgumentNullException.ThrowIfNull(words);
        if (language is "de" or "lb")
        {
            return false;
        }

        bool upper = false, lower = false;
        foreach (var c in words.SelectMany(w => w))
        {
            upper |= char.IsUpper(c);
            lower |= char.IsLower(c);
            if (upper && lower)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Normalises words in order (a subtitle line's, or a transcript's).
    /// </summary>
    /// <param name="words">The words as written or heard, with punctuation attached.</param>
    /// <param name="language">The two-letter language, if known (English rules apply to <c>en</c> or unknown).</param>
    /// <param name="names">Whether a capital can mark a name (see <see cref="CapitalsMarkNames"/>).</param>
    /// <returns>The tokens.</returns>
    public static IReadOnlyList<SpokenToken> Tokens(IReadOnlyList<string> words, string? language, bool names)
    {
        ArgumentNullException.ThrowIfNull(words);
        var english = language is null or "en";
        var raw = new List<(string Norm, string Surface, int Word, bool Name, bool Ends)>();
        var sentenceStart = true;
        for (var w = 0; w < words.Count; w++)
        {
            var word = (words[w] ?? string.Empty).Replace('’', '\'').Replace('‘', '\'');
            var parts = WordPattern().Matches(word).Select(m => m.Value).ToList();
            var ends = EndsClause(word);
            for (var p = 0; p < parts.Count; p++)
            {
                var part = parts[p].Trim('\'', '.', ',');
                if (part.Length == 0)
                {
                    continue;
                }

                var capital = names && !sentenceStart && char.IsUpper(part[0]) && !IsPronounI(part);
                sentenceStart = false;
                foreach (var (norm, surface) in Split(part, english))
                {
                    raw.Add((norm, surface, w, capital, false));
                    capital = false;
                }
            }

            if (raw.Count > 0 && ends)
            {
                raw[^1] = raw[^1] with { Ends = true };
            }

            sentenceStart = EndsSentence(word) || (parts.Count == 0 && sentenceStart);
        }

        return english ? JoinNumbers(raw) : [.. raw.Select(r => Token(r.Norm, r.Surface, r.Word, r.Word, r.Name))];
    }

    /// <summary>
    /// Normalises one word the way <see cref="Tokens"/> does (without joining numbers said in several words).
    /// </summary>
    /// <param name="word">The word.</param>
    /// <returns>Its normalised form (empty when nothing is left).</returns>
    public static string Normalise(string word)
        => string.Concat(Split((word ?? string.Empty).Replace('’', '\'').Trim('\'', '.', ','), false).Select(p => p.Norm));

    private static SpokenToken Token(string norm, string surface, int word, int last, bool name)
    {
        var number = IsDigits(norm);
        return new SpokenToken(norm, surface, word, last, name && !number, number, Negations.Contains(norm));
    }

    // A word's parts: an English contraction split into its words; digits with separators joined ("1,000" is "1000")
    private static IEnumerable<(string Norm, string Surface)> Split(string part, bool english)
    {
        var lower = new string([.. part.Select(char.ToLowerInvariant)]);
        if (english && Whole.TryGetValue(lower, out var whole))
        {
            foreach (var w in whole)
            {
                yield return (w, part);
            }

            yield break;
        }

        if (english)
        {
            foreach (var (suffix, word) in Suffixes)
            {
                if (lower.Length > suffix.Length && lower.EndsWith(suffix, StringComparison.Ordinal))
                {
                    yield return (Letters(lower[..^suffix.Length]), part);
                    yield return (word, part);
                    yield break;
                }
            }
        }

        if (NumberPattern().IsMatch(lower))
        {
            var digits = lower.Replace(",", string.Empty, StringComparison.Ordinal);
            yield return (digits.Contains('.', StringComparison.Ordinal) ? digits.TrimEnd('0').TrimEnd('.') : digits.TrimStart('0') is { Length: > 0 } d ? d : "0", part);
            yield break;
        }

        // "yes,no" or "U.S." : separators inside letters are dropped or split
        foreach (var piece in lower.Split(','))
        {
            var norm = Letters(piece);
            if (norm.Length > 0)
            {
                yield return (norm, part);
            }
        }
    }

    // English numbers said in words become digits: "twenty-five" 25, "one hundred and five" 105, "nineteen ninety" 1990,
    // "a thousand" 1000. A run ends at punctuation, so "two, three" stays two numbers.
    private static List<SpokenToken> JoinNumbers(List<(string Norm, string Surface, int Word, bool Name, bool Ends)> raw)
    {
        var result = new List<SpokenToken>(raw.Count);
        var i = 0;
        while (i < raw.Count)
        {
            var (end, value) = NumberAt(raw, i);
            if (end > i)
            {
                result.Add(new SpokenToken(value.ToString(CultureInfo.InvariantCulture), string.Join(' ', raw.Skip(i).Take(end - i).Select(r => r.Surface).Distinct()), raw[i].Word, raw[end - 1].Word, false, true, false));
                i = end;
                continue;
            }

            result.Add(Token(raw[i].Norm, raw[i].Surface, raw[i].Word, raw[i].Word, raw[i].Name));
            i++;
        }

        return result;
    }

    private static (int End, long Value) NumberAt(List<(string Norm, string Surface, int Word, bool Name, bool Ends)> raw, int from)
    {
        long total = 0, group = 0;
        var last = Part.None;
        var year = false;
        var i = from;
        if (raw[i].Norm == "a" && i + 1 < raw.Count && !raw[i].Ends && (raw[i + 1].Norm == "hundred" || Scales.ContainsKey(raw[i + 1].Norm)))
        {
            group = 1;
            last = Part.Unit;
            i++;
        }
        else if (raw[i].Norm == "zero")
        {
            return (i + 1, 0);
        }

        for (; i < raw.Count; i++)
        {
            var w = raw[i].Norm;
            if (Units.TryGetValue(w, out var unit))
            {
                if (last is Part.Unit or Part.TensUnit or Part.Teen)
                {
                    break;
                }

                group += unit;
                last = last == Part.Tens ? Part.TensUnit : Part.Unit;
            }
            else if (Teens.TryGetValue(w, out var teen) || Tens.TryGetValue(w, out teen))
            {
                var isTeen = teen < 20;
                if (last is Part.Unit or Part.TensUnit or Part.Teen or Part.Tens)
                {
                    // "nineteen ninety", "twenty twenty": a year said in pairs
                    if (year || total != 0 || group < 10 || group > 99 || last == Part.Unit)
                    {
                        break;
                    }

                    year = true;
                    group *= 100;
                }

                group += teen;
                last = isTeen ? Part.Teen : Part.Tens;
            }
            else if (w == "hundred" && (last is Part.Unit or Part.TensUnit or Part.Teen or Part.Tens) && group < 100 && !year)
            {
                group *= 100;
                last = Part.Hundred;
            }
            else if (Scales.TryGetValue(w, out var scale) && last is not (Part.None or Part.Scale or Part.And) && !year)
            {
                total += group * scale;
                group = 0;
                last = Part.Scale;
            }
            else if (w == "and" && (last is Part.Hundred or Part.Scale) && i + 1 < raw.Count && !raw[i].Ends
                && (Units.ContainsKey(raw[i + 1].Norm) || Teens.ContainsKey(raw[i + 1].Norm) || Tens.ContainsKey(raw[i + 1].Norm)))
            {
                last = Part.And;
            }
            else
            {
                break;
            }

            if (raw[i].Ends)
            {
                i++;
                break;
            }
        }

        // A lone "a" isn't a number; nor is an "and" left hanging
        return last is Part.None || (i == from + 1 && raw[from].Norm == "a") ? (from, 0) : (i, total + group);
    }

    private static bool IsDigits(string norm) => norm.Length > 0 && char.IsDigit(norm[0]) && norm.All(c => char.IsDigit(c) || c == '.');

    private static bool IsPronounI(string part) => part == "I" || part.StartsWith("I'", StringComparison.Ordinal);

    private static string Letters(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }

    private static bool EndsSentence(string word)
    {
        var bare = word.TrimEnd('"', '\'', '”', '’', ')', ']');
        return bare.EndsWith('.') || bare.EndsWith('?') || bare.EndsWith('!') || bare.EndsWith('…') || bare.EndsWith(':')
            || bare.EndsWith('。') || bare.EndsWith('？') || bare.EndsWith('！');
    }

    private static bool EndsClause(string word)
    {
        var bare = word.TrimEnd('"', '\'', '”', '’', ')', ']');
        return EndsSentence(word) || bare.EndsWith(',') || bare.EndsWith(';') || bare.EndsWith('-') || bare.EndsWith('–') || bare.EndsWith('—');
    }

    // Sound descriptions, music notes and upper-case speaker labels ("JOHN:") aren't spoken
    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|[♪♫]|^\s*-?\s*[A-Z][A-Z .'\-]{1,24}:", RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NotSpoken();

    [GeneratedRegex(@"[\p{L}\p{M}\p{Nd}]+(?:['.,][\p{L}\p{M}\p{Nd}]+)*'?", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"^\d{1,3}(?:,\d{3})+(?:\.\d+)?$|^\d+(?:\.\d+)?$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NumberPattern();
}
