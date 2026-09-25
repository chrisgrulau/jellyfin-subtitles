using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Subtitles.Candidates;

/// <summary>
/// Guesses the language of subtitle text from its most common short words. Cheap and good enough to catch a subtitle
/// labelled with the wrong language; it is not a general-purpose detector.
/// </summary>
public static partial class LanguageGuesser
{
    private static readonly Dictionary<string, HashSet<string>> CommonWords = new(StringComparer.Ordinal)
    {
        ["eng"] = Set("the you i to a and it is that what of in me this we he not have be your do was are no for on my don't know"),
        ["fre"] = Set("de je le la les et est pas vous que un une il ce en ne on tu qui pour mais c'est moi me qu'est-ce"),
        ["spa"] = Set("de que el la y no es en a lo un los por qué me se una con para te está mi eso"),
        ["ger"] = Set("ich die und der nicht du das ist es sie zu ein was wir mit sie den mir ja auf hast"),
        ["ita"] = Set("di che non è e il la un a per mi ti ho sono lo una cosa questo io ma se sei"),
        ["por"] = Set("que não de o a é e um eu você se do da me uma para isso está com os"),
        ["dut"] = Set("de het een ik je niet is dat van en wat we zijn hij er maar op te met"),
    };

    /// <summary>
    /// Guesses the language.
    /// </summary>
    /// <param name="plainText">Subtitle text without markup.</param>
    /// <returns>An ISO 639-2 code and the share of words that support it, or <c>null</c> when too little text or no
    /// clear winner.</returns>
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

        // Must stand out: at least 15 % of all words, and clearly ahead of the runner-up
        var best = ranked[0];
        return best.Share >= 0.15 && best.Share >= ranked[1].Share * 1.5 ? best : null;
    }

    private static HashSet<string> Set(string words) => new(words.ToUpperInvariant().Split(' '), StringComparer.Ordinal);

    [GeneratedRegex(@"\p{L}+(?:'\p{L}+)?")]
    private static partial Regex Word();
}
