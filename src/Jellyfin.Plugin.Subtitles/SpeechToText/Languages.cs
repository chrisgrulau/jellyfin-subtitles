using System;

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
        // A table, not the server's culture data: without ICU (or with minimal ICU data) that knows no languages (FAM-01)
        return Common.Languages.IsoLanguages.TwoLetter(code);
    }
}
