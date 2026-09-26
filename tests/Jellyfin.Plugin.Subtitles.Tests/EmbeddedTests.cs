using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.Audio;
using Jellyfin.Plugin.Subtitles.Configuration;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.Pipeline;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

public sealed class EmbeddedTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "subs-embedded-" + Guid.NewGuid().ToString("N"));
    private static readonly Policies Auto = new(ChangePolicy.Automatic, ChangePolicy.Review, new CleanupSettings());

    public EmbeddedTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private (EmbeddedChecker Checker, SubtitleProcessor Processor, EmbeddedJob Job) Setup()
    {
        var video = Path.Combine(_dir, "Invented Film (2020).mkv");
        File.WriteAllText(video, "not really a video");
        var store = new ResultStore(Path.Combine(_dir, "results.json"));
        var job = new EmbeddedJob(Guid.NewGuid(), "Invented Film", video, 3, "subrip", "eng", TimeSpan.FromMinutes(25), 0, EmbeddedChecker.FingerprintOf(video, 3));
        return (new EmbeddedChecker(store), new SubtitleProcessor(store, new SubtitleFiles(Path.Combine(_dir, "originals"))), job);
    }

    [Fact]
    public async Task A_late_track_gets_a_corrected_copy_beside_the_video_which_undo_removes()
    {
        var (checker, processor, job) = Setup();
        var fake = new PipelineTests.Shifted(PipelineTests.Story(), 2.5);

        var result = await checker.CheckAsync(job, SubtitleWriter.ToBytes(PipelineTests.Story()), fake, fake, Auto, TestContext.Current.CancellationToken);

        var added = Path.Combine(_dir, "Invented Film (2020).en.srt");
        Assert.Equal(ResultStatus.Added, result.Status);
        Assert.Equal(added, result.SubtitlePath);
        Assert.Equal(12.5, SubtitleReader.Read(File.ReadAllBytes(added), added)!.Cues[0].Start.TotalSeconds, 0.05);
        Assert.Equal("not really a video", File.ReadAllText(job.VideoPath));
        Assert.False(checker.NeedsCheck(job));

        processor.Undo(result.Id);
        Assert.False(File.Exists(added));
        Assert.False(checker.NeedsCheck(job));
    }

    [Fact]
    public async Task A_track_in_time_is_recorded_and_not_read_again_until_the_video_changes()
    {
        var (checker, _, job) = Setup();
        var fake = new PipelineTests.Shifted(PipelineTests.Story(), 0);

        var result = await checker.CheckAsync(job, SubtitleWriter.ToBytes(PipelineTests.Story()), fake, fake, Auto, TestContext.Current.CancellationToken);

        Assert.Equal(ResultStatus.InSync, result.Status);
        Assert.Empty(Directory.GetFiles(_dir, "*.srt"));
        Assert.False(checker.NeedsCheck(job));
        Assert.True(checker.NeedsCheck(job with { Fingerprint = "embedded-other" }));
    }

    [Fact]
    public void A_failed_copy_is_not_retried_every_night()
    {
        var (checker, _, job) = Setup();
        checker.RecordFailure(job, "ffmpeg failed");

        Assert.False(checker.NeedsCheck(job));
    }

    [Fact]
    public void The_track_is_copied_with_an_argument_list_and_a_file_prefix()
    {
        var args = FfmpegSubtitleExtractor.Arguments("/media/-odd name.mkv", 4, "ass");

        Assert.Contains("file:/media/-odd name.mkv", args);
        Assert.Equal("0:4", args[Array.IndexOf(args, "-map") + 1]);
        Assert.Equal("ass", args[Array.IndexOf(args, "-f") + 1]);
        Assert.Equal("srt", FfmpegSubtitleExtractor.OutputFormat("mov_text"));
    }
}
