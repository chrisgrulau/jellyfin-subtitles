using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.Configuration;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.Pipeline;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// SUB-14: subtitles in legacy code pages are read in the right one where the name says the language, and are always
// written back in the one they were read with, so unchanged lines keep their exact bytes. Invented text throughout.
public sealed class EncodingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "subs-enc-" + Guid.NewGuid().ToString("N"));

    static EncodingTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public EncodingTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Srt(params string[] lines)
        => string.Concat(lines.Select((l, i) => $"{i + 1}\r\n00:00:{10 + (i * 5):00},000 --> 00:00:{12 + (i * 5):00},000\r\n{l}\r\n\r\n"));

    public static TheoryData<string, int, string, string> Samples => new()
    {
        { "Film.ru.srt", 1251, "Привет, как дела?", "Где ты был вчера?" },
        { "Film.pl.srt", 1250, "Zażółć gęślą jaźń.", "Dziękuję bardzo." },
        { "Film.ja.srt", 932, "こんにちは、元気ですか？", "昨日どこにいましたか？" },
        { "Film.zh.srt", 54936, "你好，你今天怎么样？", "昨天你在哪里？" },
        { "Film.el.srt", 1253, "Καλημέρα σας.", "Πού ήσουν χθες;" },
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void Legacy_text_is_read_in_the_language_code_page_and_written_back_byte_for_byte(string name, int codePage, string first, string second)
    {
        var bytes = Encoding.GetEncoding(codePage).GetBytes(Srt(first, second));

        var document = SubtitleReader.Read(bytes, name)!;

        Assert.Equal(first, document.Cues[0].Text);
        Assert.False(document.TextSuspect);
        Assert.Equal(bytes, SubtitleWriter.ToBytes(document));
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Retiming_changes_only_the_timing_lines(string name, int codePage, string first, string second)
    {
        var encoding = Encoding.GetEncoding(codePage);
        var bytes = encoding.GetBytes(Srt(first, second));

        // Even with no language in the name (so the code page is guessed wrong), only the timing lines change
        foreach (var file in new[] { name, "Film.srt" })
        {
            var document = SubtitleReader.Read(bytes, file)!;
            var written = SubtitleWriter.ToBytes(document.Retime(t => t + TimeSpan.FromSeconds(1)));

            var expected = encoding.GetBytes(Srt(first, second).Replace("00:00:10,000 --> 00:00:12,000", "00:00:11,000 --> 00:00:13,000", StringComparison.Ordinal).Replace("00:00:15,000 --> 00:00:17,000", "00:00:16,000 --> 00:00:18,000", StringComparison.Ordinal));
            Assert.Equal(expected, written);
        }
    }

    [Fact]
    public void Utf8_and_utf16_are_still_written_as_utf8()
    {
        var text = Srt("Größe", "Ça va?");
        var fromUtf16 = SubtitleReader.Read([0xFF, 0xFE, .. Encoding.Unicode.GetBytes(text)], "Film.de.srt")!;
        var fromUtf8 = SubtitleReader.Read(Encoding.UTF8.GetBytes(text), "Film.de.srt")!;

        Assert.Equal([.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(text)], SubtitleWriter.ToBytes(fromUtf16));
        Assert.Equal("Größe", fromUtf8.Cues[0].Text);
    }

    [Fact]
    public void A_changed_line_the_code_page_cant_hold_is_refused_not_replaced()
    {
        var document = SubtitleReader.Read(Encoding.GetEncoding(1251).GetBytes(Srt("Привет", "Пока")), "Film.ru.srt")!;
        var edited = document with { Cues = [document.Cues[0] with { Text = "こんにちは" }, document.Cues[1]] };

        Assert.Throws<InvalidOperationException>(() => SubtitleWriter.ToBytes(edited));
    }

    [Theory]
    [InlineData("Film.ru.srt", "ru")]
    [InlineData("Film.rus.forced.srt", "rus")]
    [InlineData("Film.en.srt", null)]
    [InlineData("Film.srt", null)]
    [InlineData("Some.Show.S01E01.pl.srt", "pl")]
    public void The_language_tag_comes_from_the_name(string name, string? tag)
        => Assert.Equal(tag, SubtitleEncoding.LanguageTag(name));

    [Fact]
    public async Task Text_that_decodes_badly_only_gets_proposals_and_the_file_is_left_alone()
    {
        // Not UTF-8 (a stray byte in every line), read as Windows-1252, where that byte is a C1 control character
        var path = Path.Combine(_dir, "Film.srt");
        var story = PipelineTests.Story();
        var stray = Encoding.Latin1.GetBytes(SubtitleWriter.Write(story).Replace(" says ", " says\u0081 ", StringComparison.Ordinal));
        await File.WriteAllBytesAsync(path, stray, TestContext.Current.CancellationToken);
        Assert.True(SubtitleReader.Read(stray, path)!.TextSuspect);
        var processor = new SubtitleProcessor(new ResultStore(Path.Combine(_dir, "results.json")), new SubtitleFiles(Path.Combine(_dir, "originals")));
        var job = new SubtitleJob(Guid.NewGuid(), "Invented Film", Path.Combine(_dir, "Film.mkv"), path, "jpn", TimeSpan.FromMinutes(25), 0);
        // The fake speech-to-text "hears" the file's own (mis-decoded) words, 2.5 s late, so the timing is found
        var late = new PipelineTests.Shifted(SubtitleReader.Read(stray, path)!, 2.5);

        var result = await processor.ProcessAsync(job, late, late, new Policies(ChangePolicy.Automatic, ChangePolicy.Automatic, new CleanupSettings()), CancellationToken.None);

        Assert.Equal(ResultStatus.Proposed, result.Status);
        Assert.Contains("didn't decode cleanly", result.Explanation, StringComparison.Ordinal);
        Assert.Equal(stray, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }
}
