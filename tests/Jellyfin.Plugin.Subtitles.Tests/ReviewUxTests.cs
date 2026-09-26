using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.Configuration;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.Pipeline;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// SUB-19 and SUB-20: declining, checking again, unclear results after speech-to-text changes, waiting items first.
public sealed class ReviewUxTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "subs-ux-" + Guid.NewGuid().ToString("N"));

    public ReviewUxTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private (SubtitleProcessor Processor, ResultStore Store, SubtitleJob Job, string Path) Setup(string name = "Film.en.srt")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, SubtitleWriter.ToBytes(PipelineTests.Story()));
        var store = new ResultStore(Path.Combine(_dir, "results.json"));
        var processor = new SubtitleProcessor(store, new SubtitleFiles(Path.Combine(_dir, "originals")));
        return (processor, store, new SubtitleJob(Guid.NewGuid(), "Invented Film", Path.Combine(_dir, "Film.mkv"), path, "eng", TimeSpan.FromMinutes(25), 0), path);
    }

    private static readonly Policies Review = new(ChangePolicy.Review, ChangePolicy.Review, new CleanupSettings());

    [Fact]
    public async Task A_proposal_can_be_declined_and_nothing_changes()
    {
        var (processor, _, job, path) = Setup();
        var before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var late = new PipelineTests.Shifted(PipelineTests.Story(), 2.5);
        var proposed = await processor.ProcessAsync(job, late, late, Review, CancellationToken.None);
        Assert.Equal(ResultStatus.Proposed, proposed.Status);

        var declined = processor.Decline(proposed.Id);

        Assert.Equal(ResultStatus.Declined, declined.Status);
        Assert.False(declined.PendingReview);
        Assert.Equal(before, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.False(processor.NeedsCheck(path, SubtitleFiles.Fingerprint(before)));
        Assert.Throws<InvalidOperationException>(() => processor.Decline(proposed.Id));
    }

    [Fact]
    public async Task Check_again_forgets_the_result_but_not_for_a_file_holding_our_changes()
    {
        var (processor, _, job, path) = Setup();
        var inSync = new PipelineTests.Shifted(PipelineTests.Story(), 0);
        var result = await processor.ProcessAsync(job, inSync, inSync, Review, CancellationToken.None);
        var fingerprint = SubtitleFiles.Fingerprint(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.False(processor.NeedsCheck(path, fingerprint));

        processor.CheckAgain(result.Id);
        Assert.True(processor.NeedsCheck(path, fingerprint));

        var late = new PipelineTests.Shifted(PipelineTests.Story(), 2.5);
        var corrected = await processor.ProcessAsync(job, late, late, new Policies(ChangePolicy.Automatic, ChangePolicy.Review, new CleanupSettings()), CancellationToken.None);
        Assert.True(corrected.Changed);
        Assert.Throws<InvalidOperationException>(() => processor.CheckAgain(corrected.Id));
    }

    [Fact]
    public async Task An_unclear_result_is_checked_again_once_the_speech_to_text_service_changes()
    {
        var (processor, _, job, path) = Setup();
        var silent = new PipelineTests.Shifted(PipelineTests.Story(), 2.5);
        var unclear = await processor.ProcessAsync(job, silent, null, Review, CancellationToken.None);
        Assert.Equal(ResultStatus.Unreliable, unclear.Status);
        var fingerprint = SubtitleFiles.Fingerprint(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));

        Assert.False(processor.NeedsCheck(path, fingerprint, string.Empty));
        Assert.True(processor.NeedsCheck(path, fingerprint, "builtin"));
    }

    [Fact]
    public async Task Items_waiting_for_review_come_first()
    {
        var (processor, _, job, _) = Setup();
        var late = new PipelineTests.Shifted(PipelineTests.Story(), 2.5);
        var waiting = await processor.ProcessAsync(job, late, late, Review, CancellationToken.None);
        var (_, _, other, _) = Setup("Other.en.srt");
        var inSync = new PipelineTests.Shifted(PipelineTests.Story(), 0);
        await processor.ProcessAsync(other with { Name = "Other" }, inSync, inSync, Review, CancellationToken.None);

        Assert.Equal(waiting.Id, processor.Recent(1)[0].Id);
        Assert.Equal(2, processor.Recent(1).Count);
    }
}
