using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.Api;
using Jellyfin.Plugin.Subtitles.Configuration;
using Jellyfin.Plugin.Subtitles.Discrepancy;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.Generation;
using Jellyfin.Plugin.Subtitles.Pipeline;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Jellyfin.Plugin.Subtitles.Sync;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// Subtitles made for a different cut of the video: fitted to a full transcript section by section. Invented words throughout.
public sealed class PiecewiseTests : IDisposable
{
    private const string Setup = "builtin/base";
    private static readonly string[] English = ["eng"];
    private static readonly double Pal = 25 / (24000 / 1001.0);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "subs-sections-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private readonly ResultStore _store;
    private readonly TranscriptCache _cache;
    private readonly SectionFixer _fixer;
    private readonly SubtitleProcessor _processor;

    public PiecewiseTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new ResultStore(Path.Combine(_dir, "results.json"));
        _cache = new TranscriptCache(Path.Combine(_dir, "transcripts"));
        _fixer = new SectionFixer(_store, _cache, _clock);
        _processor = new SubtitleProcessor(_store, new SubtitleFiles(Path.Combine(_dir, "originals")), _clock);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Hears(IReadOnlyList<TranscribedWord> words) : ISpeechToText
    {
        public int Calls { get; private set; }

        public string Id => "builtin";

        public Task<Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new Transcript(words, language, "builtin", "base", samples.Length / 16000.0));
        }
    }

    // A made-up episode: a line every 5 s from 10 s, each four words nobody else says ("ka12 lo12 mi12 nu12")
    private static List<(double Start, string Text)> Episode(int lines = 400, string stem = "")
        => [.. Enumerable.Range(0, lines).Select(i => (10.0 + (5 * i), $"Ka{stem}{i} lo{stem}{i}, mi{stem}{i} nu{stem}{i}."))];

    // What is heard: each line's words where the audio has them (null: the video doesn't have the line)
    private static List<TranscribedWord> Heard(IEnumerable<(double Start, string Text)> lines, Func<double, double?> audioOf, double jitter = 0, int seed = 1)
    {
        var rng = new Random(seed);
        var words = new List<TranscribedWord>();
        foreach (var (start, text) in lines)
        {
            if (audioOf(start) is not { } at)
            {
                continue;
            }

            var tokens = text.Split(' ');
            for (var i = 0; i < tokens.Length; i++)
            {
                var t = at + (0.4 * i) + (jitter * ((rng.NextDouble() * 2) - 1));
                words.Add(new TranscribedWord(tokens[i], t, t + 0.3, 0.95));
            }
        }

        return [.. words.OrderBy(w => w.Start)];
    }

    private static SubtitleDocument Doc(IEnumerable<(double Start, string Text)> lines) => DiscrepancyTests.Subtitle(lines);

    [Fact]
    public void One_offset_is_one_section()
    {
        var lines = Episode();
        var fit = PiecewiseAligner.Fit(Doc(lines), Heard(lines, t => t + 2.4));
        Assert.Equal(PiecewiseStatus.OneTiming, fit.Status);
        Assert.Equal(1, fit.Scale);
        Assert.Equal(2.4, Assert.Single(fit.Sections).Offset, 1);
        Assert.Empty(fit.NotInVideo);
    }

    [Fact]
    public void A_scene_the_subtitle_lacks_is_one_jump_placed_between_lines()
    {
        // The video has 60 s more after 1000 s: every later line is 60 s late in the video
        var lines = Episode();
        var fit = PiecewiseAligner.Fit(Doc(lines), Heard(lines, t => t < 1000 ? t : t + 60));
        Assert.Equal(PiecewiseStatus.Sections, fit.Status);
        Assert.Equal(2, fit.Sections.Count);
        Assert.Equal(0, fit.Sections[0].Offset, 1);
        Assert.Equal(60, fit.Sections[1].Offset, 1);

        // Between the line at 995 s (ending 997.5 s) and the one at 1000 s; shows in the video at 1060 s
        Assert.InRange(fit.Sections[1].From, 997.5, 1000);
        Assert.Equal(1060, Assert.Single(fit.JumpsAt), 1);
        Assert.Empty(fit.NotInVideo);
        Assert.StartsWith("Timing jumps at 17:40 (+60.0 s): subtitle made for a different cut.", fit.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_scene_the_video_lacks_is_flagged_not_moved()
    {
        // The subtitle has 80 s the video doesn't (lines from 1200 s to 1275 s): later lines are 80 s early in the video
        var lines = Episode();
        var fit = PiecewiseAligner.Fit(Doc(lines), Heard(lines, t => t < 1200 ? t : t < 1280 ? null : t - 80));
        Assert.Equal(PiecewiseStatus.Sections, fit.Status);
        Assert.Equal(-80, fit.Sections[1].Offset, 1);
        var flagged = fit.NotInVideo.Select(i => lines[i].Start).ToList();
        Assert.Equal(16, flagged.Count);
        Assert.All(flagged, t => Assert.InRange(t, 1200, 1275));
        Assert.Contains("16 lines cover a part the video doesn't have", fit.Explanation, StringComparison.Ordinal);
        Assert.Contains("(−80.0 s)", fit.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_cuts_with_noise_are_found_and_a_good_subtitle_with_noise_is_left_as_one()
    {
        // A recap the video lacks at the start, and ad-break-like jumps: timing jitters and a tenth of the words are misheard
        var lines = Episode(500);
        double? Audio(double t) => t < 40 ? null : t < 700 ? t - 30 : t < 1500 ? t - 30 + 12.5 : t - 30 + 12.5 + 45;
        var heard = Heard(lines, Audio, jitter: 0.2);
        var rng = new Random(7);
        heard = [.. heard.Select(w => rng.NextDouble() < 0.1 ? w with { Text = "zz" + rng.Next(1000) } : w)];
        var fit = PiecewiseAligner.Fit(Doc(lines), heard, duration: 2600);
        Assert.Equal(PiecewiseStatus.Sections, fit.Status);
        Assert.Equal(3, fit.Sections.Count);
        Assert.Equal(-30, fit.Sections[0].Offset, 0.3);
        Assert.Equal(-17.5, fit.Sections[1].Offset, 0.3);
        Assert.Equal(27.5, fit.Sections[2].Offset, 0.3);
        // The recap's lines that would land before the video starts are flagged
        Assert.Equal([0, 1, 2, 3], fit.NotInVideo.ToArray());

        var good = PiecewiseAligner.Fit(Doc(lines), [.. Heard(lines, t => t + 0.3, jitter: 0.2).Select(w => rng.NextDouble() < 0.1 ? w with { Text = "zz" + rng.Next(1000) } : w)]);
        Assert.Equal(PiecewiseStatus.OneTiming, good.Status);
    }

    [Fact]
    public void Another_episode_is_rejected()
    {
        // Another episode's words, with a few phrases in common heard at unrelated times
        var lines = Episode();
        var other = Episode(stem: "x");
        var heard = Heard(other, t => t).Concat(Heard(lines.Where((_, i) => i % 37 == 0), t => (t * 7) % 1900)).OrderBy(w => w.Start).ToList();
        var fit = PiecewiseAligner.Fit(Doc(lines), heard);
        Assert.Equal(PiecewiseStatus.Rejected, fit.Status);
        Assert.Empty(fit.Sections);

        // And nothing in common at all
        Assert.Equal(PiecewiseStatus.Rejected, PiecewiseAligner.Fit(Doc(lines), Heard(other, t => t)).Status);
    }

    [Fact]
    public void A_frame_rate_change_and_a_cut_are_found_together()
    {
        // Timed for 25 fps on a 23.976 fps video, with 45 s more in the video after the line at 1100 s
        var lines = Episode();
        var fit = PiecewiseAligner.Fit(Doc(lines), Heard(lines, t => (Pal * t) + (t < 1100 ? 0.5 : 45.5)));
        Assert.Equal(PiecewiseStatus.Sections, fit.Status);
        Assert.Equal(Pal, fit.Scale, 6);
        Assert.Equal(0.5, fit.Sections[0].Offset, 1);
        Assert.Equal(45.5, fit.Sections[1].Offset, 1);
        Assert.Contains("frame-rate change", fit.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Too_many_jumps_are_rejected()
    {
        // A jump every 90 s: no stretch long enough to trust, or too many sections
        var lines = Episode();
        var fit = PiecewiseAligner.Fit(Doc(lines), Heard(lines, t => t + (7 * Math.Floor(t / 90))));
        Assert.Equal(PiecewiseStatus.Rejected, fit.Status);
    }

    [Fact]
    public void A_jump_never_splits_a_line_even_when_lines_overlap()
    {
        // Long overlapping lines around the jump
        var lines = Episode();
        var doc = Doc(lines);
        doc = doc with { Cues = [.. doc.Cues.Select(c => c.Start.TotalSeconds is > 980 and < 1020 ? c with { End = c.Start + TimeSpan.FromSeconds(6.5) } : c)] };
        var fit = PiecewiseAligner.Fit(doc, Heard(lines, t => t < 1000 ? t : t + 30));
        Assert.Equal(PiecewiseStatus.Sections, fit.Status);

        // Each line keeps its length, moved whole by one section
        var moved = PiecewiseFit.Retime(doc, fit.Scale, fit.Sections);
        foreach (var (before, after) in doc.Cues.OrderBy(c => c.Start).Zip(moved.Cues))
        {
            Assert.Equal((before.End - before.Start).TotalSeconds, (after.End - after.Start).TotalSeconds, 2);
            var shift = (after.Start - before.Start).TotalSeconds;
            Assert.True(Math.Abs(shift) < 0.5 || Math.Abs(shift - 30) < 0.5, "moved by " + shift);
        }
    }

    [Fact]
    public void Clocks_map_each_section_and_back()
    {
        TimingSection[] sections = [new(0, 1, 100), new(500, 61, 100), new(900, 20, 100)];
        var (toAudio, toFile) = PiecewiseFit.Clocks(1, sections);
        Assert.Equal(101, toAudio(100));
        Assert.Equal(661, toAudio(600));
        Assert.Equal(1020, toAudio(1000));
        Assert.Equal(600, toFile(661), 6);
        Assert.Equal(1000, toFile(1020), 6);

        var r = new SubtitleResult { Id = "a", SubtitlePath = "a", Status = ResultStatus.Proposed, Scale = 1, Offset = 1, Sections = sections };
        Assert.Equal(661, WholeFileChecker.Clocks(r).ToAudio(600));
    }

    // ---- The fix in the pipeline ----

    // A subtitle file beside a video, and its result
    private SubtitleJob File_(string name, IEnumerable<(double Start, string Text)> lines, ResultStatus status = ResultStatus.Unreliable, string? audio = "eng", Func<SubtitleResult, SubtitleResult>? change = null)
    {
        var video = Path.Combine(_dir, name + ".mkv");
        File.WriteAllText(video, name);
        var path = Path.Combine(_dir, name + ".en.srt");
        var bytes = SubtitleWriter.ToBytes(Doc(lines));
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
        return new SubtitleJob(Guid.NewGuid(), name, video, path, "eng", TimeSpan.FromMinutes(40), 0, audio);
    }

    private SubtitleResult Result(SubtitleJob job) => _store.Get(ResultStore.IdFor(job.SubtitlePath))!;

    private Task<SubtitleResult?> Fix(SubtitleJob job, ISpeechToText? speech)
        => _fixer.FixAsync(job, speech is null ? null : new TranscriberTests.FakeAudio(TimeSpan.FromSeconds(30)), speech, Setup, English, TestContext.Current.CancellationToken);

    [Fact]
    public void Candidates_are_unclear_or_partly_agreeing_timings()
    {
        Assert.True(SectionFixer.IsCandidate(new SubtitleResult { Id = "a", SubtitlePath = "a", Status = ResultStatus.Unreliable }));
        Assert.True(SectionFixer.IsCandidate(new SubtitleResult { Id = "a", SubtitlePath = "a", Status = ResultStatus.InSync, Stage = WholeFileChecker.SpeechStage, Confidence = 0.3 }));
        Assert.True(SectionFixer.IsCandidate(new SubtitleResult { Id = "a", SubtitlePath = "a", Status = ResultStatus.Corrected, Stage = WholeFileChecker.SpeechStage, Confidence = 0.6 }));
        Assert.False(SectionFixer.IsCandidate(new SubtitleResult { Id = "a", SubtitlePath = "a", Status = ResultStatus.Corrected, Stage = WholeFileChecker.SpeechStage, Confidence = SectionFixer.PartialAgreement }));
        Assert.False(SectionFixer.IsCandidate(new SubtitleResult { Id = "a", SubtitlePath = "a", Status = ResultStatus.InSync, Stage = WholeFileChecker.SpeechStage, Confidence = 0.9 }));
        Assert.False(SectionFixer.IsCandidate(new SubtitleResult { Id = "a", SubtitlePath = "a", Status = ResultStatus.WrongLanguage }));
        Assert.False(SectionFixer.IsCandidate(new SubtitleResult { Id = "a", SubtitlePath = "a", Status = ResultStatus.Unreliable, Stage = SyncCheck.ByMeaningStage }));
        Assert.False(SectionFixer.IsCandidate(new SubtitleResult { Id = "a", SubtitlePath = "a", Status = ResultStatus.Declined }));
        Assert.False(SectionFixer.IsCandidate(new SubtitleResult { Id = "find-a", SubtitlePath = "a", Status = ResultStatus.Added, Stage = WholeFileChecker.SpeechStage, Confidence = 0.3 }));
        Assert.False(SectionFixer.IsCandidate(new SubtitleResult { Id = "a", SubtitlePath = "a", Status = ResultStatus.Proposed, Stage = SectionFixer.Stage, Confidence = 0.2 }));
        Assert.False(SectionFixer.IsCandidate(new SubtitleResult { Id = SubtitleGenerator.IdPrefix + "a", SubtitlePath = "a", Status = ResultStatus.Unreliable }));
        Assert.False(SectionFixer.IsCandidate(new SubtitleResult { Id = "a", SubtitlePath = "a", Status = ResultStatus.Unreliable, Explanation = "The text didn't decode cleanly as windows-1252." }));
    }

    [Fact]
    public void The_night_takes_those_asked_for_then_candidates_not_tried_within_the_cap()
    {
        var lines = Episode(20);
        var unclear = File_("Unclear", lines);
        _clock.Now = _clock.Now.AddMinutes(1);
        var weak = File_("Weak", lines, ResultStatus.InSync, change: r => r with { Confidence = 0.2 });
        var good = File_("Good", lines, ResultStatus.InSync);
        var asked = File_("Asked", lines, ResultStatus.InSync, change: r => r with { SectionFixRequested = true });
        var french = File_("French audio", lines, audio: "fre");
        var tried = File_("Tried", lines, change: r => r with { SectionFix = new SectionFixCheck { Time = _clock.Now } });
        var failed = File_("Failed", lines, change: r => r with { SectionFix = new SectionFixCheck { Time = _clock.Now.AddDays(-4), Failed = true } });
        var jobs = new[] { unclear, weak, good, asked, french, tried, failed };

        Assert.Equal([asked, unclear, failed, weak], _fixer.Choose(jobs, _ => English, automatic: true, max: 10));
        Assert.Equal([asked, unclear], _fixer.Choose(jobs, _ => English, automatic: true, max: 2));
        Assert.Equal([asked], _fixer.Choose(jobs, _ => English, automatic: false, max: 10));
        Assert.Empty(_fixer.Choose(jobs, _ => English, automatic: true, max: 0));
        Assert.True(_fixer.HasRequests);
    }

    [Fact]
    public void A_fix_is_asked_for_only_where_the_run_could_make_one()
    {
        var lines = Episode(20);
        var unclear = File_("Unclear", lines);
        var good = File_("Good", lines, ResultStatus.InSync);
        var french = File_("French audio", lines, audio: "fre");
        SubtitleJob? JobOf(SubtitleResult r) => new[] { unclear, good, french }.FirstOrDefault(j => j.SubtitlePath == r.SubtitlePath);

        var (queued, _, refused) = _processor.RequestSectionFix(Result(unclear).Id, JobOf, _ => English);
        Assert.Null(refused);
        Assert.True(queued!.SectionFixRequested);
        Assert.True(Result(unclear).SectionFixRequested);
        Assert.Contains("queued", string.Join(' ', ResultPresenter.Chips(Result(unclear)).Select(c => c.Kind)), StringComparison.Ordinal);

        Assert.StartsWith("Only a subtitle whose timing the check left unclear", _processor.RequestSectionFix(Result(good).Id, JobOf, _ => English).Refused, StringComparison.Ordinal);
        Assert.Equal("Only subtitles in a wanted language that matches the audio can be fixed by section.", _processor.RequestSectionFix(Result(french).Id, JobOf, _ => English).Refused);

        // Queued ones the run can no longer fix leave the queue with the reason
        _store.Put(Result(good) with { SectionFixRequested = true });
        Assert.Equal(1, _fixer.ClearUnreachable([unclear, good, french], _ => English));
        Assert.False(Result(good).SectionFixRequested);
        Assert.Contains("Timing by section: not tried", Result(good).Explanation, StringComparison.Ordinal);
        Assert.True(Result(unclear).SectionFixRequested);
    }

    [Fact]
    public async Task A_different_cut_waits_for_review_and_Apply_moves_each_section_and_Undo_restores()
    {
        // The subtitle has 80 s the video doesn't (1200–1275 s), and 30 s less after 1600 s (in its own time)
        var lines = Episode();
        double? Audio(double t) => t < 1200 ? t : t < 1280 ? null : t < 1600 ? t - 80 : t - 50;
        var job = File_("Cut", lines);
        var original = File.ReadAllBytes(job.SubtitlePath);
        var speech = new Hears(Heard(lines, Audio));

        var result = (await Fix(job, speech))!;
        Assert.Equal(ResultStatus.Proposed, result.Status);
        Assert.Equal(SectionFixer.Stage, result.Stage);
        Assert.Equal(3, result.Sections!.Count);
        Assert.True(result.PendingReview);
        Assert.False(result.SectionFixRequested);
        Assert.Equal(original, File.ReadAllBytes(job.SubtitlePath));
        var flagged = result.Findings.Where(DiscrepancyReview.IsSection).ToList();
        Assert.Equal(16, flagged.Count);
        Assert.All(flagged, f => Assert.Equal(DiscrepancyReview.NotInVideo, f.Kind));
        Assert.Contains("Timing by section: Timing jumps at 20:00 (−80.0 s) and 25:50 (+30.0 s): subtitle made for a different cut.", result.Explanation, StringComparison.Ordinal);
        Assert.StartsWith("Made for a different cut: timing jumps at 20:00 (−80.0 s) and 25:50 (+30.0 s)", ResultPresenter.Summary(result), StringComparison.Ordinal);
        Assert.Contains(ResultPresenter.NerdStats(result, TimeZoneInfo.Utc), s => s.Name == "Sections" && s.Value.Contains("-80.00 s", StringComparison.Ordinal));

        // One flagged line kept (declined): it stays, moved with its section
        var kept = flagged[0];
        _processor.DeclineFinding(result.Id, result.Findings.ToList().IndexOf(kept), kept.Time);

        var applied = _processor.Apply(result.Id, new Policies(ChangePolicy.Automatic, ChangePolicy.Review, new CleanupSettings()));
        Assert.Equal(ResultStatus.Corrected, applied.Status);
        Assert.Empty(applied.Findings);
        Assert.Equal(15, applied.Cleaned[DiscrepancyReview.RemovedNotInVideoKind]);
        var now = SubtitleReader.Read(File.ReadAllBytes(job.SubtitlePath), job.SubtitlePath)!;
        Assert.Equal(lines.Count - 15, now.Cues.Count);
        foreach (var (start, text) in lines)
        {
            var cue = now.Cues.FirstOrDefault(c => c.Text == text);
            if (Audio(start) is { } at)
            {
                Assert.Equal(at, cue!.Start.TotalSeconds, 1);
            }
            else if (text == kept.Current)
            {
                Assert.NotNull(cue);
            }
            else
            {
                Assert.Null(cue);
            }
        }

        var undone = _processor.Undo(result.Id);
        Assert.Equal(ResultStatus.Undone, undone.Status);
        Assert.Null(undone.Sections);
        Assert.Equal(original, File.ReadAllBytes(job.SubtitlePath));
    }

    [Fact]
    public async Task A_file_corrected_before_keeps_its_first_original_for_Undo()
    {
        // The regular check shifted the whole file (few words agreeing); the file is really cut
        var lines = Episode();
        var job = File_("Shifted", lines, ResultStatus.Corrected, change: r => r with { Confidence = 0.6 });
        var original = File.ReadAllBytes(job.SubtitlePath);
        var shifted = Episode().Select(l => (l.Start + 1, l.Text)).ToList();
        var (backup, written) = new SubtitleFiles(Path.Combine(_dir, "originals")).Replace(job.SubtitlePath, Result(job).Fingerprint, SubtitleWriter.ToBytes(Doc(shifted)));
        _store.Put(Result(job) with { Changed = true, Backup = backup, Fingerprint = written, Offset = 1 });

        var proposed = (await Fix(job, new Hears(Heard(shifted, t => t < 1001 ? t : t + 45))))!;
        Assert.Equal(ResultStatus.Proposed, proposed.Status);
        Assert.True(proposed.Changed);
        Assert.Equal(2, proposed.Sections!.Count);

        _processor.Apply(proposed.Id, new Policies(ChangePolicy.Automatic, ChangePolicy.Review, new CleanupSettings()));
        Assert.Equal(ResultStatus.Undone, _processor.Undo(proposed.Id).Status);
        Assert.Equal(original, File.ReadAllBytes(job.SubtitlePath));
    }

    [Fact]
    public async Task One_line_not_in_the_video_can_be_removed_on_its_own_and_Decline_drops_the_proposal()
    {
        var lines = Episode();
        var job = File_("Cut", lines);
        var result = (await Fix(job, new Hears(Heard(lines, t => t < 1200 ? t : t < 1280 ? null : t - 80))))!;
        var first = result.Findings.First(DiscrepancyReview.IsSection);
        var after = _processor.ApplyFinding(result.Id, result.Findings.ToList().IndexOf(first), first.Time);
        Assert.Equal(ResultStatus.Proposed, after.Status);
        Assert.True(after.Changed);
        Assert.Equal(1, after.Cleaned[DiscrepancyReview.RemovedNotInVideoKind]);
        Assert.DoesNotContain(SubtitleReader.Read(File.ReadAllBytes(job.SubtitlePath), job.SubtitlePath)!.Cues, c => c.Text == first.Current);

        var declined = _processor.Decline(result.Id);
        Assert.Equal(ResultStatus.Declined, declined.Status);
        Assert.Null(declined.Sections);
        Assert.Empty(declined.Findings);
        Assert.False(SectionFixer.IsCandidate(declined));
    }

    [Fact]
    public async Task Without_a_kept_transcript_nothing_happens_until_the_run_and_the_transcript_is_then_reused()
    {
        var lines = Episode();
        var job = File_("Cut", lines, change: r => r with { SectionFixRequested = true });
        Assert.Null(await Fix(job, null));
        Assert.True(Result(job).SectionFixRequested);

        var speech = new Hears(Heard(lines, t => t < 1000 ? t : t + 60));
        var run = await _fixer.RunAsync([job], _ => new TranscriberTests.FakeAudio(TimeSpan.FromSeconds(30)), speech, Setup, _ => English, null, null, null, TestContext.Current.CancellationToken);
        Assert.Equal(1, run.Proposed);
        Assert.Equal(1, speech.Calls);
        Assert.Contains("1 correction waits for review", run.Summary(), StringComparison.Ordinal);

        // Asked again (from the results): the kept transcript answers at once, without the service
        _store.Put(Result(job) with { Status = ResultStatus.Unreliable, Stage = "line starts", SectionFixRequested = true });
        var again = (await Fix(job, null))!;
        Assert.Equal(ResultStatus.Proposed, again.Status);
        Assert.Contains("transcript reused", again.Explanation, StringComparison.Ordinal);
        Assert.Equal(1, speech.Calls);
    }

    [Fact]
    public async Task Another_episode_or_a_file_in_sync_is_noted_and_left_alone()
    {
        var lines = Episode();
        var wrong = File_("Wrong", lines);
        var noted = (await Fix(wrong, new Hears(Heard(Episode(stem: "x"), t => t))))!;
        Assert.Equal(ResultStatus.Unreliable, noted.Status);
        Assert.Null(noted.Sections);
        Assert.Contains("Timing by section: no fix proposed", noted.Explanation, StringComparison.Ordinal);
        Assert.False(noted.PendingReview);
        Assert.NotNull(noted.SectionFix);

        var fine = File_("Fine", lines, ResultStatus.InSync, change: r => r with { Confidence = 0.3 });
        var same = (await Fix(fine, new Hears(Heard(lines, t => t + 0.05))))!;
        Assert.Equal(ResultStatus.InSync, same.Status);
        Assert.Contains("no fix needed", same.Explanation, StringComparison.Ordinal);

        // Tried once: not taken again on its own
        Assert.Empty(_fixer.Choose([wrong, fine], _ => English, automatic: true, max: 10));
    }

    // PluginConfiguration can't be made in unit tests (its Jellyfin base type): its default is the fixer's constant, and
    // the page leaves the box unticked unless the saved setting says true
    [Fact]
    public void The_setting_is_off_by_default_and_on_the_page_with_its_action()
    {
        Assert.False(SectionFixer.OnByDefault);
        using var stream = typeof(SectionFixer).Assembly.GetManifestResourceStream("Jellyfin.Plugin.Subtitles.Configuration.configPage.html")!;
        var page = new StreamReader(stream).ReadToEnd();
        Assert.Contains("Fix subtitles made for a different cut", page, StringComparison.Ordinal);
        Assert.Contains("page.querySelector('#FixDifferentCuts').checked = config.FixDifferentCuts === true;", page, StringComparison.Ordinal);
        Assert.Contains("config.FixDifferentCuts = page.querySelector('#FixDifferentCuts').checked;", page, StringComparison.Ordinal);
        Assert.Contains("['#GenerateMissing', '#CheckWholeFile', '#FixDifferentCuts']", page, StringComparison.Ordinal);
        Assert.Contains("'Try fixing timing by section'", page, StringComparison.Ordinal);
        Assert.Contains("'/FixBySection'", page, StringComparison.Ordinal);
    }
}
