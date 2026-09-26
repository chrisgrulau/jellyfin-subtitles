using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Ai;
using Jellyfin.Plugin.Subtitles.Ai;
using Jellyfin.Plugin.Subtitles.Audit;
using Jellyfin.Plugin.Subtitles.Configuration;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.Pipeline;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Jellyfin.Plugin.Subtitles.Sync;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// Auditing a subtitle's wording against what is said. Invented text throughout.
public sealed class AuditTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "subs-audit-" + Guid.NewGuid().ToString("N"));

    public AuditTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class Auditor(Func<IReadOnlyList<AuditLine>, AuditAnswer> answer) : ITextAuditor
    {
        public int Calls { get; private set; }

        public IReadOnlyList<AuditLine>? Offered { get; private set; }

        public Task<AuditAnswer> AuditAsync(IReadOnlyList<HeardPhrase> heard, IReadOnlyList<AuditLine> lines, string? language, CancellationToken cancellationToken)
        {
            Calls++;
            Offered = lines;
            return Task.FromResult(answer(lines));
        }
    }

    private (SubtitleProcessor Processor, SubtitleJob Job, string Path) Setup()
    {
        var path = Path.Combine(_dir, "Film.en.srt");
        File.WriteAllBytes(path, SubtitleWriter.ToBytes(PipelineTests.Story()));
        var processor = new SubtitleProcessor(new ResultStore(Path.Combine(_dir, "results.json")), new SubtitleFiles(Path.Combine(_dir, "originals")));
        return (processor, new SubtitleJob(Guid.NewGuid(), "Invented Film", Path.Combine(_dir, "Film.mkv"), path, "eng", TimeSpan.FromMinutes(25), 0), path);
    }

    private static Policies With(ITextAuditor auditor) => new(ChangePolicy.Automatic, ChangePolicy.Review, new CleanupSettings(), null, auditor);

    [Fact]
    public async Task A_line_that_differs_is_flagged_applied_on_request_and_undone()
    {
        var (processor, job, path) = Setup();
        var original = File.ReadAllBytes(path);
        var fake = new PipelineTests.Shifted(PipelineTests.Story(), 2.5);
        var auditor = new Auditor(lines => new AuditAnswer([new AuditFinding(0, "name", "Line zero says Marta", "The name heard is Marta.")], string.Empty, "AI (test)"));

        var result = await processor.ProcessAsync(job, fake, fake, With(auditor), CancellationToken.None);

        Assert.Equal(ResultStatus.Corrected, result.Status);
        Assert.Equal(1, auditor.Calls);
        var finding = Assert.Single(result.Findings);
        Assert.True(result.PendingReview);
        Assert.Contains("→", result.Examples[0], StringComparison.Ordinal);
        Assert.Contains("Wording audited (AI (test))", result.Explanation, StringComparison.Ordinal);

        // The finding names the line as the corrected file has it, on the audio's clock
        var saved = SubtitleReader.Read(File.ReadAllBytes(path), path)!;
        Assert.Contains(saved.Cues, c => c.Text == finding.Current && Math.Abs(c.Start.TotalSeconds - finding.Time) < 0.01);
        Assert.Equal(auditor.Offered![0].At, finding.Time, 1);

        var applied = processor.Apply(result.Id, With(auditor));
        var reworded = SubtitleReader.Read(File.ReadAllBytes(path), path)!;
        Assert.Contains(reworded.Cues, c => c.Text == "Line zero says Marta");
        Assert.Equal(1, applied.Cleaned[SubtitleProcessor.RewordedKind]);
        Assert.Empty(applied.Findings);
        Assert.False(applied.PendingReview);

        processor.Undo(result.Id);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task No_differences_is_said_and_nothing_waits()
    {
        var (processor, job, _) = Setup();
        var fake = new PipelineTests.Shifted(PipelineTests.Story(), 0);

        var result = await processor.ProcessAsync(job, fake, fake, With(new Auditor(_ => new AuditAnswer([], string.Empty, "AI (test)"))), CancellationToken.None);

        Assert.Equal(ResultStatus.InSync, result.Status);
        Assert.Empty(result.Findings);
        Assert.False(result.PendingReview);
        Assert.Contains("no differences in meaning", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_speech_to_text_nothing_is_audited()
    {
        var (processor, job, _) = Setup();
        var auditor = new Auditor(_ => throw new InvalidOperationException("not asked"));

        await processor.ProcessAsync(job, new PipelineTests.Shifted(PipelineTests.Story(), 2.5), null, With(auditor), CancellationToken.None);

        Assert.Equal(0, auditor.Calls);
    }

    [Fact]
    public void Only_offered_lines_of_known_kinds_count_once_each()
    {
        var file = PipelineTests.Story();
        IReadOnlyList<AuditLine> lines = [new(0, 10, "Line 0 says word0x and word0y"), new(1, 15, "Line 1 says word1x and word1y")];
        var answer = new AuditAnswer(
            [new(5, "name", "x", "r"), new(0, "style", "x", "r"), new(0, "number", "Line 0 says 7", "r"), new(0, "name", "y", "r"), new(1, "wrong", "Line 1 says word1x and word1y", "r")],
            string.Empty,
            "AI (test)");

        var findings = WordingAudit.Findings(answer, lines, [0, 1], file);

        Assert.Equal(2, findings.Count);
        Assert.Equal("Line 0 says 7", findings[0].Suggestion);
        Assert.Null(findings[1].Suggestion);
        Assert.Equal(15, findings[1].Time);
    }

    [Fact]
    public void Suggestions_apply_only_to_lines_still_as_found()
    {
        var file = PipelineTests.Story();
        var findings = new[]
        {
            new LineFinding(10, file.Cues[0].Text, "Changed zero", "wrong", "r"),
            new LineFinding(15, "Something the file no longer says", "Changed one", "wrong", "r"),
            new LineFinding(20, file.Cues[2].Text, null, "name", "r"),
        };

        var (document, changed) = WordingAudit.Apply(file, findings);

        Assert.Equal(1, changed);
        Assert.Equal("Changed zero", document.Cues[0].Text);
        Assert.Equal(file.Cues[1].Text, document.Cues[1].Text);
        Assert.Equal(file.Cues[2].Text, document.Cues[2].Text);
    }

    [Fact]
    public void Lines_are_those_shown_during_the_heard_stretches_on_the_audio_clock()
    {
        var transcripts = new List<(double, Transcript)> { (100, new Transcript([], "en", "f", "f", 60)) };

        var (lines, cues) = WordingAudit.Lines(PipelineTests.Story(), t => t + TimeSpan.FromSeconds(10), transcripts);

        Assert.All(lines, l => Assert.InRange(l.At, 100, 160));
        Assert.Equal(lines.Count, cues.Count);
        Assert.NotEmpty(lines);
        Assert.All(cues, c => Assert.InRange(PipelineTests.Story().Cues[c].Start.TotalSeconds + 10, 100, 160));
    }

    private static AiReply Ok(string json) => new(true, JsonDocument.Parse(json).RootElement.Clone(), "claude-opus-5-5", null, null);

    [Fact]
    public async Task The_AI_auditor_sends_only_phrases_and_lines_and_shares_the_run_allowance()
    {
        string? sent = null;
        var checks = new AiChecks(1);
        var auditor = new AiTextAuditor(checks, (caller, purpose, instructions, data, schema, max, effort, ct) =>
        {
            sent = JsonSerializer.Serialize(data);
            Assert.Equal("subtitles.audit", purpose);
            return Task.FromResult(Ok("{\"findings\":[{\"line\":0,\"kind\":\"number\",\"suggestion\":\"Seven.\",\"reason\":\"Heard seven.\"},{\"line\":\"x\",\"kind\":\"name\"}]}"));
        });

        var answer = await auditor.AuditAsync([new(0, 1, "Seven.")], [new(0, 1, "Six.")], "en", TestContext.Current.CancellationToken);
        var matcher = new AiLineMatcher(checks, (_, _, _, _, _, _, _, _) => throw new InvalidOperationException("not asked"));
        var refused = await matcher.MatchAsync([new(0, 1, "a")], [new(0, 1, "b")], "en", TestContext.Current.CancellationToken);

        Assert.Equal([new AuditFinding(0, "number", "Seven.", "Heard seven.")], answer.Findings);
        Assert.Equal("AI (claude-opus-5-5)", answer.By);
        Assert.Contains("limit", refused.Note, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(sent!);
        Assert.Equal(["subtitleLanguage", "heard", "lines"], doc.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(string.Empty, AiTextAuditor.Read(new AiReply(false, null, null, "No.", "not-allowed")).Note);
    }

    private static readonly Policies Plain = new(ChangePolicy.Automatic, ChangePolicy.Review, new CleanupSettings());

    [Fact]
    public async Task A_subtitle_checked_before_the_audit_is_audited_once_later()
    {
        var (processor, job, path) = Setup();
        var fake = new PipelineTests.Shifted(PipelineTests.Story(), 0);
        await processor.ProcessAsync(job, fake, fake, Plain, CancellationToken.None);
        Assert.True(processor.NeedsAudit(path));
        var auditor = new Auditor(_ => new AuditAnswer([new AuditFinding(1, "number", "Line 1 says 7", "Heard seven.")], string.Empty, "AI (test)"));

        var audited = await processor.AuditAsync(job, fake, fake, auditor, CancellationToken.None);

        Assert.NotNull(audited);
        Assert.True(audited.Audited);
        Assert.Single(audited.Findings);
        Assert.True(audited.PendingReview);
        Assert.False(processor.NeedsAudit(path));
        Assert.Null(await processor.AuditAsync(job, fake, fake, auditor, CancellationToken.None));
        Assert.Equal(1, auditor.Calls);
    }

    [Fact]
    public async Task No_answer_leaves_it_for_another_run_and_a_changed_file_is_left_alone()
    {
        var (processor, job, path) = Setup();
        var fake = new PipelineTests.Shifted(PipelineTests.Story(), 0);
        await processor.ProcessAsync(job, fake, fake, Plain, CancellationToken.None);

        var none = await processor.AuditAsync(job, fake, fake, new Auditor(_ => new AuditAnswer([], "The wording wasn't audited: this run's limit of AI checks was reached.", null)), CancellationToken.None);

        Assert.Null(none);
        Assert.True(processor.NeedsAudit(path));
        await File.AppendAllTextAsync(path, "\n", TestContext.Current.CancellationToken);
        Assert.False(processor.NeedsAudit(path, SubtitleFiles.Fingerprint(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken))));
    }

    [Fact]
    public async Task A_subtitle_no_longer_in_sync_isnt_audited_or_tried_again()
    {
        var (processor, job, path) = Setup();
        var inSync = new PipelineTests.Shifted(PipelineTests.Story(), 0);
        await processor.ProcessAsync(job, inSync, inSync, Plain, CancellationToken.None);
        var late = new PipelineTests.Shifted(PipelineTests.Story(), 3);
        var auditor = new Auditor(_ => throw new InvalidOperationException("not asked"));

        var result = await processor.AuditAsync(job, late, late, auditor, CancellationToken.None);

        Assert.Equal(0, auditor.Calls);
        Assert.True(result!.Audited);
        Assert.Contains("didn't find it in sync", result.Explanation, StringComparison.Ordinal);
        Assert.False(processor.NeedsAudit(path));
    }
}
