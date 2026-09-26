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
    /// Encodes written text. A document read in a legacy code page is written back in that code page, so unchanged lines
    /// keep their exact bytes. Anything else is UTF-8: SubRip and ASS get a byte-order mark, which stops players that
    /// assume a legacy code page from garbling accented characters; WebVTT is UTF-8 by definition and gets none.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <returns>The file bytes.</returns>
    public static byte[] ToBytes(SubtitleDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        // A file read in a legacy code page is written back in it: every line that wasn't changed keeps its bytes
        if (!SubtitleEncoding.WritesUtf8(document.SourceEncoding))
        {
            return SubtitleEncoding.EncodeLegacy(Write(document), document.SourceEncoding!);
        }

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
                .Append(CueText(c.Text).Replace("\n", "\r\n", StringComparison.Ordinal)).Append("\r\n\r\n");
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

            // "-->" would read as a timing line; the entity shows the same characters
            sb.Append('\n').Append(CueText(c.Text).Replace("-->", "--&gt;", StringComparison.Ordinal)).Append("\n\n");
        }

        return sb.ToString();
    }

    private static string WriteAss(SubtitleDocument document)
    {
        // A missing or unusable format (no Start, End or final Text) falls back to the standard v4+ one; each event's
        // fields are moved to it by name
        var original = document.AssFormat;
        var format = SubtitleReader.IsUsableAssFormat(original) ? original : SubtitleReader.DefaultEventFormat;
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
            var fields = c.AssFields is not { } f ? Defaults(format)
                : ReferenceEquals(format, original) ? (f.Count == format.Count ? f.ToArray() : Defaults(format))
                : f.Count == original.Count ? SubtitleReader.RemapAssFields(f, original, format) : Defaults(format);
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
        => [.. format.Select(SubtitleReader.DefaultAssField)];

    // A blank line ends a SubRip or WebVTT cue early, so blank lines inside a cue (from an ASS "\N\N" or a removed
    // middle line) are dropped
    private static string CueText(string text)
        => string.Join('\n', text.Split('\n').Where(l => l.Trim().Length > 0));
}
