using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Subtitles.Candidates;

/// <summary>
/// A writing system, as far as telling a subtitle's language apart needs.
/// </summary>
public enum Script
{
    /// <summary>Latin letters.</summary>
    Latin = 0,

    /// <summary>Cyrillic.</summary>
    Cyrillic,

    /// <summary>Greek.</summary>
    Greek,

    /// <summary>Arabic (Arabic, Persian, Urdu …).</summary>
    Arabic,

    /// <summary>Hebrew (Hebrew, Yiddish).</summary>
    Hebrew,

    /// <summary>Chinese characters and Japanese kana (Chinese and Japanese mix them, so they aren't told apart).</summary>
    Cjk,

    /// <summary>Korean Hangul.</summary>
    Hangul,

    /// <summary>Thai.</summary>
    Thai,

    /// <summary>Devanagari (Hindi, Marathi, Nepali …).</summary>
    Devanagari,
}

/// <summary>
/// Guesses the language of subtitle text: its writing system for any language, and for some languages written in Latin
/// letters its most common short words. Cheap and good enough to catch a subtitle labelled with the wrong language; it is
/// not a general-purpose detector. Codes are two-letter (ISO 639-1).
/// </summary>
public static partial class LanguageGuesser
{
    private static readonly Dictionary<string, HashSet<string>> CommonWords = new(StringComparer.Ordinal)
    {
        ["en"] = Set("the you i to a and it is that what of in me this we he not have be your do was are no for on my don't know"),
        ["fr"] = Set("de je le la les et est pas vous que un une il ce en ne on tu qui pour mais c'est moi me qu'est-ce"),
        ["es"] = Set("de que el la y no es en a lo un los por qué me se una con para te está mi eso"),
        ["de"] = Set("ich die und der nicht du das ist es sie zu ein was wir mit sie den mir ja auf hast"),
        ["it"] = Set("di che non è e il la un a per mi ti ho sono lo una cosa questo io ma se sei"),
        ["pt"] = Set("que não de o a é e um eu você se do da me uma para isso está com os"),
        ["nl"] = Set("de het een ik je niet is dat van en wat we zijn hij er maar op te met"),
        ["sv"] = Set("och det att i en jag är som på du inte med han för har vi de till var den så vad kan om mig"),
        ["da"] = Set("og det at i en jeg er som på du ikke med han for har vi de til var den så hvad kan om mig"),
        ["no"] = Set("og det at i en jeg er som på du ikke med han for har vi de til var den så hva kan om meg"),
        ["pl"] = Set("nie to się w i na że jest co jak z ja ty tak do mi mnie go już ale czy o by"),
        ["fi"] = Set("on ja se ei en että hän mitä minä sinä olen tämä mutta kun no niin oli jos nyt vain"),
        ["tr"] = Set("bir ve bu da de ne için ben sen o çok mi mı var yok gibi ama evet hayır şey"),
    };

    // Languages too close to tell apart by their common words: a guess within one of these groups isn't a mismatch
    private static readonly string[][] Families = [["da", "no", "nb", "nn", "sv"]];

    private static readonly Dictionary<string, Script> Scripts = new(StringComparer.Ordinal)
    {
        ["ru"] = Script.Cyrillic, ["uk"] = Script.Cyrillic, ["be"] = Script.Cyrillic, ["bg"] = Script.Cyrillic, ["mk"] = Script.Cyrillic,
        ["kk"] = Script.Cyrillic, ["ky"] = Script.Cyrillic, ["tg"] = Script.Cyrillic, ["mn"] = Script.Cyrillic,
        ["el"] = Script.Greek,
        ["ar"] = Script.Arabic, ["fa"] = Script.Arabic, ["ur"] = Script.Arabic, ["ps"] = Script.Arabic,
        ["he"] = Script.Hebrew, ["yi"] = Script.Hebrew,
        ["zh"] = Script.Cjk, ["ja"] = Script.Cjk,
        ["ko"] = Script.Hangul,
        ["th"] = Script.Thai,
        ["hi"] = Script.Devanagari, ["mr"] = Script.Devanagari, ["ne"] = Script.Devanagari,
    };

    /// <summary>
    /// Guesses the language from the common words of the languages it knows (see <see cref="KnowsWordsOf"/>).
    /// </summary>
    /// <param name="plainText">Subtitle text without markup.</param>
    /// <returns>A two-letter code and the share of words that support it, or <c>null</c> when too little text or no
    /// clear winner (a close relative, such as Danish for Swedish, doesn't count against it).</returns>
    public static (string Language, double Share)? Guess(string plainText)
    {
        ArgumentNullException.ThrowIfNull(plainText);

        var words = Word().Matches(plainText.ToUpperInvariant()).Select(m => m.Value).ToList();
        if (words.Count < 30)
        {
            return null;
        }

        var ranked = CommonWords
            .Select(kv => (Language: kv.Key, Share: (double)words.Count(kv.Value.Contains) / words.Count))
            .OrderByDescending(x => x.Share)
            .ToList();

        // Must stand out: at least 15 % of all words, and clearly ahead of the best language outside its family
        var best = ranked[0];
        var runner = ranked.Skip(1).FirstOrDefault(x => !SameFamily(x.Language, best.Language));
        return best.Share >= 0.15 && best.Share >= runner.Share * 1.5 ? best : null;
    }

