using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.Audio;
using Jellyfin.Plugin.Subtitles.Candidates;
using Jellyfin.Plugin.Subtitles.Configuration;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.Pipeline;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// Invented dialogue and release names throughout.
public sealed class FinderTests : IDisposable
{
    private static readonly Policies Auto = new(ChangePolicy.Automatic, ChangePolicy.Review, new CleanupSettings());

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "subs-finder-" + Guid.NewGuid().ToString("N"));

    public FinderTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    // "Real" is the film's dialogue; anything else is another film's (no words in common)
    private static SubtitleDocument Script(string tag)
        => new()
        {
            Format = SubtitleFormat.Srt,
            Cues = [.. Enumerable.Range(0, 300).Select(i => new SubtitleCue
            {
                Start = TimeSpan.FromSeconds(10 + (i * 5)),
                End = TimeSpan.FromSeconds(12 + (i * 5)),
                Text = tag == "Real" ? $"Line {i} says word{i}x and word{i}y" : $"Entirely unrelated {tag}{i} chatter about {tag}{i}q matters",
            })],
        };

    // The audio "speaks" the real script, lateBy seconds after its cues
    private sealed class Audio(SubtitleDocument said, double lateBy) : IAudioSource, ISpeechToText
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
            var words = new List<TranscribedWord>();
            foreach (var cue in said.Cues)
            {
                var at = cue.Start.TotalSeconds + lateBy - _start.TotalSeconds;
                if (at >= 0 && at <= 60)
                {
                    words.AddRange(cue.Text.Split(' ').Select((w, k) => new TranscribedWord(w, at + (k * 0.25), at + (k * 0.25) + 0.2, 0.9)));
                }
            }

            return Task.FromResult(new Transcript(words, "en", "fake", "fake", 60));
        }
    }

    private sealed class Source(params (SubtitleCandidate Candidate, SubtitleDocument Document)[] offers) : ICandidateSource
    {
        public List<string> Fetched { get; } = [];

        public string Name => "Fake";

        public Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(Guid itemId, string language, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SubtitleCandidate>>([.. offers.Select(o => o.Candidate)]);

        public Task<FetchedSubtitle?> FetchAsync(SubtitleCandidate candidate, CancellationToken cancellationToken)
        {
            Fetched.Add(candidate.Id);
            var doc = offers.Single(o => o.Candidate.Id == candidate.Id).Document;
            return Task.FromResult<FetchedSubtitle?>(new FetchedSubtitle(SubtitleWriter.ToBytes(doc), "srt", "eng"));
        }
    }

    private static SubtitleCandidate Offer(string id, string release, int downloads, bool? hi = null)
        => new() { Source = "Fake", Id = id, ReleaseName = release, Format = "srt", Language = "eng", DownloadCount = downloads, HearingImpaired = hi };

    private (SubtitleFinder Finder, ResultStore Store, FindJob Job, Clock Clock) Setup()
    {
        var clock = new Clock();
        var store = new ResultStore(Path.Combine(_dir, "results.json"));
        var finder = new SubtitleFinder(store, new DownloadLedger(Path.Combine(_dir, "downloads.json"), clock), clock);
        var video = Path.Combine(_dir, "Invented Film (2020).mkv");
        File.WriteAllText(video, string.Empty);
        var job = new FindJob(Guid.NewGuid(), "Invented Film", video, new VideoFacts { FileName = "Invented Film (2020).mkv", Duration = TimeSpan.FromMinutes(25) }, "eng", TimeSpan.FromMinutes(25), 0);
        return (finder, store, job, clock);
    }

    // FAM-06: the download count keeps its policy on the shared JSON file helper: a damaged file counts from zero again
    [Fact]
    public void A_damaged_download_count_starts_from_zero_and_is_replaced()
    {
        var path = Path.Combine(_dir, "downloads.json");
        var ledger = new DownloadLedger(path, new Clock());
        Assert.True(ledger.TryTake(2));
        Assert.Equal(1, new DownloadLedger(path, new Clock()).Today());

        File.WriteAllText(path, "{ damaged");
        var again = new DownloadLedger(path, new Clock());
        Assert.Equal(0, again.Today());
        Assert.True(again.TryTake(2));
        Assert.Equal(1, new DownloadLedger(path, new Clock()).Today());
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task A_fitting_subtitle_is_added_beside_the_video_with_its_timing_corrected()
    {
        var (finder, _, job, _) = Setup();
        var audio = new Audio(Script("Real"), 1.5);
        var source = new Source((Offer("a", "Invented.Film.2020.1080p.WEB", 900), Script("Real")), (Offer("b", "Invented.Film.2020.720p", 100), Script("Real")));

        var result = await finder.FindAsync(job, source, audio, audio, Auto, 100, CancellationToken.None);

        Assert.Equal(ResultStatus.Added, result.Status);
        Assert.Equal(["a"], source.Fetched);
        Assert.EndsWith("Invented Film (2020).en.srt", result.SubtitlePath, StringComparison.Ordinal);
        Assert.Contains("Invented.Film.2020.1080p.WEB", result.Origin, StringComparison.Ordinal);
        var added = SubtitleReader.Read(File.ReadAllBytes(result.SubtitlePath), result.SubtitlePath)!;
        Assert.Equal(11.5, added.Cues[0].Start.TotalSeconds, 0.05);
    }

    [Fact]
    public async Task A_candidate_for_something_else_is_skipped_for_the_next()
    {
        var (finder, _, job, _) = Setup();
        var audio = new Audio(Script("Real"), 0);
        var source = new Source((Offer("a", "Invented.Film.2020.1080p", 900), Script("Other")), (Offer("b", "Invented.Film.2020.720p", 100), Script("Real")));

        var result = await finder.FindAsync(job, source, audio, audio, Auto, 100, CancellationToken.None);

        Assert.Equal(ResultStatus.Added, result.Status);
        Assert.Equal(["a", "b"], source.Fetched);
        Assert.Contains("Invented.Film.2020.720p", result.Origin, StringComparison.Ordinal);
        Assert.Contains("Skipped first", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task When_nothing_fits_nothing_is_added_and_the_search_waits()
    {
        var (finder, _, job, clock) = Setup();
        var audio = new Audio(Script("Real"), 0);
        var source = new Source((Offer("a", "Some.Other.Film.2020", 10), Script("Other")));

        var result = await finder.FindAsync(job, source, audio, audio, Auto, 100, CancellationToken.None);

        Assert.Equal(ResultStatus.NotFound, result.Status);
        Assert.False(File.Exists(SubtitleFinder.PathFor(job.VideoPath, "eng", false, SubtitleFormat.Srt)));
        Assert.False(finder.NeedsSearch(job));
        clock.Now += SubtitleFinder.SearchAgainAfter;
        Assert.True(finder.NeedsSearch(job));
    }

    // SUB-19: a search no provider answered isn't "nothing fitting found"
    [Fact]
    public async Task When_no_provider_answers_nothing_is_recorded()
    {
        var (finder, store, job, _) = Setup();
        var audio = new Audio(Script("Real"), 0);

        await Assert.ThrowsAsync<NoSourceAnsweredException>(() => finder.FindAsync(job, new CombinedSource([new Down()]), audio, audio, Auto, 100, CancellationToken.None));

        Assert.True(finder.NeedsSearch(job));
        Assert.Null(store.Get(SubtitleFinder.IdFor(job.VideoPath, job.Language)));
    }

    private sealed class Down : ICandidateSource
    {
        public string Name => "Down";

        public Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(Guid itemId, string language, CancellationToken cancellationToken)
            => throw new System.Net.Http.HttpRequestException("unreachable");

        public Task<FetchedSubtitle?> FetchAsync(SubtitleCandidate candidate, CancellationToken cancellationToken) => Task.FromResult<FetchedSubtitle?>(null);
    }

    [Fact]
    public async Task No_downloads_left_means_nothing_is_recorded()
    {
        var (finder, _, job, _) = Setup();
        var audio = new Audio(Script("Real"), 0);
        var source = new Source((Offer("a", "Invented.Film.2020", 10), Script("Real")));

        await Assert.ThrowsAsync<DownloadLimitReachedException>(() => finder.FindAsync(job, source, audio, audio, Auto, 0, CancellationToken.None));

        Assert.Empty(source.Fetched);
        Assert.True(finder.NeedsSearch(job));
    }

    [Fact]
    public async Task Running_out_of_downloads_part_way_leaves_the_video_for_tomorrow()
    {
        var (finder, _, job, _) = Setup();
        var audio = new Audio(Script("Real"), 0);
        var source = new Source((Offer("a", "Invented.Film.2020", 10), Script("Other")), (Offer("b", "Invented.Film.2020.x", 5), Script("Real")));

        await Assert.ThrowsAsync<DownloadLimitReachedException>(() => finder.FindAsync(job, source, audio, audio, Auto, 1, CancellationToken.None));

        // One download used; not marked "nothing fits", because the better candidate was never tried
        Assert.Single(source.Fetched);
        Assert.True(finder.NeedsSearch(job));
    }

    [Fact]
    public async Task An_existing_file_is_never_replaced()
    {
        var (finder, _, job, _) = Setup();
        var existing = SubtitleFinder.PathFor(job.VideoPath, "eng", false, SubtitleFormat.Srt);
        File.WriteAllText(existing, "mine");
        var audio = new Audio(Script("Real"), 0);

        var result = await finder.FindAsync(job, new Source((Offer("a", "Invented.Film.2020", 10), Script("Real"))), audio, audio, Auto, 100, CancellationToken.None);

        Assert.Equal(ResultStatus.NotFound, result.Status);
        Assert.Equal("mine", File.ReadAllText(existing));
    }

    [Fact]
    public async Task Undo_removes_the_added_file_and_it_is_not_searched_for_again()
    {
        var (finder, store, job, _) = Setup();
        var audio = new Audio(Script("Real"), 0);
        var result = await finder.FindAsync(job, new Source((Offer("a", "Invented.Film.2020", 10, hi: true), Script("Real"))), audio, audio, Auto, 100, CancellationToken.None);
        Assert.EndsWith(".en.sdh.srt", result.SubtitlePath, StringComparison.Ordinal);
        var processor = new SubtitleProcessor(store, new SubtitleFiles(Path.Combine(_dir, "originals")));

        // The checker leaves the added file alone (it was checked as it was added)
        Assert.False(processor.NeedsCheck(result.SubtitlePath, SubtitleFiles.Fingerprint(File.ReadAllBytes(result.SubtitlePath))));

        var undone = processor.Undo(result.Id);

        Assert.Equal(ResultStatus.Undone, undone.Status);
        Assert.False(File.Exists(result.SubtitlePath));
        Assert.False(finder.NeedsSearch(job));
    }

    [Fact]
    public async Task Undo_refuses_to_remove_an_added_file_someone_edited()
    {
        var (finder, store, job, _) = Setup();
        var audio = new Audio(Script("Real"), 0);
        var result = await finder.FindAsync(job, new Source((Offer("a", "Invented.Film.2020", 10), Script("Real"))), audio, audio, Auto, 100, CancellationToken.None);
        File.AppendAllText(result.SubtitlePath, "\r\n");

        Assert.Throws<InvalidOperationException>(() => new SubtitleProcessor(store, new SubtitleFiles(Path.Combine(_dir, "originals"))).Undo(result.Id));
        Assert.True(File.Exists(result.SubtitlePath));
    }
}
