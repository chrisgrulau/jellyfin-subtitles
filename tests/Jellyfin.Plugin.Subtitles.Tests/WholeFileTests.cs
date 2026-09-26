using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Resilience;
using Jellyfin.Plugin.Subtitles.Audit;
using Jellyfin.Plugin.Subtitles.Candidates;
using Jellyfin.Plugin.Subtitles.Discrepancy;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.Generation;
using Jellyfin.Plugin.Subtitles.Pipeline;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Jellyfin.Plugin.Subtitles.Sync;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// Stage 4: doubtful subtitles compared whole with a full transcript, and the findings reviewed. Invented lines throughout.
public sealed class WholeFileTests : IDisposable
{
    private const string Setup = "builtin/base";
    private static readonly string[] English = ["eng"];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "subs-wholefile-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private readonly ResultStore _store;
    private readonly TranscriptCache _cache;
    private readonly WholeFileChecker _checker;
    private readonly SubtitleProcessor _processor;

    public WholeFileTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new ResultStore(Path.Combine(_dir, "results.json"));
        _cache = new TranscriptCache(Path.Combine(_dir, "transcripts"));
        _checker = new WholeFileChecker(_store, _cache, new ConfidenceCalibration(Path.Combine(_dir, "calibration.json")), _clock);
        _processor = new SubtitleProcessor(_store, new SubtitleFiles(Path.Combine(_dir, "originals")), _clock);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    // Hears the given words (the video is shorter than one chunk, so they are on the video's clock)
    private sealed class Hears(IReadOnlyList<TranscribedWord> words, string id = "builtin", Action? during = null) : ISpeechToText
    {
        public int Calls { get; private set; }

        public string Id => id;