    /// <summary>
    /// Whether <see cref="Guess"/> knows a language's common words (so a guess of another language is a real mismatch).
    /// </summary>
    /// <param name="language">A two-letter code.</param>
    /// <returns><c>true</c> if known.</returns>
    public static bool KnowsWordsOf(string? language)
        => language is not null && (CommonWords.ContainsKey(language) || Families.Any(f => f.Contains(language, StringComparer.Ordinal)));

    /// <summary>
    /// Whether two languages are the same or too close to tell apart by their common words.
    /// </summary>
    /// <param name="a">A two-letter code.</param>
    /// <param name="b">Another.</param>
    /// <returns><c>true</c> when the same, or in one family.</returns>
    public static bool SameFamily(string? a, string? b)
        => string.Equals(a, b, StringComparison.Ordinal)
            || (a is not null && b is not null && Families.Any(f => f.Contains(a, StringComparer.Ordinal) && f.Contains(b, StringComparer.Ordinal)));

    /// <summary>
    /// The writing system a language is written in, when it has one clear one (Serbian, written in both Cyrillic and
    /// Latin letters, has none; nor do languages not listed).
    /// </summary>
    /// <param name="language">A two-letter code.</param>
    /// <returns>The script, or <c>null</c>.</returns>
    public static Script? ScriptFor(string? language)
        => language is null ? null
            : Scripts.TryGetValue(language, out var s) ? s
            : CommonWords.ContainsKey(language) || language is "nb" or "nn" or "cs" or "sk" or "hu" or "ro" or "hr" or "sl" or "et" or "lv" or "lt" or "id" or "ms" or "vi" or "ca" or "eu" or "gl" or "is" or "ga" or "cy" or "sq" or "af" or "sw" or "tl" ? Script.Latin
            : null;

    /// <summary>
    /// The writing system most of a text's letters are in: at least 80 % of at least 200 letters.
    /// </summary>
    /// <param name="plainText">Subtitle text without markup.</param>
    /// <returns>The script, or <c>null</c> when too little text or mixed.</returns>
    public static Script? ScriptOf(string plainText)
    {
        ArgumentNullException.ThrowIfNull(plainText);
        var counts = new Dictionary<Script, int>();
        var letters = 0;
        foreach (var rune in plainText.EnumerateRunes())
        {
            if (!Rune.IsLetter(rune))
            {
                continue;
            }

            letters++;
            if (ScriptOf(rune) is { } s)
            {
                counts[s] = counts.TryGetValue(s, out var n) ? n + 1 : 1;
            }
        }

        if (letters < 200 || counts.Count == 0)
        {
            return null;
        }

        var top = counts.MaxBy(kv => kv.Value);
        return top.Value >= letters * 0.8 ? top.Key : null;
    }

    private static Script? ScriptOf(Rune rune)
    {
        var c = rune.Value;
        return c switch
        {
            < 0x0250 => Script.Latin,
            >= 0x1E00 and < 0x1F00 => Script.Latin,
            >= 0x0370 and < 0x0400 => Script.Greek,
            >= 0x1F00 and < 0x2000 => Script.Greek,
            >= 0x0400 and < 0x0530 => Script.Cyrillic,
            >= 0x0590 and < 0x0600 => Script.Hebrew,
            >= 0x0600 and < 0x0700 => Script.Arabic,
            >= 0x0750 and < 0x0780 => Script.Arabic,
            >= 0xFB50 and < 0xFE00 => Script.Arabic,
            >= 0xFE70 and < 0xFF00 => Script.Arabic,
            >= 0x0900 and < 0x0980 => Script.Devanagari,
            >= 0x0E00 and < 0x0E80 => Script.Thai,
            >= 0x1100 and < 0x1200 => Script.Hangul,
            >= 0xAC00 and < 0xD7B0 => Script.Hangul,
            >= 0x3040 and < 0x3100 => Script.Cjk,
            >= 0x3400 and < 0xA000 => Script.Cjk,
            >= 0xF900 and < 0xFB00 => Script.Cjk,
            >= 0xFF66 and < 0xFFA0 => Script.Cjk,
            >= 0x20000 and < 0x30000 => Script.Cjk,
            _ => null,
        };
    }

    private static HashSet<string> Set(string words) => new(words.ToUpperInvariant().Split(' '), StringComparer.Ordinal);

    [GeneratedRegex(@"\p{L}+(?:'\p{L}+)?")]
    private static partial Regex Word();
}
