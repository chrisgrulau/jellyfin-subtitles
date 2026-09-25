using System;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.Subtitles.SpeechToText;

/// <summary>
/// Language codes: the settings use three-letter ISO 639-2 codes (as Jellyfin does); speech-to-text services use
/// two-letter ISO 639-1 codes.
/// </summary>
public static class Languages
{
    /// <summary>
    /// Converts a language code to its two-letter form.
    /// </summary>
    /// <param name="code">A two- or three-letter code (<c>eng</c>, <c>en</c>, <c>en-US</c>).</param>
    /// <returns>The two-letter code, or <c>null</c> if unknown.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "ISO 639-1 codes are lower case by definition; only ASCII letters reach this point.")]
    public static string? ToTwoLetter(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var c = code.Trim().ToUpperInvariant();
        var dash = c.IndexOf('-', StringComparison.Ordinal);
        if (dash > 0)
        {
            c = c[..dash];
        }

        if (c.Length == 2 && c.All(char.IsAsciiLetter))
        {
            return c.ToLowerInvariant();
        }

        // Bibliographic codes that differ from the terminology ones .NET uses
        c = c switch { "FRE" => "FRA", "GER" => "DEU", "DUT" => "NLD", "CHI" => "ZHO", "CZE" => "CES", "GRE" => "ELL", "PER" => "FAS", "RUM" => "RON", "SLO" => "SLK", "ALB" => "SQI", "ARM" => "HYE", "BAQ" => "EUS", "BUR" => "MYA", "GEO" => "KAT", "ICE" => "ISL", "MAC" => "MKD", "MAO" => "MRI", "MAY" => "MSA", "TIB" => "BOD", "WEL" => "CYM", _ => c };
        var culture = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
            .FirstOrDefault(x => string.Equals(x.ThreeLetterISOLanguageName, c, StringComparison.OrdinalIgnoreCase));
        return culture is null || culture.TwoLetterISOLanguageName.Length != 2 ? null : culture.TwoLetterISOLanguageName;
    }
}
