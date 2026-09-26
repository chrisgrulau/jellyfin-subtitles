using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.Audio;
using Jellyfin.Plugin.Subtitles.Configuration;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.Pipeline;
using Jellyfin.Plugin.Subtitles.Sync;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// Review pass 3: SUB-16, SUB-17, SUB-18, SUB-21, SUB-28. Invented text throughout.
public sealed class ReviewPass3Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "subs-pass3-" + Guid.NewGuid().ToString("N"));

    public ReviewPass3Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
        {
            foreach (var p in Directory.EnumerateFileSystemEntries(_dir, "*", SearchOption.AllDirectories).Append(_dir))
            {
                File.SetUnixFileMode(p, Directory.Exists(p) ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute : UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        Directory.Delete(_dir, recursive: true);
    }

    private static void NeedsPermissions() => Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.UserName == "root", "Needs a non-root Unix account.");

    private static SubtitleResult Result(string path) => new() { Id = ResultStore.IdFor(path), SubtitlePath = path, Status = ResultStatus.InSync, Time = DateTimeOffset.UtcNow };

    // SUB-18
    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void A_results_file_that_cant_be_read_is_never_overwritten()
    {
        NeedsPermissions();
        var path = Path.Combine(_dir, "results.json");
        new ResultStore(path).Put(Result(Path.Combine(_dir, "a.srt")));
        var before = File.ReadAllBytes(path);
        File.SetUnixFileMode(path, UnixFileMode.None);

        var store = new ResultStore(path);

        Assert.False(store.Readable);
        Assert.NotNull(store.Problem);
        Assert.Throws<InvalidOperationException>(() => store.Put(Result(Path.Combine(_dir, "b.srt"))));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.True(store.Readable);
        Assert.Single(store.All());
    }

    [Fact]
    public void A_damaged_results_file_is_set_aside_and_results_start_afresh()
    {
        var path = Path.Combine(_dir, "results.json");
        File.WriteAllText(path, "[{ damaged");

        var store = new ResultStore(path);
        store.Put(Result(Path.Combine(_dir, "a.srt")));

        Assert.Single(Directory.GetFiles(_dir, "results.json.damaged-*"));
        Assert.Contains("set aside", store.Problem, StringComparison.Ordinal);
        Assert.Single(new ResultStore(path).All());
    }

    // SUB-16
    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task A_folder_that_cant_be_written_is_recorded_once_and_not_checked_every_night()
    {
        NeedsPermissions();
        var folder = Path.Combine(_dir, "ro");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "Film.en.srt");
        await File.WriteAllBytesAsync(path, SubtitleWriter.ToBytes(PipelineTests.Story()), TestContext.Current.CancellationToken);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var processor = new SubtitleProcessor(new ResultStore(Path.Combine(_dir, "results.json")), new SubtitleFiles(Path.Combine(_dir, "originals")));
        var job = new SubtitleJob(Guid.NewGuid(), "Invented Film", Path.Combine(folder, "Film.mkv"), path, "eng", TimeSpan.FromMinutes(25), 0);
        var audio = new PipelineTests.Shifted(PipelineTests.Story(), 2.5);

        var result = await processor.ProcessAsync(job, audio, audio, new Policies(ChangePolicy.Automatic, ChangePolicy.Review, new CleanupSettings()), CancellationToken.None);

        Assert.Equal(ResultStatus.CantWrite, result.Status);
        Assert.False(processor.NeedsCheck(path, SubtitleFiles.Fingerprint(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken))));
    }

    [Fact]
    public void A_failure_waits_before_it_is_tried_again_unless_the_file_changes()
    {
        var path = Path.Combine(_dir, "Film.en.srt");
        File.WriteAllText(path, "1\n00:00:01,000 --> 00:00:02,000\nHello\n");
        var processor = new SubtitleProcessor(new ResultStore(Path.Combine(_dir, "results.json")), new SubtitleFiles(Path.Combine(_dir, "originals")));

        processor.RecordFailure(new SubtitleJob(Guid.NewGuid(), "Invented Film", Path.Combine(_dir, "Film.mkv"), path, "eng", TimeSpan.FromMinutes(25), 0), "Odd file.");

        Assert.False(processor.NeedsCheck(path, SubtitleFiles.Fingerprint(File.ReadAllBytes(path))));
        Assert.True(processor.NeedsCheck(path, "changed"));
    }

    // SUB-17
    [Theory]
    [InlineData("１:２３:４５,６７８")]
    [InlineData("0١:23:45,678")]
    public void Non_ascii_digits_are_simply_not_a_timecode(string text)
        => Assert.False(Timecode.TryParse(text, out _));

    [Fact]
    public void Random_bytes_and_odd_digits_never_throw_from_the_reader()
    {
        var random = new Random(17);
        for (var i = 0; i < 300; i++)
        {
            var bytes = new byte[random.Next(0, 400)];
            random.NextBytes(bytes);
            _ = SubtitleReader.Read(bytes, "x.srt");
            _ = SubtitleReader.Read(bytes, "x.ass");
            _ = SubtitleReader.Read(bytes, "x.vtt");
        }

        _ = SubtitleReader.Read(Encoding.UTF8.GetBytes("1\n０:00:01,000 --> 00:00:0２,000\nHello\n"), "x.srt");
    }

    // SUB-21
    [Fact]
    public void A_bare_ffmpeg_name_is_found_on_the_path()
    {
        var bin = Path.Combine(_dir, "bin");
        Directory.CreateDirectory(bin);
        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        File.WriteAllText(Path.Combine(bin, name), string.Empty);

        Assert.Equal(Path.Combine(bin, name), FfmpegLocator.Resolve("ffmpeg", "relative" + Path.PathSeparator + bin));
        Assert.Null(FfmpegLocator.Resolve("ffmpeg", Path.Combine(_dir, "empty")));
        Assert.Null(FfmpegLocator.Resolve("tools/ffmpeg", bin));
        Assert.Equal(Path.Combine(bin, name), FfmpegLocator.Resolve(Path.Combine(bin, name)));
        Assert.Null(FfmpegLocator.Resolve(Path.Combine(bin, "missing")));
    }

    // SUB-28
    [Fact]
    public async Task A_timing_matched_by_meaning_waits_for_review_even_when_timing_fixes_are_automatic()
    {
        var path = Path.Combine(_dir, "Film.en.srt");
        await File.WriteAllBytesAsync(path, SubtitleWriter.ToBytes(PipelineTests.Story()), TestContext.Current.CancellationToken);
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var processor = new SubtitleProcessor(new ResultStore(Path.Combine(_dir, "results.json")), new SubtitleFiles(Path.Combine(_dir, "originals")));
        var job = new SubtitleJob(Guid.NewGuid(), "Invented Film", Path.Combine(_dir, "Film.mkv"), path, "eng", TimeSpan.FromMinutes(25), 0);
        var audio = new Translated(PipelineTests.Story(), 7);
        var matcher = new ByNumber();

        var result = await processor.ProcessAsync(job, audio, audio, new Policies(ChangePolicy.Automatic, ChangePolicy.Review, new CleanupSettings(), matcher), CancellationToken.None);

        Assert.Equal(ResultStatus.Proposed, result.Status);
        Assert.Equal(7, result.Offset, 1);
        Assert.Contains(result.Examples, e => e.StartsWith("Matched by meaning", StringComparison.Ordinal));
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    // Audio that says each line in another language, later than the subtitles say
    private sealed class Translated(SubtitleDocument said, double lateBy) : IAudioSource, SpeechToText.ISpeechToText
    {
        private TimeSpan _start;

        public string Id => "fake";

        public Task<float[]> ReadAsync(TimeSpan start, TimeSpan length, CancellationToken cancellationToken)
        {
            _start = start;
            return Task.FromResult(new float[(int)(length.TotalSeconds * AudioFormat.SampleRate)]);
        }

        public Task<SpeechToText.Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken cancellationToken)
        {
            var words = new List<SpeechToText.TranscribedWord>();
            for (var i = 0; i < said.Cues.Count; i++)
            {
                var at = said.Cues[i].Start.TotalSeconds + lateBy - _start.TotalSeconds;
                if (at is >= 0 and <= 58)
                {
                    words.AddRange($"Linea {i} de la historia inventada.".Split(' ').Select((w, k) => new SpeechToText.TranscribedWord(w, at + (k * 0.3), at + (k * 0.3) + 0.25, 0.9)));
                }
            }

            return Task.FromResult(new SpeechToText.Transcript(words, "es", "fake", "fake", 60));
        }
    }

    private sealed class ByNumber : ILineMatcher
    {
        public Task<LineMatch> MatchAsync(IReadOnlyList<HeardPhrase> phrases, IReadOnlyList<CueLine> cues, string? language, CancellationToken cancellationToken)
        {
            static string Number(string s) => new(s.SkipWhile(c => !char.IsAsciiDigit(c)).TakeWhile(char.IsAsciiDigit).ToArray());
            var pairs = phrases.Select(p => new LinePair(p.Index, cues.ToList().FindIndex(c => Number(c.Text) == Number(p.Text)))).Where(p => p.Cue >= 0).ToList();
            return Task.FromResult(new LineMatch(LineVerdict.SameContent, pairs, "Translated.", "AI (test)"));
        }
    }
}
