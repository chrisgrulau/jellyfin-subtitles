using System;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Subtitles.Formats;

/// <summary>
/// Parses SubRip, WebVTT and ASS/SSA text. Real-world files are messy, so parsing is tolerant: missing or wrong cue
/// numbers, stray blank lines, mixed line endings, dot or comma millisecond separators and unreadable cues (skipped) are
/// all accepted.
/// </summary>
public static partial class SubtitleReader
{
    /// <summary>
    /// The largest subtitle file read, in bytes. Real subtitles are well under 1 MB; files come from the internet, so
    /// anything bigger is refused before it is decoded.
    /// </summary>
    public const int MaxBytes = 10 * 1024 * 1024;

    // Default v4+ event format, used when a file has no usable Format line
    private static readonly string[] DefaultAssFormat = ["Layer", "Start", "End", "Style", "Name", "MarginL", "MarginR", "MarginV", "Effect", "Text"];

    /// <summary>Gets the default ASS v4+ event format.</summary>
    public static IReadOnlyList<string> DefaultEventFormat => DefaultAssFormat;

    /// <summary>
    /// Reads a stream into memory, stopping as soon as it is larger than <see cref="MaxBytes"/> (so an oversized or
    /// endless download is never buffered in full).
    /// </summary>
    /// <param name="stream">The stream.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The bytes, or <c>null</c> if it is too large.</returns>
    public static async Task<byte[]?> ReadLimitedAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Reads a subtitle file from its bytes: size check, decoding, format detection and parsing. This is the entry point
    /// for any file that came from outside (a provider download, a file on disk).
    /// </summary>
    /// <param name="bytes">The file content.</param>
    /// <param name="fileName">File name or path, for format detection (may be empty).</param>
    /// <returns>The document, or <c>null</c> if the file is too big or not a supported text format.</returns>
    public static SubtitleDocument? Read(ReadOnlySpan<byte> bytes, string fileName)
    {
        if (bytes.Length > MaxBytes)
        {
            return null;
        }

        // Not UTF-8 or UTF-16: guess the legacy code page from the language tag in the name (Film.ru.srt → Windows-1251)
        var (text, encoding) = SubtitleEncoding.Decode(bytes, SubtitleEncoding.CodePageFor(SubtitleEncoding.LanguageTag(fileName)));
        return Detect(fileName, text) is { } format
            ? Parse(text, format) with { SourceEncoding = encoding, TextSuspect = !SubtitleEncoding.WritesUtf8(encoding) && SubtitleEncoding.LooksWrong(text) }
            : null;
    }

    /// <summary>
    /// Works out the format from the file extension, falling back to the content.
    /// </summary>
    /// <param name="fileName">File name or path (may be empty).</param>
    /// <param name="text">The decoded content.</param>
    /// <returns>The format, or <c>null</c> if it isn't a supported text format.</returns>
    public static SubtitleFormat? Detect(string fileName, string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var head = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (head.StartsWith("WEBVTT", StringComparison.Ordinal))
        {
            return SubtitleFormat.WebVtt;
        }

        if (head.StartsWith("[Script Info]", StringComparison.OrdinalIgnoreCase) || Matches(AssEventsHeading(), text))
        {
            return SubtitleFormat.Ass;
        }

        var ext = Path.GetExtension(fileName ?? string.Empty).ToUpperInvariant();
        switch (ext)
        {
            case ".VTT":
                return SubtitleFormat.WebVtt;
            case ".ASS" or ".SSA":
                return SubtitleFormat.Ass;
            case ".SRT":
                return SubtitleFormat.Srt;
        }

        return Matches(TimingLine(), text) ? SubtitleFormat.Srt : null;
    }

