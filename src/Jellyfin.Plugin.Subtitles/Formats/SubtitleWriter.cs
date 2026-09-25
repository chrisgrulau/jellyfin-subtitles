using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.Subtitles.Formats;

/// <summary>
/// Writes a <see cref="SubtitleDocument"/> back to text in its own format. ASS/SSA events keep every field (style,
/// layer, margins, effect, override tags) except the ones that changed, so retiming never loses styling.
/// </summary>
public static class SubtitleWriter
{
    /// <summary>
    /// Writes the document as text.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <returns>The file content (SubRip and ASS with CRLF line endings for the widest player support; WebVTT with LF).</returns>
    public static string Write(SubtitleDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Format switch
        {
            SubtitleFormat.Srt => WriteSrt(document),
            SubtitleFormat.WebVtt => WriteVtt(document),
            _ => WriteAss(document),
        };
    }

    /// <summary>
    /// Encodes written text as UTF-8. SubRip and ASS get a byte-order mark, which stops players that assume a legacy code
    /// page from garbling accented characters; WebVTT is UTF-8 by definition and gets none.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <returns>The file bytes.</returns>
    public static byte[] ToBytes(SubtitleDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var body = Encoding.UTF8.GetBytes(Write(document));
        return document.Format == SubtitleFormat.WebVtt ? body : [.. Encoding.UTF8.Preamble, .. body];
    }

    private static string WriteSrt(SubtitleDocument document)
    {
        var sb = new StringBuilder();
        var n = 1;
        foreach (var c in document.Cues)
        {
            sb.Append(n++.ToString(CultureInfo.InvariantCulture)).Append("\r\n")
                .Append(Timecode.Format(c.Start, SubtitleFormat.Srt)).Append(" --> ").Append(Timecode.Format(c.End, SubtitleFormat.Srt)).Append("\r\n")
                .Append(c.Text.Replace("\n", "\r\n", StringComparison.Ordinal)).Append("\r\n\r\n");
        }

        return sb.ToString();
    }

    private static string WriteVtt(SubtitleDocument document)
    {
        var sb = new StringBuilder(string.IsNullOrWhiteSpace(document.Header) ? "WEBVTT" : document.Header).Append("\n\n");
        foreach (var c in document.Cues)
        {
            if (!string.IsNullOrEmpty(c.Identifier))
            {
                sb.Append(c.Identifier).Append('\n');
            }

            sb.Append(Timecode.Format(c.Start, SubtitleFormat.WebVtt)).Append(" --> ").Append(Timecode.Format(c.End, SubtitleFormat.WebVtt));
            if (!string.IsNullOrEmpty(c.Settings))
            {
                sb.Append(' ').Append(c.Settings);
            }

            sb.Append('\n').Append(c.Text).Append("\n\n");
        }

        return sb.ToString();
    }

    private static string WriteAss(SubtitleDocument document)
    {
        var format = document.AssFormat.Count > 0
            ? document.AssFormat
            : ["Layer", "Start", "End", "Style", "Name", "MarginL", "MarginR", "MarginV", "Effect", "Text"];
        int At(string name) => format.ToList().FindIndex(f => f.Equals(name, StringComparison.OrdinalIgnoreCase));
        var startAt = At("Start");
        var endAt = At("End");
        var textAt = At("Text");

        var sb = new StringBuilder(document.Header.Length > 0 ? document.Header : "[Script Info]\r\nScriptType: v4.00+\r\n\r\n[Events]");
        sb.Replace("\r\n", "\n").Replace("\n", "\r\n");
        sb.Append("\r\nFormat: ").Append(string.Join(", ", format)).Append("\r\n");
        foreach (var other in document.AssOtherEvents)
        {
            sb.Append(other).Append("\r\n");
        }

        foreach (var c in document.Cues)
        {
            var fields = c.AssFields is { } f && f.Count == format.Count ? f.ToArray() : Defaults(format);
            fields[startAt] = Timecode.Format(c.Start, SubtitleFormat.Ass);
            fields[endAt] = Timecode.Format(c.End, SubtitleFormat.Ass);
            fields[textAt] = c.Text.Replace("\n", "\\N", StringComparison.Ordinal);
            sb.Append("Dialogue: ").Append(string.Join(',', fields)).Append("\r\n");
        }

        if (document.AssTrailer.Length > 0)
        {
            sb.Append("\r\n").Append(document.AssTrailer.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal)).Append("\r\n");
        }

        return sb.ToString();
    }

    private static string[] Defaults(System.Collections.Generic.IReadOnlyList<string> format)
        => [.. format.Select(f => f.ToUpperInvariant() switch
        {
            "LAYER" or "MARGINL" or "MARGINR" or "MARGINV" => "0",
            "STYLE" => "Default",
            _ => string.Empty,
        })];
}
