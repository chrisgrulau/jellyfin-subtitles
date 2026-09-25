using System;
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

        if (head.StartsWith("[Script Info]", StringComparison.OrdinalIgnoreCase) || AssEventsHeading().IsMatch(text))
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

        return TimingLine().IsMatch(text) ? SubtitleFormat.Srt : null;
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
            var t = Array.FindIndex(lines, l => TimingLine().IsMatch(l));
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

        // Default v4+ event format, used if the file has no Format line
        string[] format = ["Layer", "Start", "End", "Style", "Name", "MarginL", "MarginR", "MarginV", "Effect", "Text"];
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
                format = [.. rest.Split(',').Select(f => f.Trim())];
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

            cues.Add(new SubtitleCue
            {
                Start = start,
                End = end,
                Text = fields[textAt].Replace("\\N", "\n", StringComparison.Ordinal).Replace("\\n", "\n", StringComparison.Ordinal),
                AssFields = fields,
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
            AssFormat = format,
            AssOtherEvents = others,
            AssTrailer = trailer.ToString().Trim('\n'),
        };
    }

    private static IEnumerable<string> Blocks(string text)
        => BlankLines().Split(text).Select(b => b.Trim('\n')).Where(b => b.Trim().Length > 0);

    private static bool TryTiming(string line, out TimeSpan start, out TimeSpan end, out string settings)
    {
        start = end = TimeSpan.Zero;
        settings = string.Empty;
        var m = TimingLine().Match(line);
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

    [GeneratedRegex(@"^\s*(?<a>[\d:.,]+)\s*-->\s*(?<b>[\d:.,]+)(?<rest>.*)$", RegexOptions.Multiline)]
    private static partial Regex TimingLine();

    [GeneratedRegex(@"\n[ \t]*\n")]
    private static partial Regex BlankLines();

    [GeneratedRegex(@"^\s*\[Events\]\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex AssEventsHeading();
}
