using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Resilience;
using Jellyfin.Plugin.Subtitles.Candidates;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.Pipeline;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// Stage 4: last-resort subtitles generated from a full transcript. Invented titles and speech throughout.
public sealed class GeneratorTests : IDisposable
{
    private const string Setup = "builtin/base";
    private static readonly string[] English = ["eng"];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "subs-generator-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private readonly ResultStore _store;
    private readonly SubtitleGenerator _generator;

    public GeneratorTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new ResultStore(Path.Combine(_dir, "results.json"));
        _generator = new SubtitleGenerator(_store, new SubtitleFiles(Path.Combine(_dir, "originals")), _clock);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Silent : ISpeechToText
    {
        public string Id => "builtin";

        public Task<Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken cancellationToken)
            => Task.FromResult(new Transcript([new TranscribedWord("[Music]", 1, 30, null), new TranscribedWord("Hm.", 40, 40.2, null)], language, "builtin", "base", samples.Length / 16000.0));
    }

    private sealed class Refusing(FailureClass failure) : ISpeechToText
    {
        public string Id => "deepgram";

        public Task<Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken cancellationToken)
            => throw new SpeechToTextException("refused") { Failure = failure };
    }

    private FindJob Video(string name = "Invented Film (2020)", string language = "eng", string? audio = "eng", int minutes = 25)
    {
        var path = Path.Combine(_dir, name + ".mkv");
        File.WriteAllText(path, string.Empty);
        return new FindJob(Guid.NewGuid(), name, path, new VideoFacts { FileName = name + ".mkv", Duration = TimeSpan.FromMinutes(minutes) }, language, TimeSpan.FromMinutes(minutes), 0, audio);
    }

    // What the finder records when nothing fitted
    private void NothingFound(FindJob job, DateTimeOffset? at = null)
        => _store.Put(new SubtitleResult { Id = SubtitleFinder.IdFor(job.VideoPath, job.Language), SubtitlePath = SubtitleFinder.PathFor(job.VideoPath, job.Language, false, SubtitleFormat.Srt), Status = ResultStatus.NotFound, Time = at ?? _clock.Now });

    private Task<SubtitleResult?> Generate(FindJob job, ISpeechToText? speech = null)
        => _generator.GenerateAsync(job, new TranscriberTests.FakeAudio(job.Duration), speech ?? new TranscriberTests.FakeSpeech("builtin"), Setup, TestContext.Current.CancellationToken);

    [Fact]
    public void Generated_files_are_named_after_the_video_with_language_and_marker()
    {
        var path = SubtitleGenerator.PathFor(Path.Combine(_dir, "Invented Film (2020).mkv"), "eng");
        Assert.Equal(Path.Combine(_dir, "Invented Film (2020).en.generated.srt"), path);
        Assert.Equal("Show S01E02.fr.generated.srt", Path.GetFileName(SubtitleGenerator.PathFor(Path.Combine(_dir, "Show S01E02.mp4"), "fre")));

        Assert.True(SubtitleGenerator.IsGenerated(path));
        Assert.True(SubtitleGenerator.IsGenerated("/v/Film.EN.GENERATED.srt"));
        Assert.False(SubtitleGenerator.IsGenerated("/v/Film.en.srt"));
        Assert.False(SubtitleGenerator.IsGenerated("/v/Film.en.sdh.srt"));
        Assert.False(SubtitleGenerator.IsGenerated(null));
    }

    [Fact]
    public void A_generated_subtitle_does_not_count_as_having_one()
    {
        Assert.True(FindRules.Counts(isForced: false, isText: true, countImages: true, isGenerated: false));
        Assert.False(FindRules.Counts(isForced: false, isText: true, countImages: true, isGenerated: true));
        Assert.False(FindRules.Counts(isForced: true, isText: true, countImages: true, isGenerated: false));
    }

    [Fact]
    public void Only_the_language_spoken_is_generated()
    {
        Assert.True(SubtitleGenerator.AudioMatches(Video(audio: "eng"), English));
        Assert.True(SubtitleGenerator.AudioMatches(Video(audio: "en"), English));
        Assert.False(SubtitleGenerator.AudioMatches(Video(audio: "jpn"), English));

        // No tag: taken to be the first wanted language
        Assert.True(SubtitleGenerator.AudioMatches(Video(audio: null), ["eng", "fre"]));
        Assert.True(SubtitleGenerator.AudioMatches(Video(audio: "und"), ["eng", "fre"]));
        Assert.False(SubtitleGenerator.AudioMatches(Video(language: "fre", audio: null), ["eng", "fre"]));
        Assert.True(SubtitleGenerator.AudioMatches(Video(language: "fre", audio: "fra"), ["eng", "fre"]));
    }

    [Fact]
    public void Setup_names_the_service_and_its_effective_model()
    {
        Assert.Equal("builtin/base", SubtitleGenerator.SetupOf("builtin", string.Empty));
        Assert.Equal("builtin/small", SubtitleGenerator.SetupOf("builtin", "Small"));
        Assert.Equal("deepgram/nova-3", SubtitleGenerator.SetupOf("deepgram", null));
        Assert.Equal("local/default", SubtitleGenerator.SetupOf("local", " "));
        Assert.True(SubtitleGenerator.TimeLimit(TimeSpan.FromHours(2)) > TimeSpan.FromHours(10));
        Assert.Equal(TimeSpan.FromDays(1), SubtitleGenerator.TimeLimit(TimeSpan.FromDays(3)));
    }

    [Fact]
    public void Only_videos_the_finder_found_nothing_for_are_candidates()
    {
        var job = Video();
        Assert.False(_generator.NeedsGeneration(job, English, Setup));

        NothingFound(job);
        Assert.True(_generator.NeedsGeneration(job, English, Setup));

        // Found later: no longer a candidate
        _store.Put(new SubtitleResult { Id = SubtitleFinder.IdFor(job.VideoPath, job.Language), SubtitlePath = "x.srt", Status = ResultStatus.Added, Changed = true, Time = _clock.Now });
        Assert.False(_generator.NeedsGeneration(job, English, Setup));

        // Another language's audio: never
        var dubbed = Video("Dubbed Film", audio: "ger");
        NothingFound(dubbed);
        Assert.False(_generator.NeedsGeneration(dubbed, English, Setup));

        // A generated file already there (made by anyone): not again
        var other = Video("Other Film");
        NothingFound(other);
        File.WriteAllText(SubtitleGenerator.PathFor(other.VideoPath, other.Language), "1\r\n00:00:01,000 --> 00:00:02,000\r\nHi\r\n");
        Assert.False(_generator.NeedsGeneration(other, English, Setup));
    }

    [Fact]
    public async Task A_generated_subtitle_is_written_labelled_and_not_generated_twice()
    {
        var job = Video();
        NothingFound(job);

        var result = await Generate(job);

        Assert.NotNull(result);
        Assert.Equal(ResultStatus.Generated, result!.Status);
        Assert.True(result.Changed);
        Assert.Equal(Path.Combine(_dir, "Invented Film (2020).en.generated.srt"), result.SubtitlePath);
        Assert.Equal(job.VideoPath, result.VideoPath);
        Assert.Contains("by the built-in speech-to-text (m)", result.Explanation, StringComparison.Ordinal);
        Assert.Contains("lines", result.Explanation, StringComparison.Ordinal);
        Assert.NotEmpty(result.Examples);
        Assert.StartsWith("Shoal Subtitles generated a subtitle: ", SubtitleActivity.NoteFor(result)!.Name, StringComparison.Ordinal);

        var document = SubtitleReader.Read(File.ReadAllBytes(result.SubtitlePath), result.SubtitlePath)!;
        Assert.Equal(SubtitleFormat.Srt, document.Format);
        Assert.True(document.Cues.Count > 50);
        Assert.StartsWith("word", document.Cues[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(document.Cues, c => c.Text.Contains(SubtitleGenerator.Marker, StringComparison.OrdinalIgnoreCase));

        Assert.False(_generator.NeedsGeneration(job, English, Setup));
        Assert.False(_generator.NeedsGeneration(job, English, "deepgram/nova-3"));
    }

    [Fact]
    public async Task No_speech_writes_nothing_and_is_retried_only_with_another_service()
    {
        var job = Video();
        NothingFound(job);

        var result = await Generate(job, new Silent());

        Assert.Equal(ResultStatus.NoSpeech, result!.Status);
        Assert.Contains("No speech to transcribe", result.Explanation, StringComparison.Ordinal);
        Assert.False(File.Exists(result.SubtitlePath));
        Assert.True(ResultStore.MustKeep(result, _clock.Now.AddYears(1)));

        _clock.Now = _clock.Now.AddDays(60);
        Assert.False(_generator.NeedsGeneration(job, English, Setup));
        Assert.True(_generator.NeedsGeneration(job, English, "deepgram/nova-3"));
    }

    [Fact]
    public async Task A_service_failure_is_retried_after_a_while_and_a_limit_stops_the_run()
    {
        var job = Video();
        NothingFound(job);

        var failed = await Generate(job, new Refusing(FailureClass.Transient));
        Assert.Equal(ResultStatus.Failed, failed!.Status);
        Assert.False(_generator.NeedsGeneration(job, English, Setup));
        _clock.Now = _clock.Now.AddDays(3);
        Assert.True(_generator.NeedsGeneration(job, English, Setup));

        await Assert.ThrowsAsync<SpeechToTextException>(() => Generate(job, new Refusing(FailureClass.ProviderLimit)));
    }

    [Fact]
    public void The_nightly_pace_is_capped_and_the_longest_waiting_go_first()
    {
        var jobs = Enumerable.Range(0, 30).Select(i => Video("Film " + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture))).ToList();
        for (var i = 0; i < jobs.Count; i++)
        {
            // Film 29 was searched first, film 0 last
            NothingFound(jobs[i], _clock.Now.AddHours(-i));
        }

        var chosen = _generator.Choose(jobs, English, Setup, 20);
        Assert.Equal(20, chosen.Count);
        Assert.Equal("Film 29", chosen[0].Name);
        Assert.Equal("Film 10", chosen[^1].Name);

        Assert.Empty(_generator.Choose(jobs, English, Setup, 0));
        Assert.Equal(30, _generator.Choose(jobs, English, Setup, 1000).Count);
    }

    [Fact]
    public async Task A_real_subtitle_found_later_replaces_the_generated_one()
    {
        var job = Video();
        NothingFound(job);
        var generated = (await Generate(job))!;

        var real = SubtitleFinder.PathFor(job.VideoPath, job.Language, false, SubtitleFormat.Srt);
        File.WriteAllText(real, "1\r\n00:00:01,000 --> 00:00:02,000\r\nHello\r\n");
        var replaced = _generator.ReplaceGenerated(job.VideoPath, job.Language, real);

        Assert.NotNull(replaced);
        Assert.Equal(ResultStatus.Replaced, replaced!.Status);
        Assert.False(replaced.Changed);
        Assert.False(File.Exists(generated.SubtitlePath));
        Assert.True(File.Exists(real));
        Assert.True(File.Exists(Path.Combine(_dir, "originals", replaced.Backup!)));
        Assert.Contains(Path.GetFileName(real), replaced.Explanation, StringComparison.Ordinal);

        // Nothing generated for another video: nothing to replace
        Assert.Null(_generator.ReplaceGenerated(Video("Else").VideoPath, "eng", real));
    }

    [Fact]
    public async Task A_generated_subtitle_changed_by_someone_is_left_in_place()
    {
        var job = Video();
        NothingFound(job);
        var generated = (await Generate(job))!;
        File.AppendAllText(generated.SubtitlePath, "\r\n99\r\n01:00:00,000 --> 01:00:01,000\r\nAdded by hand\r\n");

        var replaced = _generator.ReplaceGenerated(job.VideoPath, job.Language, "/v/real.en.srt")!;

        Assert.True(File.Exists(generated.SubtitlePath));
        Assert.Contains("left in place", replaced.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Undo_removes_the_generated_file_and_it_is_not_generated_again()
    {
        var job = Video();
        NothingFound(job);
        var generated = (await Generate(job))!;
        var processor = new SubtitleProcessor(_store, new SubtitleFiles(Path.Combine(_dir, "originals")), _clock);

        Assert.Throws<InvalidOperationException>(() => processor.SaveEdited(generated.Id, generated.Fingerprint, []));
        var undone = processor.Undo(generated.Id);

        Assert.Equal(ResultStatus.Undone, undone.Status);
        Assert.False(File.Exists(generated.SubtitlePath));
        Assert.False(_generator.NeedsGeneration(job, English, Setup));
        Assert.Null(_generator.ReplaceGenerated(job.VideoPath, job.Language, "/v/real.en.srt"));
    }

    [Fact]
    public async Task Generated_results_go_only_with_their_video()
    {
        var job = Video();
        NothingFound(job);
        var generated = (await Generate(job))!;

        // Someone deleted the generated file: the result stays, so it isn't generated again
        File.Delete(generated.SubtitlePath);
        Assert.Equal(0, _store.Prune(File.Exists, Directory.Exists));
        Assert.NotNull(_store.Get(generated.Id));
        Assert.False(_generator.NeedsGeneration(job, English, Setup));

        File.Delete(job.VideoPath);
        Assert.Equal(1, _store.Prune(File.Exists, Directory.Exists));
        Assert.Null(_store.Get(generated.Id));
    }

    [Fact]
    public async Task Nothing_is_written_if_a_subtitle_was_found_meanwhile()
    {
        var job = Video();
        NothingFound(job);
        var generator = _generator;

        // The search finds one while the video is being transcribed
        var racing = new Racing(() => _store.Put(new SubtitleResult { Id = SubtitleFinder.IdFor(job.VideoPath, job.Language), SubtitlePath = "x.srt", Status = ResultStatus.Added, Changed = true, Time = _clock.Now }));
        var result = await generator.GenerateAsync(job, new TranscriberTests.FakeAudio(job.Duration), racing, Setup, TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.False(File.Exists(SubtitleGenerator.PathFor(job.VideoPath, job.Language)));
    }

    private sealed class Racing(Action during) : ISpeechToText
    {
        private readonly TranscriberTests.FakeSpeech _inner = new("builtin");

        public string Id => "builtin";

        public Task<Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken cancellationToken)
        {
            during();
            return _inner.TranscribeAsync(samples, language, cancellationToken);
        }
    }
}
