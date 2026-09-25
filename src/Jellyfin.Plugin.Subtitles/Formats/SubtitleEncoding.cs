using System;
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
