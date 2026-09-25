using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.Audio;
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
}