    // The patterns are linear, but content is untrusted: a match that runs out of time counts as no match
    private static bool Matches(Regex pattern, string text)
    {
        try
        {
            return pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Parses subtitle text.
    /// </summary>
    /// <param name="text">The decoded content.</param>
    /// <param name="format">Its format.</param>
    /// <returns>The document, cues ordered by start time.</returns>
    public static SubtitleDocument Parse(string text, SubtitleFormat format)
    {
        ArgumentNullException.ThrowIfNull(text);

        var normalised = text.TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return format switch
        {
            SubtitleFormat.Srt => ParseSrt(normalised),
            SubtitleFormat.WebVtt => ParseVtt(normalised),
            _ => ParseAss(normalised),
        };
    }

    private static SubtitleDocument ParseSrt(string text)
    {
        var cues = new List<SubtitleCue>();
        foreach (var block in Blocks(text))
        {
            var lines = block.Split('\n');
            var t = Array.FindIndex(lines, l => Matches(TimingLine(), l));
            if (t < 0 || !TryTiming(lines[t], out var start, out var end, out _))
            {
                continue;
            }

            var body = string.Join('\n', lines.Skip(t + 1).Select(l => l.TrimEnd())).Trim('\n');
            cues.Add(new SubtitleCue { Start = start, End = end, Text = body });
        }

        return new SubtitleDocument { Format = SubtitleFormat.Srt, Cues = Sorted(cues) };
    }

    private static SubtitleDocument ParseVtt(string text)
    {
        var header = new StringBuilder();
        var cues = new List<SubtitleCue>();
        var seenCue = false;
        foreach (var block in Blocks(text))
        {
            var lines = block.Split('\n');
            var t = Array.FindIndex(lines, l => l.Contains("-->", StringComparison.Ordinal));
            if (t < 0)
            {
                // Before the first cue: the WEBVTT line, STYLE and REGION blocks, notes. Afterwards only NOTE blocks
                // can appear, and they carry nothing a player uses.
                if (!seenCue)
                {
                    header.Append(block).Append("\n\n");
                }

                continue;
            }

            if (!TryTiming(lines[t], out var start, out var end, out var settings))
            {
                continue;
            }

            seenCue = true;
            var id = t > 0 ? lines[t - 1].Trim() : null;
            var body = string.Join('\n', lines.Skip(t + 1).Select(l => l.TrimEnd())).Trim('\n');
            cues.Add(new SubtitleCue
            {
                Start = start,
                End = end,
                Text = body,
                Identifier = string.IsNullOrEmpty(id) ? null : id,
                Settings = string.IsNullOrWhiteSpace(settings) ? null : settings.Trim(),
            });
        }

        var head = header.ToString().TrimEnd('\n');
        return new SubtitleDocument { Format = SubtitleFormat.WebVtt, Cues = Sorted(cues), Header = head.Length == 0 ? "WEBVTT" : head };
    }

    private static SubtitleDocument ParseAss(string text)
    {
        var lines = text.Split('\n');
        var events = Array.FindIndex(lines, l => l.Trim().Equals("[Events]", StringComparison.OrdinalIgnoreCase));
        if (events < 0)
        {
            return new SubtitleDocument { Format = SubtitleFormat.Ass, Cues = [], Header = text.TrimEnd('\n') };
        }

        var header = new StringBuilder();
        foreach (var l in lines.Take(events + 1))
        {
            header.Append(l).Append('\n');
        }

        // Events are read with the latest usable Format line, then stored in the document's format (the first usable
        // one), so every event is written back with one consistent layout. A Format line without Start, End and a
        // final Text field is ignored.
        string[] format = DefaultAssFormat;
        string[]? documentFormat = null;
        var cues = new List<SubtitleCue>();
        var others = new List<string>();
        var trailer = new StringBuilder();
        var i = events + 1;
        for (; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                break;
            }

            if (trimmed.Length == 0 || trimmed.StartsWith(';'))
            {
                continue;
            }

            var colon = trimmed.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            var kind = trimmed[..colon].Trim();
            var rest = trimmed[(colon + 1)..].TrimStart();
            if (kind.Equals("Format", StringComparison.OrdinalIgnoreCase))
            {
                string[] candidate = [.. rest.Split(',').Select(f => f.Trim())];
                if (IsUsableAssFormat(candidate))
                {
                    format = candidate;
                    documentFormat ??= candidate;
                }

                continue;
            }

            if (!kind.Equals("Dialogue", StringComparison.OrdinalIgnoreCase))
            {
                others.Add(line.TrimEnd());
                continue;
            }

            // The Text field is last and may itself contain commas
            var fields = rest.Split(',', format.Length);
            var startAt = Array.FindIndex(format, f => f.Equals("Start", StringComparison.OrdinalIgnoreCase));
            var endAt = Array.FindIndex(format, f => f.Equals("End", StringComparison.OrdinalIgnoreCase));
            var textAt = Array.FindIndex(format, f => f.Equals("Text", StringComparison.OrdinalIgnoreCase));
            if (fields.Length != format.Length || startAt < 0 || endAt < 0 || textAt < 0
                || !Timecode.TryParse(fields[startAt], out var start) || !Timecode.TryParse(fields[endAt], out var end))
            {
                continue;
            }

            documentFormat ??= format;
            cues.Add(new SubtitleCue
            {
                Start = start,
                End = end,
                Text = fields[textAt].Replace("\\N", "\n", StringComparison.Ordinal).Replace("\\n", "\n", StringComparison.Ordinal),
                AssFields = ReferenceEquals(format, documentFormat) ? fields : RemapAssFields(fields, format, documentFormat),
            });
        }

        for (; i < lines.Length; i++)
        {
            trailer.Append(lines[i]).Append('\n');
        }

        return new SubtitleDocument
        {
            Format = SubtitleFormat.Ass,
            Cues = Sorted(cues),
            Header = header.ToString().TrimEnd('\n'),
            AssFormat = documentFormat ?? format,
            AssOtherEvents = others,
            AssTrailer = trailer.ToString().Trim('\n'),
        };
    }

    /// <summary>
    /// Whether an ASS event format can be used: it names Start, End and Text, with Text last (the text may contain
    /// commas, so it must be the final field).
    /// </summary>
    /// <param name="format">The field names.</param>
    /// <returns><c>true</c> if usable.</returns>
    public static bool IsUsableAssFormat(IReadOnlyList<string> format)
    {
        ArgumentNullException.ThrowIfNull(format);
        return format.Count >= 3
            && format.Any(f => f.Equals("Start", StringComparison.OrdinalIgnoreCase))
            && format.Any(f => f.Equals("End", StringComparison.OrdinalIgnoreCase))
            && format[^1].Equals("Text", StringComparison.OrdinalIgnoreCase)
            && format.Distinct(StringComparer.OrdinalIgnoreCase).Count() == format.Count;
    }

    /// <summary>
    /// Moves an event's fields from one format's layout to another's, by name; fields the target has and the source
    /// lacks get their usual defaults.
    /// </summary>
    /// <param name="fields">The fields, in the source layout.</param>
    /// <param name="from">The source format.</param>
    /// <param name="to">The target format.</param>
    /// <returns>The fields in the target layout.</returns>
    public static string[] RemapAssFields(IReadOnlyList<string> fields, IReadOnlyList<string> from, IReadOnlyList<string> to)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        return [.. to.Select(name =>
        {
            var i = from.ToList().FindIndex(f => f.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i < fields.Count ? fields[i] : DefaultAssField(name);
        })];
    }

    /// <summary>
    /// The usual value of an ASS event field that isn't given.
    /// </summary>
    /// <param name="name">Field name.</param>
    /// <returns>The default.</returns>
    public static string DefaultAssField(string name) => (name ?? string.Empty).ToUpperInvariant() switch
    {
        "LAYER" or "MARGINL" or "MARGINR" or "MARGINV" => "0",
        "STYLE" => "Default",
        _ => string.Empty,
    };

    private static IEnumerable<string> Blocks(string text)
        => BlankLines().Split(text).Select(b => b.Trim('\n')).Where(b => b.Trim().Length > 0);

    private static bool TryTiming(string line, out TimeSpan start, out TimeSpan end, out string settings)
    {
        start = end = TimeSpan.Zero;
        settings = string.Empty;
        Match m;
        try
        {
            m = TimingLine().Match(line);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }

        if (!m.Success || !Timecode.TryParse(m.Groups["a"].Value, out start) || !Timecode.TryParse(m.Groups["b"].Value, out end))
        {
            return false;
        }

        settings = m.Groups["rest"].Value;
        if (end < start)
        {
            end = start;
        }

        return true;
    }

    private static List<SubtitleCue> Sorted(List<SubtitleCue> cues) => [.. cues.OrderBy(c => c.Start).ThenBy(c => c.End)];

    // Only spaces and tabs around the parts (never \s, which also matches newlines and let a multiline match wander over
    // runs of blank lines); a time limit on every pattern that sees whole files
    [GeneratedRegex(@"^[ \t]*(?<a>[\d:.,]+)[ \t]*-->[ \t]*(?<b>[\d:.,]+)(?<rest>.*)$", RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TimingLine();

    [GeneratedRegex(@"\n[ \t]*\n", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BlankLines();

    [GeneratedRegex(@"^[ \t]*\[Events\][ \t]*$", RegexOptions.Multiline | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AssEventsHeading();
}
