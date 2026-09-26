using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Subtitles.Configuration;

/// <summary>
/// What the subtitle-languages setting means.
/// </summary>
public static class LanguageSettings
{
    /// <summary>
    /// The languages to check: the configured three-letter codes, lower case, each once; English when none are set.
    /// </summary>
    /// <param name="configured">The setting.</param>
    /// <returns>The codes.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "ISO 639-2 codes are lower case by convention, as Jellyfin stores them.")]
    public static IReadOnlyList<string> EffectiveLanguages(IEnumerable<string>? configured)
    {
        // Any form is accepted (eng, en, English, fre, fra, en-GB), through a table rather than the server's culture data
        // A three-letter code is kept as written (Jellyfin uses the bibliographic ones, such as fre and ger)
        var codes = (configured ?? []).Select(c => (c ?? string.Empty).Trim())
            .Select(c => c.Length == 3 && c.All(char.IsAsciiLetter) && Common.Languages.IsoLanguages.TwoLetter(c) is not null
                ? c.ToLowerInvariant()
                : Common.Languages.IsoLanguages.ThreeLetter(c))
            .OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        return codes.Count > 0 ? codes : ["eng"];
    }

    /// <summary>
    /// Configured languages that aren't recognised (they are ignored; worth a line in the log).
    /// </summary>
    /// <param name="configured">The setting.</param>
    /// <returns>The entries that aren't languages.</returns>
    public static IReadOnlyList<string> UnknownLanguages(IEnumerable<string>? configured)
    {
        return [.. (configured ?? []).Where(c => !string.IsNullOrWhiteSpace(c) && Common.Languages.IsoLanguages.ThreeLetter(c) is null).Select(c => c.Trim())];
    }
}
