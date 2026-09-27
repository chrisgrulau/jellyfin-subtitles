using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.Ai;
using Jellyfin.Plugin.Subtitles.Api;
using Jellyfin.Plugin.Subtitles.Audit;
using Jellyfin.Plugin.Subtitles.Configuration;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.Pipeline;
using Jellyfin.Plugin.Subtitles.Sync;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// What the "What changed" list and the explanation say once held-back changes are applied or declined. Invented text.
public sealed class ReviewTextTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "subs-reviewtext-" + Guid.NewGuid().ToString("N"));

    public ReviewTextTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class Auditor(IReadOnlyList<AuditFinding> findings) : ITextAuditor
    {
        public Task<AuditAnswer> AuditAsync(IReadOnlyList<HeardPhrase> heard, IReadOnlyList<AuditLine> lines, string? language, CancellationToken cancellationToken)
            => Task.FromResult(new AuditAnswer(findings, string.Empty, "AI (test)"));
    }

    // A repeated line (its merge waits for review) and a suggested wording or two from the audit, waiting together
    private async Task<(SubtitleProcessor Processor, SubtitleResult Result, Policies Policies)> Waiting(params AuditFinding[] findings)
    {
        var story = PipelineTests.Story();
        var cues = story.Cues.ToList();
        cues.Insert(5, new SubtitleCue { Start = cues[4].End - TimeSpan.FromSeconds(0.5), End = cues[4].End + TimeSpan.FromSeconds(1), Text = cues[4].Text });
        var path = Path.Combine(_dir, "Film.en.srt");
        File.WriteAllBytes(path, SubtitleWriter.ToBytes(story with { Cues = cues }));
        var processor = new SubtitleProcessor(new ResultStore(Path.Combine(_dir, "results.json")), new SubtitleFiles(Path.Combine(_dir, "originals")));
        var job = new SubtitleJob(Guid.NewGuid(), "Invented Film", Path.Combine(_dir, "Film.mkv"), path, "eng", TimeSpan.FromMinutes(25), 0);
        var policies = new Policies(ChangePolicy.Automatic, ChangePolicy.Review, new CleanupSettings(), null, new Auditor(findings));
        var fake = new PipelineTests.Shifted(PipelineTests.Story(), 0);

        var result = await processor.ProcessAsync(job, fake, fake, policies, CancellationToken.None);

        Assert.True(result.PendingReview);
        Assert.Contains(result.Examples, e => e.StartsWith("Waiting for review: Merged repeated line", StringComparison.Ordinal));
        Assert.Contains(ReviewText.SuggestionWaits, result.Explanation, StringComparison.Ordinal);
        return (processor, result, policies);
    }

    private static AuditFinding Name(int line) => new(line, "name", "Line says Marta", "The name heard is Marta.");

    [Fact]
    public async Task Applying_clean_up_and_suggested_wording_says_it_was_applied()
    {
        var (processor, result, policies) = await Waiting(Name(0));
        var suggestion = WordingAudit.Describe(Assert.Single(result.Findings));

        var applied = processor.Apply(result.Id, policies);

        Assert.False(applied.PendingReview);
        Assert.DoesNotContain(applied.Examples, e => e.Contains("Waiting for review", StringComparison.Ordinal));
        Assert.Contains(applied.Examples, e => e.StartsWith("Merged repeated line", StringComparison.Ordinal));
        Assert.Contains(ReviewText.AppliedPrefix + suggestion, applied.Examples);
        Assert.DoesNotContain(ReviewText.SuggestionWaits, applied.Explanation, StringComparison.Ordinal);
        Assert.Contains(ReviewText.SuggestionApplied, applied.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Declining_drops_what_was_waiting_and_says_it_was_declined()
    {
        var (processor, result, _) = await Waiting(Name(0));
        var suggestion = WordingAudit.Describe(Assert.Single(result.Findings));

        var declined = processor.Decline(result.Id);

        Assert.False(declined.PendingReview);
        Assert.DoesNotContain(declined.Examples, e => e.Contains("Merged repeated line", StringComparison.Ordinal));
        Assert.DoesNotContain(declined.Examples, e => e.Contains(suggestion, StringComparison.Ordinal));
        Assert.Contains(ReviewText.SuggestionDeclined, declined.Explanation, StringComparison.Ordinal);
        Assert.DoesNotContain(ReviewText.SuggestionWaits, declined.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Applying_one_finding_marks_its_line_and_the_rest_keep_waiting()
    {
        var (processor, result, _) = await Waiting(Name(0), new AuditFinding(1, "number", "Line one says 7", "Heard seven."));
        Assert.Equal(2, result.Findings.Count);
        var first = WordingAudit.Describe(result.Findings[0]);
        var second = WordingAudit.Describe(result.Findings[1]);

        var one = processor.ApplyFinding(result.Id, 0, result.Findings[0].Time);

        Assert.Contains(ReviewText.AppliedPrefix + first, one.Examples);
        Assert.DoesNotContain(first, one.Examples);
        Assert.Contains(second, one.Examples);
        Assert.Contains(ReviewText.SuggestionWaits, one.Explanation, StringComparison.Ordinal);
        Assert.Contains(one.Examples, e => e.StartsWith("Waiting for review: Merged repeated line", StringComparison.Ordinal));

        var rest = processor.DeclineFinding(result.Id, 0, one.Findings[0].Time);

        Assert.DoesNotContain(second, rest.Examples);
        Assert.Contains(ReviewText.AppliedPrefix + first, rest.Examples);
        Assert.Contains(ReviewText.SuggestionPartly, rest.Explanation, StringComparison.Ordinal);

        // The clean-up still waits, so its line still says so
        Assert.Contains(rest.Examples, e => e.StartsWith("Waiting for review: Merged repeated line", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Applying_all_suggestions_marks_each_applied_line()
    {
        var (processor, result, _) = await Waiting(Name(0), new AuditFinding(1, "number", "Line one says 7", "Heard seven."));
        var lines = result.Findings.Select(WordingAudit.Describe).ToList();

        var (after, applied) = processor.ApplyFindings(result.Id);

        Assert.Equal(2, applied);
        Assert.All(lines, l => Assert.Contains(ReviewText.AppliedPrefix + l, after.Examples));
        Assert.Contains(ReviewText.SuggestionApplied, after.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_result_saved_with_stale_waiting_text_is_shown_as_applied_once_nothing_waits()
    {
        var r = new SubtitleResult
        {
            Id = "0123456789abcdef",
            SubtitlePath = "/media/Invented Film (2019)/Invented Film (2019).en.srt",
            Name = "Invented Film",
            Status = ResultStatus.InSync,
            Changed = true,
            Time = DateTimeOffset.UnixEpoch,
            Cleaned = new Dictionary<string, int> { ["StrippedHearingImpaired"] = 2, [SubtitleProcessor.RewordedKind] = 1 },
            Examples =
            [
                "0:01:02 (name): “Hello, Pip.” → “Hello, Pim.” — The name heard is Pim.",
                "Waiting for review: Removed sound description: “CROWD: Hurrah.” → “Hurrah.”",
                "Waiting for review: Removed sound description: “[door creaks]” → “”",
            ],
            Explanation = "In sync. Wording audited (AI (test)): 1 line(s) differ in meaning from what is said; Apply uses the suggested wording.",
        };

        var view = ResultPresenter.Present(r, null, DateTimeOffset.UnixEpoch, TimeZoneInfo.Utc, canRerun: false);

        Assert.DoesNotContain(view.Examples, e => e.Contains("Waiting for review", StringComparison.Ordinal));
        Assert.Contains("Removed sound description: “CROWD: Hurrah.” → “Hurrah.”", view.Examples);
        Assert.Equal(3, view.Examples.Count);
        var explanation = view.NerdStats.Single(s => s.Name == "Explanation").Value;
        Assert.EndsWith("from what is said" + ReviewText.SuggestionApplied, explanation, StringComparison.Ordinal);

        // While something still waits, it says so
        var waiting = ResultPresenter.Present(r with { CleanupPending = new Dictionary<string, int> { ["StrippedHearingImpaired"] = 2 } }, null, DateTimeOffset.UnixEpoch, TimeZoneInfo.Utc, canRerun: false);
        Assert.Equal(2, waiting.Examples.Count(e => e.StartsWith(ReviewText.WaitingPrefix, StringComparison.Ordinal)));

        // A declined review drops them
        var declined = ResultPresenter.Present(r with { Explanation = r.Explanation + " Declined in review: nothing was changed.", Cleaned = new Dictionary<string, int>() }, null, DateTimeOffset.UnixEpoch, TimeZoneInfo.Utc, canRerun: false);
        Assert.Single(declined.Examples);
        Assert.Contains(ReviewText.SuggestionDeclined, declined.NerdStats.Single(s => s.Name == "Explanation").Value, StringComparison.Ordinal);
    }
}
