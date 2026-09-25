using System;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.Subtitles.Formats;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// Invented dialogue throughout.
public class FormatTests
{
    private const string Srt = "1\r\n00:00:01,500 --> 00:00:03,000\r\n<i>Where did you park?</i>\r\n\r\n2\r\n00:00:03,200 --> 00:00:05,000\r\nBy the harbour.\r\nNear the old crane.\r\n\r\n";

    [Theory]
    [InlineData("00:01:02,345", 0, 1, 2, 345)]
    [InlineData("00:01:02.345", 0, 1, 2, 345)]
    [InlineData("01:02.345", 0, 1, 2, 345)]
    [InlineData("1:02:03.45", 1, 2, 3, 450)]
    [InlineData("10:00:00,5", 10, 0, 0, 500)]
    [InlineData("00:00:07", 0, 0, 7, 0)]
    public void Timecodes_in_every_notation_parse(string text, int h, int m, int s, int ms)
    {
        Assert.True(Timecode.TryParse(text, out var t));
        Assert.Equal(new TimeSpan(0, h, m, s, ms), t);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("00:61:00,000")]
    public void Nonsense_timecodes_are_rejected(string text) => Assert.False(Timecode.TryParse(text, out _));

    [Fact]
    public void Timecodes_are_written_per_format()
    {
        var t = new TimeSpan(0, 1, 2, 3, 456);
        Assert.Equal("01:02:03,456", Timecode.Format(t, SubtitleFormat.Srt));
        Assert.Equal("01:02:03.456", Timecode.Format(t, SubtitleFormat.WebVtt));
        Assert.Equal("1:02:03.46", Timecode.Format(t, SubtitleFormat.Ass));
        Assert.Equal("00:00:00,000", Timecode.Format(TimeSpan.FromSeconds(-3), SubtitleFormat.Srt));
    }

    [Fact]
    public void SubRip_round_trips()
    {
        var doc = SubtitleReader.Parse(Srt, SubtitleFormat.Srt);
        Assert.Equal(2, doc.Cues.Count);
        Assert.Equal("By the harbour.\nNear the old crane.", doc.Cues[1].Text);
        Assert.Equal(Srt, SubtitleWriter.Write(doc));
    }

    [Fact]
    public void Messy_SubRip_is_read_tolerantly()
    {
        const string messy = "﻿\n\n7\n00:00:05.000 --> 00:00:06.000\nSecond\n\n\n\n3\n00:00:01,000 --> 00:00:02,000\nFirst   \n\nnot a cue\n\n00:00:09,000 --> 00:00:10,000\nNo number\n";
        var doc = SubtitleReader.Parse(messy, SubtitleFormat.Srt);
        Assert.Equal(["First", "Second", "No number"], doc.Cues.Select(c => c.Text));
    }

    [Fact]
    public void WebVtt_keeps_header_identifiers_and_settings()
    {
        const string vtt = "WEBVTT - invented\n\nSTYLE\n::cue { color: yellow }\n\nintro\n00:01.000 --> 00:02.500 align:start line:10%\n<v Ana>Hello there</v>\n\nNOTE between cues\n\n00:00:03.000 --> 00:00:04.000\nSecond line\n\n";
        var doc = SubtitleReader.Parse(vtt, SubtitleFormat.WebVtt);
        Assert.Equal("WEBVTT - invented\n\nSTYLE\n::cue { color: yellow }", doc.Header);
        Assert.Equal("intro", doc.Cues[0].Identifier);
        Assert.Equal("align:start line:10%", doc.Cues[0].Settings);
        Assert.Equal("Hello there", SubtitleMarkup.ToPlainText(doc.Cues[0].Text));

        var written = SubtitleWriter.Write(doc);
        Assert.Contains("intro\n00:00:01.000 --> 00:00:02.500 align:start line:10%\n<v Ana>Hello there</v>", written, StringComparison.Ordinal);
        Assert.Equal(doc.Cues, SubtitleReader.Parse(written, SubtitleFormat.WebVtt).Cues);
    }

