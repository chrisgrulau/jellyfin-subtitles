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
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Jellyfin.Plugin.Subtitles.Sync;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// Invented dialogue throughout.
public sealed class PipelineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "subs-pipeline-" + Guid.NewGuid().ToString("N"));

    public PipelineTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string Srt = "1\r\n00:00:01,000 --> 00:00:02,000\r\nHello there.\r\n\r\n";

    [Fact]
    public void A_file_is_replaced_with_its_original_kept_and_can_be_undone()
    {
        var path = Path.Combine(_dir, "Film.en.srt");
        File.WriteAllText(path, Srt);
        var original = SubtitleFiles.Fingerprint(File.ReadAllBytes(path));
        var files = new SubtitleFiles(Path.Combine(_dir, "backups"));

        var (backup, written) = files.Replace(path, original, Encoding.UTF8.GetBytes("changed"));

        Assert.Equal("changed", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_dir, ".shoal-*"));
        Assert.Equal(original, files.Restore(path, backup, written));
        Assert.Equal(Srt, File.ReadAllText(path));
    }

    // SUB-24: a rewritten subtitle (and an undone one) keeps the original's permissions
    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void A_replaced_or_restored_file_keeps_its_permissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(_dir, "Film.fr.srt");
        File.WriteAllText(path, Srt);
        const UnixFileMode Shared = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite;
        File.SetUnixFileMode(path, Shared);
        var files = new SubtitleFiles(Path.Combine(_dir, "backups"));

        var (backup, written) = files.Replace(path, SubtitleFiles.Fingerprint(File.ReadAllBytes(path)), Encoding.UTF8.GetBytes("changed"));
        Assert.Equal(Shared, File.GetUnixFileMode(path));

        File.SetUnixFileMode(path, Shared | UnixFileMode.OtherRead);
        files.Restore(path, backup, written);
        Assert.Equal(Shared | UnixFileMode.OtherRead, File.GetUnixFileMode(path));
        Assert.Equal(Srt, File.ReadAllText(path));
    }

    [Fact]
    public void A_file_changed_since_it_was_checked_is_not_replaced()
    {
        var path = Path.Combine(_dir, "Film.en.srt");
        File.WriteAllText(path, Srt);
        var files = new SubtitleFiles(Path.Combine(_dir, "backups"));

        Assert.Throws<IOException>(() => files.Replace(path, "not-the-fingerprint", [1, 2, 3]));
        Assert.Equal(Srt, File.ReadAllText(path));
    }

    [Fact]
    public void Undo_never_overwrites_someone_elses_later_edit()
    {
        var path = Path.Combine(_dir, "Film.en.srt");
        File.WriteAllText(path, Srt);
        var files = new SubtitleFiles(Path.Combine(_dir, "backups"));
        var (backup, written) = files.Replace(path, SubtitleFiles.Fingerprint(File.ReadAllBytes(path)), Encoding.UTF8.GetBytes("ours"));
        File.WriteAllText(path, "edited by hand");

        Assert.Throws<IOException>(() => files.Restore(path, backup, written));
        Assert.Equal("edited by hand", File.ReadAllText(path));
    }

    [Fact]
    public void The_first_original_is_the_one_kept()
    {
        var path = Path.Combine(_dir, "Film.en.srt");
        File.WriteAllText(path, Srt);
        var files = new SubtitleFiles(Path.Combine(_dir, "backups"));
        var (backup, first) = files.Replace(path, SubtitleFiles.Fingerprint(File.ReadAllBytes(path)), Encoding.UTF8.GetBytes("one"));
        var (_, second) = files.Replace(path, first, Encoding.UTF8.GetBytes("two"));

        files.Restore(path, backup, second);

        Assert.Equal(Srt, File.ReadAllText(path));
    }

    [Fact]
    public void Results_survive_a_restart_and_a_damaged_file_starts_empty()
    {
        var file = Path.Combine(_dir, "results.json");
        var store = new ResultStore(file);
        store.Put(new SubtitleResult { Id = "a", SubtitlePath = "/x/a.srt", Status = ResultStatus.Corrected, Time = DateTimeOffset.UnixEpoch.AddDays(1) });
        store.Put(new SubtitleResult { Id = "b", SubtitlePath = "/x/b.srt", Status = ResultStatus.InSync, Time = DateTimeOffset.UnixEpoch.AddDays(2) });
        store.Put(new SubtitleResult { Id = "a", SubtitlePath = "/x/a.srt", Status = ResultStatus.Undone, Time = DateTimeOffset.UnixEpoch.AddDays(3) });

        var again = new ResultStore(file).All();
        Assert.Equal(["a", "b"], again.Select(r => r.Id));
        Assert.Equal(ResultStatus.Undone, again[0].Status);

        File.WriteAllText(file, "{ nope");
        Assert.Empty(new ResultStore(file).All());
    }

    // SUB-30: plain results are written in batches; undo records at once
    [Fact]
    public void Plain_results_are_written_in_batches_and_undo_records_at_once()
    {
        var file = Path.Combine(_dir, "batched.json");
        var store = new ResultStore(file);
        var t = DateTimeOffset.UnixEpoch;
        store.Put(new SubtitleResult { Id = "first", SubtitlePath = "/x/first.srt", Status = ResultStatus.InSync, Time = t });
        for (var i = 0; i < 5; i++)
        {
            store.Put(new SubtitleResult { Id = "p" + i, SubtitlePath = "/x/p" + i + ".srt", Status = ResultStatus.InSync, Time = t.AddMinutes(i) });
        }

        // The first result is written straight away; the next few wait for the batch
        Assert.Single(new ResultStore(file).All());

        store.Put(new SubtitleResult { Id = "changed", SubtitlePath = "/x/c.srt", Status = ResultStatus.Corrected, Changed = true, Backup = "b", Time = t.AddHours(1) });
        Assert.Equal(7, new ResultStore(file).All().Count);

        for (var i = 0; i < ResultStore.SaveEvery; i++)
        {
            store.Put(new SubtitleResult { Id = "q" + i, SubtitlePath = "/x/q" + i + ".srt", Status = ResultStatus.InSync, Time = t.AddHours(2) });
        }

        Assert.Equal(7 + ResultStore.SaveEvery, new ResultStore(file).All().Count);

        store.Put(new SubtitleResult { Id = "last", SubtitlePath = "/x/last.srt", Status = ResultStatus.InSync, Time = t.AddHours(3) });
        store.Flush();
        Assert.Equal(8 + ResultStore.SaveEvery, new ResultStore(file).All().Count);
    }

    [Fact]
    public void Results_are_found_by_id_and_by_path()
    {
        var store = new ResultStore(Path.Combine(_dir, "indexed.json"));
        var t = DateTimeOffset.UnixEpoch;
        store.Put(new SubtitleResult { Id = "find-1", SubtitlePath = "/x/a.en.srt", Status = ResultStatus.Added, Time = t });
        store.Put(new SubtitleResult { Id = "sync-1", SubtitlePath = "/x/a.en.srt", Status = ResultStatus.InSync, Time = t.AddDays(1) });
        store.Put(new SubtitleResult { Id = "sync-1", SubtitlePath = "/x/moved.en.srt", Status = ResultStatus.InSync, Time = t.AddDays(2) });

        Assert.Equal("find-1", store.ForPath("/x/a.en.srt")!.Id);
        Assert.Equal("sync-1", store.ForPath("/x/moved.en.srt")!.Id);
        Assert.Equal("/x/moved.en.srt", store.Get("sync-1")!.SubtitlePath);
        Assert.Same(store.Get("find-1"), store.FindForRequest("find-1"));

        Assert.True(store.Remove("find-1"));
        Assert.Null(store.ForPath("/x/a.en.srt"));
        Assert.Null(store.Get("find-1"));
        Assert.Equal(1, store.Prune(_ => false, _ => true));
        Assert.Empty(store.All());
    }

    private sealed class NoAudio : IAudioSource
    {
        public Task<float[]> ReadAsync(TimeSpan start, TimeSpan length, CancellationToken cancellationToken) => Task.FromResult(new float[(int)(length.TotalSeconds * AudioFormat.SampleRate)]);
    }

    private sealed class Hears(IReadOnlyList<string> words) : ISpeechToText
    {
        public string Id => "fake";

        public int Calls { get; private set; }

        public Task<Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new Transcript([.. words.Select((w, i) => new TranscribedWord(w, i * 0.4, (i * 0.4) + 0.3, 0.9))], "en", "fake", "fake", 60));
        }
    }

    private static SubtitleDocument Dialogue()
        => new() { Format = SubtitleFormat.Srt, Cues = [.. Enumerable.Range(0, 300).Select(i => new SubtitleCue { Start = TimeSpan.FromSeconds(10 + (i * 5)), End = TimeSpan.FromSeconds(12 + (i * 5)), Text = $"Line {i} of the invented story" })] };

    [Fact]
    public async Task Without_speech_to_text_an_unclear_answer_says_so()
    {
        var outcome = await new SyncCheck(new NoAudio(), null, refine: false).RunAsync(Dialogue(), TimeSpan.FromMinutes(25), "en", CancellationToken.None);

        Assert.Equal(SyncStatus.Unreliable, outcome.Model.Status);
        Assert.Contains("No speech-to-text", outcome.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Speech_in_another_language_is_flagged()
    {
        var speech = new Hears([.. Enumerable.Range(0, 150).Select(i => "palabra" + i)]);

        var outcome = await new SyncCheck(new NoAudio(), speech, refine: false).RunAsync(Dialogue(), TimeSpan.FromMinutes(25), "en", CancellationToken.None);

        Assert.True(speech.Calls > 0);
        Assert.True(outcome.WrongLanguageSuspected);
        Assert.Equal(SyncStatus.Unreliable, outcome.Model.Status);
    }

    [Fact]
    public async Task Silence_is_not_mistaken_for_another_language()
    {
        var outcome = await new SyncCheck(new NoAudio(), new Hears([]), refine: false).RunAsync(Dialogue(), TimeSpan.FromMinutes(25), "en", CancellationToken.None);

        Assert.False(outcome.WrongLanguageSuspected);
    }

    // Audio whose "speech" is the subtitle text, spoken 2.5 s later than the subtitles say: the fake speech-to-text knows
    // which stretch was read last and returns those lines' words
    internal sealed class Shifted(SubtitleDocument said, double lateBy) : IAudioSource, ISpeechToText
    {
        private TimeSpan _start;

        public string Id => "fake";

        public Task<float[]> ReadAsync(TimeSpan start, TimeSpan length, CancellationToken cancellationToken)
        {
            _start = start;
            return Task.FromResult(new float[(int)(length.TotalSeconds * AudioFormat.SampleRate)]);
        }

        public Task<Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken cancellationToken)
        {
            var from = _start.TotalSeconds;
            var words = new List<TranscribedWord>();
            foreach (var cue in said.Cues)
            {
                var at = cue.Start.TotalSeconds + lateBy - from;
                if (at < 0 || at > 60)
                {
                    continue;
                }

                var parts = cue.Text.Split(' ');
                words.AddRange(parts.Select((w, k) => new TranscribedWord(w, at + (k * 0.25), at + (k * 0.25) + 0.2, 0.9)));
            }

            return Task.FromResult(new Transcript(words, "en", "fake", "fake", samples.Length / (double)AudioFormat.SampleRate));
        }
    }

    private static readonly Policies Auto = new(ChangePolicy.Automatic, ChangePolicy.Review, new CleanupSettings());

    private static readonly Policies TimingReview = new(ChangePolicy.Review, ChangePolicy.Review, new CleanupSettings());

    internal static SubtitleDocument Story()
        => new() { Format = SubtitleFormat.Srt, Cues = [.. Enumerable.Range(0, 300).Select(i => new SubtitleCue { Start = TimeSpan.FromSeconds(10 + (i * 5)), End = TimeSpan.FromSeconds(12 + (i * 5)), Text = $"Line {i} says word{i}x and word{i}y" })] };

    private (SubtitleProcessor Processor, SubtitleJob Job, string Path) Setup()
    {
        var path = Path.Combine(_dir, "Film.en.srt");
        File.WriteAllBytes(path, SubtitleWriter.ToBytes(Story()));
        var processor = new SubtitleProcessor(new ResultStore(Path.Combine(_dir, "results.json")), new SubtitleFiles(Path.Combine(_dir, "originals")));
        return (processor, new SubtitleJob(Guid.NewGuid(), "Invented Film", Path.Combine(_dir, "Film.mkv"), path, "eng", TimeSpan.FromMinutes(25), 0), path);
    }

    [Fact]
    public async Task A_late_subtitle_is_corrected_and_can_be_undone()
    {
        var (processor, job, path) = Setup();
        var original = File.ReadAllBytes(path);
        var fake = new Shifted(Story(), 2.5);

        var result = await processor.ProcessAsync(job, fake, fake, Auto, CancellationToken.None);

        Assert.Equal(ResultStatus.Corrected, result.Status);
        Assert.Equal(2.5, result.Offset, 0.05);
        var corrected = SubtitleReader.Read(File.ReadAllBytes(path), path)!;
        Assert.Equal(12.5, corrected.Cues[0].Start.TotalSeconds, 0.05);
        Assert.False(processor.NeedsCheck(path, SubtitleFiles.Fingerprint(File.ReadAllBytes(path))));

        var undone = processor.Undo(result.Id);

        Assert.Equal(ResultStatus.Undone, undone.Status);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(processor.NeedsCheck(path, SubtitleFiles.Fingerprint(original)));
    }

    [Fact]
    public async Task With_review_the_correction_waits_until_applied()
    {
        var (processor, job, path) = Setup();
        var before = File.ReadAllBytes(path);
        var fake = new Shifted(Story(), 2.5);

        var result = await processor.ProcessAsync(job, fake, fake, TimingReview, CancellationToken.None);

        Assert.Equal(ResultStatus.Proposed, result.Status);
        Assert.Equal(before, File.ReadAllBytes(path));

        var applied = processor.Apply(result.Id, Auto);
        Assert.Equal(ResultStatus.Corrected, applied.Status);
        Assert.Equal(12.5, SubtitleReader.Read(File.ReadAllBytes(path), path)!.Cues[0].Start.TotalSeconds, 0.05);
        Assert.Throws<InvalidOperationException>(() => processor.Apply(result.Id, Auto));
    }

    [Fact]
    public async Task A_subtitle_in_sync_is_left_alone()
    {
        var (processor, job, path) = Setup();
        var before = File.ReadAllBytes(path);
        var fake = new Shifted(Story(), 0);

        var result = await processor.ProcessAsync(job, fake, fake, Auto, CancellationToken.None);

        Assert.Equal(ResultStatus.InSync, result.Status);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Null(result.Backup);
    }

    [Fact]
    public void A_changed_or_failed_subtitle_is_checked_again()
    {
        var (processor, job, _) = Setup();
        processor.RecordFailure(job, "ffmpeg failed");

        Assert.True(processor.NeedsCheck(job.SubtitlePath, "anything"));
        Assert.StartsWith("ffmpeg failed", processor.Recent(10)[0].Explanation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("eng", 1)]
    [InlineData("fre", 2)]
    [InlineData("ger", 0)]
    [InlineData(null, 0)]
    public void The_audio_in_the_subtitles_language_is_used(string? language, int expected)
    {
        (string?, bool)[] audio = [("jpn", true), ("eng", false), ("fra", false)];

        Assert.Equal(expected, AudioChoice.For(audio, language));
    }

    private (SubtitleProcessor Processor, SubtitleJob Job, string Path) SetupWith(SubtitleDocument document)
    {
        var (processor, job, path) = Setup();
        File.WriteAllBytes(path, SubtitleWriter.ToBytes(document));
        return (processor, job, path);
    }

    private static SubtitleDocument WithAdvertAndRepeat()
    {
        var story = Story();
        var cues = story.Cues.ToList();
        cues.Insert(0, new SubtitleCue { Start = TimeSpan.FromSeconds(1), End = TimeSpan.FromSeconds(3), Text = "Downloaded from YTS.MX" });
        cues.Insert(5, new SubtitleCue { Start = cues[4].End - TimeSpan.FromSeconds(0.5), End = cues[4].End + TimeSpan.FromSeconds(1), Text = cues[4].Text });
        return story with { Cues = cues };
    }

    [Fact]
    public async Task Timing_and_automatic_clean_up_go_into_one_write_and_one_undo()
    {
        var (processor, job, path) = SetupWith(WithAdvertAndRepeat());
        var original = File.ReadAllBytes(path);
        var fake = new Shifted(Story(), 2.5);

        var result = await processor.ProcessAsync(job, fake, fake, Auto, CancellationToken.None);

        Assert.Equal(ResultStatus.Corrected, result.Status);
        Assert.True(result.Changed);
        Assert.Equal(1, result.Cleaned["RemovedAdvert"]);
        Assert.Equal(1, result.CleanupPending["MergedDuplicate"]);
        Assert.Contains(result.Examples, e => e.StartsWith("Removed advert", StringComparison.Ordinal) && e.Contains("YTS.MX", StringComparison.Ordinal));
        Assert.Contains(result.Examples, e => e.StartsWith("Waiting for review: Merged repeated line", StringComparison.Ordinal));
        var now = File.ReadAllText(path);
        Assert.DoesNotContain("YTS.MX", now, StringComparison.Ordinal);
        Assert.True(result.PendingReview);

        var undone = processor.Undo(result.Id);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(undone.Examples);
        Assert.False(undone.Changed);
    }

    [Fact]
    public async Task Held_back_clean_up_is_applied_on_request_and_stays_undoable_to_the_first_original()
    {
        var (processor, job, path) = SetupWith(WithAdvertAndRepeat());
        var original = File.ReadAllBytes(path);
        var fake = new Shifted(Story(), 0);

        var result = await processor.ProcessAsync(job, fake, fake, Auto, CancellationToken.None);
        Assert.Equal(ResultStatus.InSync, result.Status);
        Assert.True(result.Changed);

        var applied = processor.Apply(result.Id, Auto);

        Assert.Empty(applied.CleanupPending);
        Assert.Equal(1, applied.Cleaned["MergedDuplicate"]);
        Assert.False(applied.PendingReview);
        Assert.Equal(Story().Cues.Count, SubtitleReader.Read(File.ReadAllBytes(path), path)!.Cues.Count);

        processor.Undo(result.Id);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Nothing_to_change_means_the_file_is_not_touched()
    {
        var (processor, job, path) = Setup();
        var before = File.GetLastWriteTimeUtc(path);
        var fake = new Shifted(Story(), 0);

        var result = await processor.ProcessAsync(job, fake, fake, Auto, CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Empty(result.Cleaned);
        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void Older_results_are_checked_again_once_but_an_undo_is_respected()
    {
        var file = Path.Combine(_dir, "old-results.json");
        var store = new ResultStore(file);
        store.Put(new SubtitleResult { Id = ResultStore.IdFor("/x/a.srt"), SubtitlePath = "/x/a.srt", Status = ResultStatus.InSync, Fingerprint = "f", Version = 1 });
        store.Put(new SubtitleResult { Id = ResultStore.IdFor("/x/b.srt"), SubtitlePath = "/x/b.srt", Status = ResultStatus.Undone, Fingerprint = "g", Version = 1 });
        store.Put(new SubtitleResult { Id = ResultStore.IdFor("/x/c.srt"), SubtitlePath = "/x/c.srt", Status = ResultStatus.InSync, Fingerprint = "h", Version = SubtitleProcessor.CurrentVersion });
        var processor = new SubtitleProcessor(store, new SubtitleFiles(Path.Combine(_dir, "originals")));

        Assert.True(processor.NeedsCheck("/x/a.srt", "f"));
        Assert.False(processor.NeedsCheck("/x/b.srt", "g"));
        Assert.False(processor.NeedsCheck("/x/c.srt", "h"));
        Assert.True(processor.NeedsCheck("/x/c.srt", "changed"));
    }

    // SUB-09: a file replaced from outside after this plugin changed it is a new original

    private static SubtitleDocument OtherRelease()
        => new() { Format = SubtitleFormat.Srt, Cues = [.. Enumerable.Range(0, 300).Select(i => new SubtitleCue { Start = TimeSpan.FromSeconds(10 + (i * 5)), End = TimeSpan.FromSeconds(12 + (i * 5)), Text = $"Scene {i} has term{i}p then term{i}q" })] };

    [Fact]
    public async Task A_replacement_in_sync_after_a_change_offers_no_undo()
    {
        var (processor, job, path) = Setup();
        var fake = new Shifted(Story(), 2.5);
        await processor.ProcessAsync(job, fake, fake, Auto, CancellationToken.None);

        var replacement = SubtitleWriter.ToBytes(OtherRelease());
        File.WriteAllBytes(path, replacement);
        Assert.True(processor.NeedsCheck(path, SubtitleFiles.Fingerprint(replacement)));
        var other = new Shifted(OtherRelease(), 0);
        var result = await processor.ProcessAsync(job, other, other, Auto, CancellationToken.None);

        Assert.Equal(ResultStatus.InSync, result.Status);
        Assert.False(result.Changed);
        Assert.Null(result.Backup);
        Assert.Throws<InvalidOperationException>(() => processor.Undo(result.Id));
        Assert.Equal(replacement, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Undo_after_correcting_a_replacement_restores_the_replacement_not_the_older_file()
    {
        var (processor, job, path) = Setup();
        var fake = new Shifted(Story(), 2.5);
        await processor.ProcessAsync(job, fake, fake, Auto, CancellationToken.None);

        var replacement = SubtitleWriter.ToBytes(OtherRelease());
        File.WriteAllBytes(path, replacement);
        var other = new Shifted(OtherRelease(), 1.5);
        var result = await processor.ProcessAsync(job, other, other, Auto, CancellationToken.None);
        Assert.Equal(ResultStatus.Corrected, result.Status);

        processor.Undo(result.Id);

        Assert.Equal(replacement, File.ReadAllBytes(path));
    }

    [Fact]
    public void Each_original_gets_its_own_backup()
    {
        var path = Path.Combine(_dir, "Film.en.srt");
        File.WriteAllText(path, Srt);
        var files = new SubtitleFiles(Path.Combine(_dir, "backups"));
        var (first, written) = files.Replace(path, SubtitleFiles.Fingerprint(File.ReadAllBytes(path)), Encoding.UTF8.GetBytes("one"));
        File.WriteAllText(path, "outside");
        var (second, _) = files.Replace(path, SubtitleFiles.Fingerprint(File.ReadAllBytes(path)), Encoding.UTF8.GetBytes("two"));

        Assert.NotEqual(first, second);
        Assert.Equal(Srt, File.ReadAllText(Path.Combine(_dir, "backups", first)));
        Assert.Equal("outside", File.ReadAllText(Path.Combine(_dir, "backups", second)));
        Assert.NotEqual(written, SubtitleFiles.Fingerprint(Encoding.UTF8.GetBytes("two")));
    }

    // SUB-10: results are the record of what was checked, so none are evicted

    [Fact]
    public void A_large_library_is_checked_once_each_across_runs()
    {
        var store = new ResultStore(Path.Combine(_dir, "results.json"));
        var processor = new SubtitleProcessor(store, new SubtitleFiles(Path.Combine(_dir, "originals")));
        var paths = Enumerable.Range(0, 2500).Select(i => Path.Combine(_dir, $"Show S01E{i:0000}.en.srt")).ToList();
        var checks = new int[paths.Count];
        for (var run = 0; run < 60; run++)
        {
            var todo = paths.Select((p, i) => (p, i)).Where(x => processor.NeedsCheck(x.p, "f" + x.i)).Take(50).ToList();
            foreach (var (p, i) in todo)
            {
                checks[i]++;
                store.Put(new SubtitleResult
                {
                    Id = ResultStore.IdFor(p),
                    SubtitlePath = p,
                    Status = i % 100 == 0 ? ResultStatus.Corrected : ResultStatus.InSync,
                    Changed = i % 100 == 0,
                    Backup = i % 100 == 0 ? "b" + i : null,
                    Fingerprint = "f" + i,
                    Version = SubtitleProcessor.CurrentVersion,
                    Time = DateTimeOffset.UnixEpoch.AddMinutes((run * 100) + i),
                });
            }
        }

        Assert.All(checks, c => Assert.Equal(1, c));
        Assert.Equal(2500, store.All().Count);
        Assert.Equal(25, store.All().Count(r => r.Changed && r.Backup is not null));
    }

    [Fact]
    public void Past_the_ceiling_only_results_nothing_depends_on_are_dropped()
    {
        var store = new ResultStore(Path.Combine(_dir, "results.json"), maxResults: 10);
        var t = DateTimeOffset.UnixEpoch;
        for (var i = 0; i < 5; i++)
        {
            store.Put(new SubtitleResult { Id = "c" + i, SubtitlePath = "/x/c" + i, Status = ResultStatus.Corrected, Changed = true, Backup = "b", Time = t.AddMinutes(i) });
        }

        store.Put(new SubtitleResult { Id = "find-n", SubtitlePath = "/x/n", Status = ResultStatus.NotFound, Time = t.AddMinutes(6) });
        for (var i = 0; i < 20; i++)
        {
            store.Put(new SubtitleResult { Id = "s" + i, SubtitlePath = "/x/s" + i, Status = ResultStatus.InSync, Time = t.AddMinutes(10 + i) });
        }

        var kept = store.All();
        Assert.Equal(10, kept.Count);
        Assert.All(Enumerable.Range(0, 5), i => Assert.Contains(kept, r => r.Id == "c" + i));
        Assert.Contains(kept, r => r.Id == "find-n");
        Assert.Contains(kept, r => r.Id == "s19");
    }

    [Fact]
    public void Results_for_deleted_files_are_pruned_but_not_when_the_folder_is_offline()
    {
        var store = new ResultStore(Path.Combine(_dir, "results.json"));
        var here = Path.Combine(_dir, "here.en.srt");
        File.WriteAllText(here, Srt);
        store.Put(new SubtitleResult { Id = "here", SubtitlePath = here, Status = ResultStatus.InSync });
        store.Put(new SubtitleResult { Id = "gone", SubtitlePath = Path.Combine(_dir, "gone.en.srt"), Status = ResultStatus.Corrected, Changed = true });
        store.Put(new SubtitleResult { Id = "offline", SubtitlePath = Path.Combine(_dir, "unmounted", "a.en.srt"), Status = ResultStatus.Corrected, Changed = true });
        store.Put(new SubtitleResult { Id = "find-x", SubtitlePath = Path.Combine(_dir, "missing.en.srt"), Status = ResultStatus.NotFound });

        Assert.Equal(1, store.Prune(File.Exists, Directory.Exists));

        Assert.Equal(["find-x", "here", "offline"], store.All().Select(r => r.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Everything_waiting_for_review_is_listed_however_old()
    {
        var store = new ResultStore(Path.Combine(_dir, "results.json"));
        var processor = new SubtitleProcessor(store, new SubtitleFiles(Path.Combine(_dir, "originals")));
        store.Put(new SubtitleResult { Id = "old", SubtitlePath = "/x/old", Status = ResultStatus.Proposed, Time = DateTimeOffset.UnixEpoch });
        for (var i = 0; i < 20; i++)
        {
            store.Put(new SubtitleResult { Id = "n" + i, SubtitlePath = "/x/n" + i, Status = ResultStatus.InSync, Time = DateTimeOffset.UnixEpoch.AddDays(1 + i) });
        }

        var recent = processor.Recent(5);

        Assert.Equal(6, recent.Count);
        Assert.Contains(recent, r => r.Id == "old");
    }
}
