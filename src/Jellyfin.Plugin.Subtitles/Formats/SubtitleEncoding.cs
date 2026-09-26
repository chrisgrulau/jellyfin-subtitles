using System;
using System.Linq;
using System.Text;
using System.Text.Unicode;

namespace Jellyfin.Plugin.Subtitles.Formats;

/// <summary>
/// Decodes subtitle files of unknown encoding. Many subtitles, especially older ones, are not UTF-8.
/// </summary>
public static class SubtitleEncoding
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    static SubtitleEncoding()
    {
        // Windows code pages are available in .NET but must be registered before use
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// The legacy code page most likely for a language, used when a file isn't UTF-8 or UTF-16.
    /// </summary>
    /// <param name="language">An ISO 639-1 or 639-2 code (any case), or <c>null</c>.</param>
    /// <returns>The code page (Windows-1252 when the language isn't known).</returns>
    public static int CodePageFor(string? language) => (language ?? string.Empty).ToUpperInvariant() switch
    {
        "RU" or "RUS" or "UK" or "UKR" or "BG" or "BUL" or "SR" or "SRP" or "BE" or "BEL" or "MK" or "MKD" or "MAC" => 1251,
        "PL" or "POL" or "CS" or "CES" or "CZE" or "SK" or "SLK" or "SLO" or "HU" or "HUN" or "RO" or "RON" or "RUM" or "HR" or "HRV" or "SL" or "SLV" or "BS" or "BOS" or "SQ" or "SQI" or "ALB" => 1250,
        "EL" or "ELL" or "GRE" => 1253,
        "TR" or "TUR" or "AZ" or "AZE" => 1254,
        "HE" or "HEB" => 1255,
        "AR" or "ARA" or "FA" or "FAS" or "PER" or "UR" or "URD" => 1256,
        "ET" or "EST" or "LV" or "LAV" or "LT" or "LIT" => 1257,
        "VI" or "VIE" => 1258,
        "TH" or "THA" => 874,
        "JA" or "JPN" => 932,
        "KO" or "KOR" => 949,
        "ZH" or "ZHO" or "CHI" or "CHS" => 54936,
        "ZHT" or "CHT" => 950,
        _ => 1252,
    };

    /// <summary>
    /// The language tag in a subtitle file name (<c>Film.ru.srt</c>, <c>Film.rus.forced.srt</c>), if any.
    /// </summary>
    /// <param name="fileName">The file name or path.</param>
    /// <returns>The first two- or three-letter part after the name that maps to a code page, or <c>null</c>.</returns>
    public static string? LanguageTag(string? fileName)
    {
        var parts = System.IO.Path.GetFileNameWithoutExtension(fileName ?? string.Empty).Split('.');
        for (var i = parts.Length - 1; i >= 1; i--)
        {
            var p = parts[i];
            if (p.Length is 2 or 3 && p.All(char.IsAsciiLetter) && CodePageFor(p) != 1252)
            {
                return p;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether decoded text looks wrong: replacement characters or C1 control characters, which real subtitles don't
    /// contain.
    /// </summary>
    /// <param name="text">The decoded text.</param>
    /// <returns>Whether it looks wrong.</returns>
    public static bool LooksWrong(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.AsSpan().ContainsAny('\uFFFD', '\u0081', '\u008D') || text.Any(c => c is >= '\u0080' and <= '\u009F');
    }

    /// <summary>
    /// Whether text read in this encoding is written back as UTF-8 (UTF-8 and UTF-16 input), rather than in the same
    /// legacy code page.
    /// </summary>
    /// <param name="encodingName">The encoding's web name, or <c>null</c>.</param>
    /// <returns>Whether UTF-8 is written.</returns>
    public static bool WritesUtf8(string? encodingName)
        => encodingName is null || encodingName.StartsWith("utf-", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Encodes text in a legacy code page, refusing characters it can't hold (never replaces them).
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="encodingName">The code page's web name.</param>
    /// <returns>The bytes.</returns>
    /// <exception cref="InvalidOperationException">The text holds a character the code page can't.</exception>
    public static byte[] EncodeLegacy(string text, string encodingName)
    {
        var encoding = Encoding.GetEncoding(encodingName, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
        try
        {
            return encoding.GetBytes(text);
        }
        catch (EncoderFallbackException ex)
        {
            throw new InvalidOperationException($"The subtitle is in {encodingName}, which can't hold a character in the changed text; it wasn't written.", ex);
        }
    }

    /// <summary>
    /// Decodes bytes: a UTF-16 byte-order mark wins; otherwise valid UTF-8 (with or without its byte-order mark) is read as
    /// UTF-8; anything else uses the fallback code page (Windows-1252, the usual encoding of older Western European
    /// subtitles, unless another is given). A UTF-8 mark in front of text that isn't UTF-8, common after careless
    /// conversions, falls back too.
    /// </summary>
    /// <param name="bytes">The file content.</param>
    /// <param name="fallbackCodePage">Code page to use when the content isn't valid UTF-8.</param>
    /// <returns>The text and the name of the encoding it was read with.</returns>
    public static (string Text, string EncodingName) Decode(ReadOnlySpan<byte> bytes, int fallbackCodePage = 1252)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return (Encoding.Unicode.GetString(bytes[2..]), "utf-16le");
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return (Encoding.BigEndianUnicode.GetString(bytes[2..]), "utf-16be");
        }

        var body = bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? bytes[3..] : bytes;
        if (Utf8.IsValid(body))
        {
            return (StrictUtf8.GetString(body), "utf-8");
        }

        var fallback = Encoding.GetEncoding(fallbackCodePage);
        return (fallback.GetString(body), fallback.WebName);
    }
}