    [Fact]
    public void Ass_retiming_keeps_styles_fields_and_commas()
    {
        const string ass = "[Script Info]\nTitle: Invented\nScriptType: v4.00+\n\n[V4+ Styles]\nFormat: Name, Fontname\nStyle: Sign,Arial\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nComment: 0,0:00:00.00,0:00:01.00,Sign,,0,0,0,,note\nDialogue: 1,0:00:01.50,0:00:03.00,Sign,Ana,10,10,20,,{\\an8}Wait, what?\\NNo, really.\n";
        var doc = SubtitleReader.Parse(ass, SubtitleFormat.Ass);
        var cue = Assert.Single(doc.Cues);
        Assert.Equal("{\\an8}Wait, what?\nNo, really.", cue.Text);
        Assert.Equal("Wait, what?\nNo, really.", SubtitleMarkup.ToPlainText(cue.Text));

        var shifted = SubtitleWriter.Write(doc.Retime(t => t + TimeSpan.FromSeconds(2)));
        Assert.Contains("Style: Sign,Arial", shifted, StringComparison.Ordinal);
        Assert.Contains("Comment: 0,0:00:00.00,0:00:01.00,Sign,,0,0,0,,note", shifted, StringComparison.Ordinal);
        Assert.Contains("Dialogue: 1,0:00:03.50,0:00:05.00,Sign,Ana,10,10,20,,{\\an8}Wait, what?\\NNo, really.", shifted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a.srt", "1\n00:00:01,000 --> 00:00:02,000\nHi\n", SubtitleFormat.Srt)]
    [InlineData("a.txt", "1\n00:00:01,000 --> 00:00:02,000\nHi\n", SubtitleFormat.Srt)]
    [InlineData("a.srt", "WEBVTT\n\n00:01.000 --> 00:02.000\nHi\n", SubtitleFormat.WebVtt)]
    [InlineData("a.txt", "[Script Info]\nTitle: x\n", SubtitleFormat.Ass)]
    public void Format_is_detected_from_content_first(string name, string text, SubtitleFormat expected)
        => Assert.Equal(expected, SubtitleReader.Detect(name, text));

    [Fact]
    public void Unknown_content_is_not_a_subtitle() => Assert.Null(SubtitleReader.Detect("notes.txt", "just some notes"));

    [Fact]
    public void Retiming_clamps_at_zero_and_keeps_order()
    {
        var doc = SubtitleReader.Parse(Srt, SubtitleFormat.Srt).Retime(t => t - TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.Zero, doc.Cues[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(1), doc.Cues[0].End);
        Assert.Equal(TimeSpan.FromSeconds(1.2), doc.Cues[1].Start);
    }

    [Fact]
    public void Encodings_are_detected()
    {
        Assert.Equal(("Café", "utf-8"), SubtitleEncoding.Decode([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("Café")]));
        Assert.Equal(("Café", "utf-8"), SubtitleEncoding.Decode(Encoding.UTF8.GetBytes("Café")));
        Assert.Equal(("Café", "utf-16le"), SubtitleEncoding.Decode([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("Café")]));
        Assert.Equal(("Café", "windows-1252"), SubtitleEncoding.Decode([(byte)'C', (byte)'a', (byte)'f', 0xE9]));
    }

    [Fact]
    public void SubRip_is_written_as_utf8_with_a_bom_and_webvtt_without()
    {
        var srt = SubtitleReader.Parse(Srt, SubtitleFormat.Srt);
        Assert.Equal(Encoding.UTF8.Preamble.ToArray(), SubtitleWriter.ToBytes(srt)[..3]);
        var vtt = srt with { Format = SubtitleFormat.WebVtt, Header = "WEBVTT" };
        Assert.Equal((byte)'W', SubtitleWriter.ToBytes(vtt)[0]);
    }
}