        public Task<Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken cancellationToken)
        {
            Calls++;
            during?.Invoke();
            return Task.FromResult(new Transcript(words, language, id, "base", samples.Length / 16000.0));
        }
    }

    private sealed class Refusing : ISpeechToText
    {
        public string Id => "deepgram";

        public Task<Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken cancellationToken)
            => throw new SpeechToTextException("limit reached") { Failure = FailureClass.ProviderLimit };
    }

    private sealed class Auditor(Func<IReadOnlyList<AuditLine>, AuditAnswer> answer) : ITextAuditor
    {
        public Task<AuditAnswer> AuditAsync(IReadOnlyList<HeardPhrase> heard, IReadOnlyList<AuditLine> lines, string? language, CancellationToken cancellationToken)
            => Task.FromResult(answer(lines));
    }

    private static readonly Policies Policies = new(Configuration.ChangePolicy.Automatic, Configuration.ChangePolicy.Review, new Configuration.CleanupSettings());

    private static WholeFileSettings Settings(ITextAuditor? auditor = null) => new(English, false, auditor);

    // A subtitle file beside a video, and its result
    private SubtitleJob File_(string name, IEnumerable<(double Start, string Text)> lines, ResultStatus status = ResultStatus.Unreliable, string? audio = "eng", Func<SubtitleResult, SubtitleResult>? change = null, string? subtitle = null)
    {
        var video = Path.Combine(_dir, name + ".mkv");
        File.WriteAllText(video, name);
        var path = Path.Combine(_dir, subtitle ?? name + ".en.srt");
        var bytes = SubtitleWriter.ToBytes(DiscrepancyTests.Subtitle(lines));
        File.WriteAllBytes(path, bytes);
        var result = new SubtitleResult
        {
            Id = ResultStore.IdFor(path),
            Name = name,
            SubtitlePath = path,
            Status = status,
            Stage = status == ResultStatus.Unreliable ? "line starts" : WholeFileChecker.SpeechStage,
            Confidence = 0.9,
            Fingerprint = SubtitleFiles.Fingerprint(bytes),
            Time = _clock.Now,
            Explanation = "Checked.",
        };
        _store.Put(change?.Invoke(result) ?? result);
        return new SubtitleJob(Guid.NewGuid(), name, video, path, "eng", TimeSpan.FromMinutes(1), 0, audio);
    }

    private SubtitleResult Result(SubtitleJob job) => _store.Get(ResultStore.IdFor(job.SubtitlePath))!;

    private Task<SubtitleResult?> Check(SubtitleJob job, ISpeechToText speech, WholeFileSettings? settings = null)
        => _checker.CheckAsync(job, new TranscriberTests.FakeAudio(job.Duration), speech, Setup, settings ?? Settings(), TestContext.Current.CancellationToken);

    private static (double, string)[] Changed(int line, string text)
    {
        var copy = DiscrepancyTests.Script.ToArray();
        copy[line] = (copy[line].Start, text);
        return copy;
    }

    [Fact]
    public void Only_doubtful_subtitles_are_checked_on_their_own()
    {
        Assert.True(WholeFileChecker.IsDoubtful(new SubtitleResult { Id = "a", SubtitlePath = "a", Status = ResultStatus.Unreliable }));
        Assert.True(WholeFileChecker.IsDoubtful(new SubtitleResult { Id = "a", SubtitlePath = "a", Status = ResultStatus.InSync, Stage = WholeFileChecker.SpeechStage, Confidence = 0.3 }));
        Assert.True(WholeFileChecker.IsDoubtful(new SubtitleResult { Id = "a", SubtitlePath = "a", Status = ResultStatus.InSync, Stage = WholeFileChecker.SpeechStage, Confidence = 0.9, Findings = [new LineFinding(1, "x", null, "name", "r")] }));
        Assert.False(WholeFileChecker.IsDoubtful(new SubtitleResult { Id = "a", SubtitlePath = "a", Status = ResultStatus.InSync, Stage = WholeFileChecker.SpeechStage, Confidence = 0.9 }));
        Assert.False(WholeFileChecker.IsDoubtful(new SubtitleResult { Id = "a", SubtitlePath = "a", Status = ResultStatus.InSync, Stage = "line starts", Confidence = 0.1 }));
        Assert.False(WholeFileChecker.IsDoubtful(new SubtitleResult { Id = "a", SubtitlePath = "a", Status = ResultStatus.WrongLanguage }));
        Assert.False(WholeFileChecker.IsDoubtful(new SubtitleResult { Id = "a", SubtitlePath = "a", Status = ResultStatus.Unreliable, Stage = SyncCheck.ByMeaningStage }));
        Assert.False(WholeFileChecker.IsDoubtful(new SubtitleResult { Id = SubtitleGenerator.IdPrefix + "a", SubtitlePath = "a", Status = ResultStatus.Unreliable }));
    }

    [Fact]
    public void Candidates_are_the_ones_asked_for_then_doubtful_ones_in_the_audio_language_within_the_cap()
    {
        var script = DiscrepancyTests.Script;
        var unclear = File_("Unclear", script);
        _clock.Now = _clock.Now.AddMinutes(1);
        var weak = File_("Weak", script, ResultStatus.InSync, change: r => r with { Confidence = 0.2 });
        var good = File_("Good", script, ResultStatus.InSync);
        var asked = File_("Asked", script, ResultStatus.InSync, change: r => r with { WholeFileRequested = true });
        var french = File_("French audio", script, audio: "fre");
        var untagged = File_("Untagged", script, audio: null);
        var done = File_("Done", script, change: r => r with { WholeFile = new WholeFileCheck { Time = _clock.Now } });
        var failedLongAgo = File_("Failed", script, change: r => r with { WholeFile = new WholeFileCheck { Time = _clock.Now.AddDays(-4), Failed = true } });
        var generated = File_("Generated", script, subtitle: "Generated.en.generated.srt");
        var jobs = new[] { unclear, weak, good, asked, french, untagged, done, failedLongAgo, generated };

        var chosen = _checker.Choose(jobs, English, automatic: true, max: 10);
        Assert.Equal([asked, unclear, failedLongAgo, untagged, weak], chosen);

        // The cap; switched off, only those asked for
        Assert.Equal([asked, unclear], _checker.Choose(jobs, English, automatic: true, max: 2));
        Assert.Equal([asked], _checker.Choose(jobs, English, automatic: false, max: 10));
        Assert.Empty(_checker.Choose(jobs, English, automatic: true, max: 0));
        Assert.True(_checker.HasRequests);
    }

    [Fact]
    public async Task Differences_wait_for_review_with_what_was_heard_and_a_fix()
    {
        var job = File_("Film", DiscrepancyTests.Script, change: r => r with { WholeFileRequested = true });
        var heard = DiscrepancyTests.Said([.. Changed(6, "I have four tickets here."), (41, "Wait, where did my other shoe go now?")]);
        var result = (await Check(job, new Hears(heard)))!;

        Assert.False(result.WholeFileRequested);
        Assert.True(result.PendingReview);
        Assert.Equal(2, result.Findings.Count);
        Assert.All(result.Findings, f => Assert.Equal(DiscrepancyReview.WholeFile, f.From));
        var number = result.Findings.Single(f => f.Kind == DiscrepancyFinder.Number);
        Assert.Equal(25, number.Time, 3);
        Assert.Equal("I have five tickets here.", number.Current);
        Assert.Equal("I have four tickets here.", number.Heard);
        Assert.Equal("I have four tickets here.", number.Suggestion);
        var missing = result.Findings.Single(f => f.Kind == DiscrepancyFinder.MissingLine);
        Assert.Equal(string.Empty, missing.Current);
        Assert.True(missing.End > missing.Time);
        Assert.Equal(1, result.WholeFile!.Counts[DiscrepancyFinder.Number]);
        Assert.Equal(1, result.WholeFile.Counts[DiscrepancyFinder.MissingLine]);
        Assert.Contains("Whole file checked against a full transcript by the built-in speech-to-text", result.Explanation, StringComparison.Ordinal);
        Assert.Contains("1 number, 1 missing line", result.Explanation, StringComparison.Ordinal);
        Assert.StartsWith("Checked.", result.Explanation, StringComparison.Ordinal);
        Assert.Contains(result.Examples, e => e.Contains("(number)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_second_check_and_the_generator_reuse_the_cached_transcript()
    {
        var job = File_("Film", DiscrepancyTests.Script);
        var speech = new Hears(DiscrepancyTests.Said(DiscrepancyTests.Script));
        await Check(job, speech);
        Assert.Equal(1, speech.Calls);

        var again = await Check(job, speech);
        Assert.Equal(1, speech.Calls);
        Assert.Contains("transcript reused", again!.Explanation, StringComparison.Ordinal);
        Assert.Contains("no lines differ", again.Explanation, StringComparison.Ordinal);
        Assert.Equal(2, again.Explanation.Split(WholeFileChecker.Marker).Length);

        // Generating a subtitle for the same video, track, language and service doesn't transcribe it again either
        var generator = new SubtitleGenerator(_store, new SubtitleFiles(Path.Combine(_dir, "originals")), _clock, _cache);
        var find = new FindJob(Guid.NewGuid(), "Film", job.VideoPath, new VideoFacts { FileName = "Film.mkv", Duration = job.Duration }, "eng", job.Duration, 0, "eng");
        _store.Put(new SubtitleResult { Id = SubtitleFinder.IdFor(find.VideoPath, find.Language), SubtitlePath = "x", Status = ResultStatus.NotFound, Time = _clock.Now });
        var generated = await generator.GenerateAsync(find, new TranscriberTests.FakeAudio(find.Duration), speech, Setup, TestContext.Current.CancellationToken);
        Assert.Equal(ResultStatus.Generated, generated!.Status);
        Assert.Equal(1, speech.Calls);

        // Another service or model is transcribed afresh
        await _checker.CheckAsync(job, new TranscriberTests.FakeAudio(job.Duration), speech, "deepgram/nova-3", Settings(), TestContext.Current.CancellationToken);
        Assert.Equal(2, speech.Calls);
    }

    [Fact]
    public async Task Other_languages_generated_and_changed_files_are_not_compared()
    {
        var speech = new Hears(DiscrepancyTests.Said(DiscrepancyTests.Script));
        var french = File_("French", DiscrepancyTests.Script, audio: "fre", change: r => r with { WholeFileRequested = true });
        var noted = await Check(french, speech);
        Assert.Contains(WholeFileChecker.NotInLanguage, noted!.Explanation, StringComparison.Ordinal);
        Assert.False(noted.WholeFileRequested);
        Assert.Empty(noted.Findings);

        Assert.Null(await Check(File_("Generated", DiscrepancyTests.Script, subtitle: "Generated.en.generated.srt"), speech));

        var edited = File_("Edited", DiscrepancyTests.Script);
        File.AppendAllText(edited.SubtitlePath, "\n");
        Assert.Null(await Check(edited, speech));
        Assert.Equal(0, speech.Calls);
    }

    [Fact]
    public async Task A_proposed_timing_is_allowed_for_and_findings_keep_the_file_times()
    {
        // The file runs 20 s late; the correction waiting for review moves it back
        var lines = DiscrepancyTests.Script.Select(l => (l.Start + 20, l.Text)).ToArray();
        var job = File_("Early", lines, ResultStatus.Proposed, change: r => r with { Offset = -20, Scale = 1, Confidence = 0.2 });
        var heard = DiscrepancyTests.Said(Changed(6, "I have four tickets here."));
        var result = (await Check(job, new Hears(heard)))!;
        var f = Assert.Single(result.Findings);
        Assert.Equal(45, f.Time, 3);
    }

    [Fact]
    public async Task Findings_are_applied_or_declined_one_at_a_time_and_undone()
    {
        var file = DiscrepancyTests.Script.Append((50, "This line was never spoken aloud.")).ToArray();
        var job = File_("Film", file);
        var original = File.ReadAllBytes(job.SubtitlePath);
        var heard = DiscrepancyTests.Said([.. Changed(6, "I have four tickets here."), (41, "Wait, where did my other shoe go now?")]);
        var result = (await Check(job, new Hears(heard)))!;
        Assert.Equal(3, result.Findings.Count);

        // Decline the missing line: nothing changes
        var missing = result.Findings.ToList().FindIndex(f => f.Kind == DiscrepancyFinder.MissingLine);
        result = _processor.DeclineFinding(result.Id, missing, result.Findings[missing].Time);
        Assert.Equal(original, File.ReadAllBytes(job.SubtitlePath));
        Assert.Equal(2, result.Findings.Count);

        // Apply the number: the line takes the heard number
        var number = result.Findings.ToList().FindIndex(f => f.Kind == DiscrepancyFinder.Number);
        result = _processor.ApplyFinding(result.Id, number, result.Findings[number].Time);
        var text = SubtitleReader.Read(File.ReadAllBytes(job.SubtitlePath), job.SubtitlePath)!;
        Assert.Equal("I have four tickets here.", text.Cues[6].Text);
        Assert.Equal(1, result.Cleaned[DiscrepancyReview.FixedKind]);
        Assert.True(result.Changed);

        // Remove the line with nothing heard
        var extra = result.Findings.ToList().FindIndex(f => f.Kind == DiscrepancyFinder.Extra);
        result = _processor.ApplyFinding(result.Id, extra, result.Findings[extra].Time);
        Assert.Equal(10, SubtitleReader.Read(File.ReadAllBytes(job.SubtitlePath), job.SubtitlePath)!.Cues.Count);
        Assert.Empty(result.Findings);
        Assert.False(result.PendingReview);

        // A stale page can't apply a finding that has gone
        Assert.Throws<InvalidOperationException>(() => _processor.ApplyFinding(result.Id, 0, 25));

        // Undo brings the original back
        _processor.Undo(result.Id);
        Assert.Equal(original, File.ReadAllBytes(job.SubtitlePath));
    }

    [Fact]
    public async Task Apply_takes_every_suggestion_and_leaves_lines_with_nothing_heard_for_review()
    {
        var file = DiscrepancyTests.Script.Append((50, "This line was never spoken aloud.")).ToArray();
        var job = File_("Film", file);
        var heard = DiscrepancyTests.Said([.. Changed(6, "I have four tickets here."), (41, "Wait, where did my other shoe go now?")]);
        var result = (await Check(job, new Hears(heard)))!;

        result = _processor.Apply(result.Id, Policies);
        var cues = SubtitleReader.Read(File.ReadAllBytes(job.SubtitlePath), job.SubtitlePath)!.Cues;
        Assert.Equal(12, cues.Count);
        Assert.Contains(cues, c => c.Text == "Wait, where did my other shoe go now?" && Math.Abs(c.Start.TotalSeconds - 41.1) < 0.01);
        Assert.Equal("I have four tickets here.", cues[6].Text);
        Assert.Equal(2, result.Cleaned[DiscrepancyReview.FixedKind]);
        var extra = Assert.Single(result.Findings);
        Assert.Equal(DiscrepancyFinder.Extra, extra.Kind);
        Assert.True(result.PendingReview);

        // Only it is left: Apply refuses; Decline clears it
        Assert.Throws<InvalidOperationException>(() => _processor.Apply(result.Id, Policies));
        result = _processor.Decline(result.Id);
        Assert.Empty(result.Findings);
        Assert.False(result.PendingReview);
    }

    [Fact]
    public async Task An_edit_by_hand_closes_the_findings_it_dealt_with()
    {
        var job = File_("Film", DiscrepancyTests.Script);
        var result = (await Check(job, new Hears(DiscrepancyTests.Said(Changed(6, "I have four tickets here.")))))!;
        var view = _processor.LoadForEditing(result.Id)!;
        var cues = view.Cues.Select(c => c.Source == 6 ? c with { Text = "I have 4 tickets here." } : c).ToList();
        result = _processor.SaveEdited(result.Id, view.Fingerprint, cues);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task The_AI_plugin_confirms_lines_whose_wording_differs_when_it_answers()
    {
        var heard = DiscrepancyTests.Said([.. Changed(6, "I have four tickets here."), (41, "Wait, where did my other shoe go now?")]);
        var job = File_("Film", DiscrepancyTests.Script);
        var denied = (await Check(job, new Hears(heard), Settings(new Auditor(_ => new AuditAnswer([], string.Empty, "test-model")))))!;
        Assert.Equal(DiscrepancyFinder.MissingLine, Assert.Single(denied.Findings).Kind);
        Assert.Contains("confirmed 0 of 1", denied.Explanation, StringComparison.Ordinal);

        var job2 = File_("Film 2", DiscrepancyTests.Script);
        var confirmed = (await Check(job2, new Hears(heard), Settings(new Auditor(lines => new AuditAnswer([new AuditFinding(0, "number", null, "r")], string.Empty, "test-model")))))!;
        Assert.Equal(2, confirmed.Findings.Count);

        var job3 = File_("Film 3", DiscrepancyTests.Script);
        var unanswered = (await Check(job3, new Hears(heard), Settings(new Auditor(_ => new AuditAnswer([], "No AI checks left in this run.", null)))))!;
        Assert.Equal(2, unanswered.Findings.Count);
        Assert.Contains("Not confirmed by the AI plugin", unanswered.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_run_keeps_to_its_time_budget_and_stops_at_a_service_limit()
    {
        var jobs = Enumerable.Range(0, 3).Select(i => File_("Film " + i.ToString(System.Globalization.CultureInfo.InvariantCulture), DiscrepancyTests.Script)).ToList();
        var speech = new Hears(DiscrepancyTests.Said(DiscrepancyTests.Script), during: () => _clock.Now = _clock.Now.AddHours(3));
        var run = await _checker.RunAsync(jobs, j => new TranscriberTests.FakeAudio(j.Duration), speech, Setup, Settings(), TimeSpan.FromHours(4), _clock.Now.AddHours(-2), null, null, TestContext.Current.CancellationToken);

        // Two hours were used before (generating); the first file takes three more, so no second one starts
        Assert.Equal(1, run.Checked);
        Assert.Equal(2, run.Left);
        Assert.Equal(TimeSpan.FromHours(4), run.OutOfTime);
        Assert.Contains("stopped after 4 h; 2 left for tomorrow", run.Summary(), StringComparison.Ordinal);

        var stopped = await _checker.RunAsync(jobs.Skip(1).ToList(), j => new TranscriberTests.FakeAudio(j.Duration), new Refusing(), "deepgram/nova-3", Settings(), null, null, null, null, TestContext.Current.CancellationToken);
        Assert.Equal("limit reached", stopped.StoppedBy);
        Assert.Equal(2, stopped.Left);
        Assert.Null(Result(jobs[1]).WholeFile);
    }

    [Fact]
    public async Task A_transcript_that_fails_is_noted_and_tried_again_later()
    {
        var job = File_("Film", DiscrepancyTests.Script);
        var failing = new Failing();
        var result = (await Check(job, failing))!;
        Assert.True(result.WholeFile!.Failed);
        Assert.Contains("service down", result.Explanation, StringComparison.Ordinal);
        Assert.Empty(_checker.Choose([job], English, automatic: true, max: 5));
        _clock.Now = _clock.Now.AddDays(4);
        Assert.Single(_checker.Choose([job], English, automatic: true, max: 5));
    }

    private sealed class Failing : ISpeechToText
    {
        public string Id => "local";

        public Task<Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken cancellationToken)
            => throw new SpeechToTextException("service down");
    }

    [Fact]
    public void A_check_is_asked_for_only_for_files_the_run_can_check()
    {
        var job = File_("Film", DiscrepancyTests.Script, ResultStatus.InSync);
        var id = ResultStore.IdFor(job.SubtitlePath);
        var (r, refused) = _processor.RequestWholeFileCheck(id, _ => job, English);
        Assert.Null(refused);
        Assert.True(r!.WholeFileRequested);
        Assert.True(Result(job).WholeFileRequested);

        // Audio in another language, a language not wanted, not in the library: refused with the reason, nothing queued
        var french = File_("French audio", DiscrepancyTests.Script, ResultStatus.InSync, audio: "fre");
        var frenchId = ResultStore.IdFor(french.SubtitlePath);
        Assert.Equal((null, WholeFileChecker.NotInLanguage), _processor.RequestWholeFileCheck(frenchId, _ => french, English));
        var other = File_("Other", DiscrepancyTests.Script, ResultStatus.InSync);
        Assert.Equal(WholeFileChecker.NotInLanguage, _processor.RequestWholeFileCheck(ResultStore.IdFor(other.SubtitlePath), _ => other, ["fre"]).Refused);
        Assert.Contains("in the library", _processor.RequestWholeFileCheck(ResultStore.IdFor(other.SubtitlePath), _ => null, English).Refused, StringComparison.Ordinal);
        Assert.False(Result(french).WholeFileRequested);
        Assert.False(Result(other).WholeFileRequested);

        // Matched by meaning, generated, embedded
        var meaning = File_("Meaning", DiscrepancyTests.Script, ResultStatus.InSync, change: x => x with { Stage = SyncCheck.ByMeaningStage });
        Assert.Contains("matched by meaning", _processor.RequestWholeFileCheck(ResultStore.IdFor(meaning.SubtitlePath), _ => meaning, English).Refused, StringComparison.Ordinal);
        var generatedPath = Path.Combine(_dir, "Film.en.generated.srt");
        File.WriteAllText(generatedPath, "1\n00:00:01,000 --> 00:00:02,000\nHi\n");
        _store.Put(new SubtitleResult { Id = SubtitleGenerator.IdFor(job.VideoPath, "eng"), SubtitlePath = generatedPath, Status = ResultStatus.Generated });
        Assert.Contains("generated", _processor.RequestWholeFileCheck(SubtitleGenerator.IdFor(job.VideoPath, "eng"), _ => job, English).Refused, StringComparison.Ordinal);
        _store.Put(new SubtitleResult { Id = "emb-1", SubtitlePath = job.VideoPath, Status = ResultStatus.InSync });
        Assert.Contains("beside its video", _processor.RequestWholeFileCheck("emb-1", _ => job, English).Refused, StringComparison.Ordinal);

        // No such result
        Assert.Throws<InvalidOperationException>(() => _processor.RequestWholeFileCheck("nothing", _ => job, English));
    }

    [Fact]
    public void Queued_files_the_run_can_no_longer_check_leave_the_queue_with_the_reason()
    {
        var reachable = File_("Film", DiscrepancyTests.Script, change: r => r with { WholeFileRequested = true });
        var french = File_("French audio", DiscrepancyTests.Script, audio: "fre", change: r => r with { WholeFileRequested = true });
        var gone = File_("Not wanted any more", DiscrepancyTests.Script, change: r => r with { WholeFileRequested = true });

        // The library walk no longer lists the third (its language was taken off the wanted list)
        Assert.Equal(2, _checker.ClearUnreachable([reachable, french], English));
        Assert.True(Result(reachable).WholeFileRequested);
        Assert.False(Result(french).WholeFileRequested);
        Assert.Contains(WholeFileChecker.NotInLanguage, Result(french).Explanation, StringComparison.Ordinal);
        Assert.False(Result(gone).WholeFileRequested);
        Assert.Contains("in the library", Result(gone).Explanation, StringComparison.Ordinal);
        Assert.Equal([reachable], _checker.Choose([reachable, french, gone], English, automatic: false, max: 5));
    }
}
