using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.Candidates;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// Invented release names throughout.
public class SubDlTests
{
    private const string Key = "subdl_test_key_0123456789";
    private static readonly Guid Item = Guid.NewGuid();
    private const string Srt = "1\n00:00:01,000 --> 00:00:02,000\nHello.\n\n";

    private static byte[] Zip(params string[] names)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var n in names)
            {
                using var w = new StreamWriter(zip.CreateEntry(n).Open());
                w.Write(Srt.Replace("Hello", n, StringComparison.Ordinal));
            }
        }

        return ms.ToArray();
    }

    [Theory]
    [InlineData("Lantern.S01E02.720p.srt", 1, 2, true)]
    [InlineData("Lantern.S01E12.720p.srt", 1, 2, false)]
    [InlineData("Lantern 1x02 - Title.srt", 1, 2, true)]
    [InlineData("Lantern.S02E02.srt", 1, 2, false)]
    [InlineData("02 - Lantern.E02.srt", 1, 2, true)]
    [InlineData("Lantern.2002.srt", 1, 2, false)]
    public void Episode_file_names_are_recognised(string name, int season, int episode, bool match)
        => Assert.Equal(match, SubDlSource.IsEpisode(name, season, episode));

    [Fact]
    public void The_right_episode_is_taken_from_a_season_pack()
    {
        var pack = Zip("Lantern.S01E01.srt", "Lantern.S01E02.srt", "Lantern.S01E03.srt", "readme.txt");

        var picked = SubDlSource.Pick(pack, new VideoIds("tt0000001", null, 1, 2), "eng");

        Assert.Contains("Lantern.S01E02.srt", Encoding.UTF8.GetString(picked!.Content.Span), StringComparison.Ordinal);
        Assert.Null(SubDlSource.Pick(pack, new VideoIds("tt0000001", null, 1, 9), "eng"));
        Assert.Null(SubDlSource.Pick(Zip("a.S01E02.srt", "b.S01E02.srt"), new VideoIds(null, "1", 1, 2), "eng"));
        Assert.NotNull(SubDlSource.Pick(Zip("Harbour.2024.srt"), new VideoIds("tt0000002", null, null, null), "eng"));
        Assert.Null(SubDlSource.Pick(Encoding.UTF8.GetBytes("not a zip"), null, "eng"));

        // A pack that only numbers its files
        var numbered = Zip("00 Lantern, SEASON 1.srt", "01 Pilot.en.srt", "02 Second.en.srt", "12 Twelfth.en.srt");
        Assert.Contains("02 Second.en.srt", Encoding.UTF8.GetString(SubDlSource.Pick(numbered, new VideoIds(null, "1", 1, 2), "eng")!.Content.Span), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_uses_ids_keeps_packs_drops_other_episodes_and_never_keeps_the_key()
    {
        var api = new FakeSubDl();
        using var http = new HttpClient(api);
        var source = new SubDlSource(http, Key, _ => new VideoIds("tt0000001", "42", 1, 2));

        var found = await source.SearchAsync(Item, "eng", TestContext.Current.CancellationToken);

        Assert.Equal(["Lantern S01 pack", "Lantern.S01E02.WEB"], found.Select(c => c.ReleaseName));
        Assert.All(found, c => Assert.DoesNotContain(Key, c.Id, StringComparison.Ordinal));
        var search = api.Requests[0];
        Assert.Equal("api.subdl.com", search.Host);
        Assert.Contains("imdb_id=tt0000001", search.Query, StringComparison.Ordinal);
        Assert.Contains("type=tv", search.Query, StringComparison.Ordinal);
        Assert.Contains("season_number=1&episode_number=2", search.Query, StringComparison.Ordinal);

        var fetched = await source.FetchAsync(found[0], TestContext.Current.CancellationToken);
        Assert.Contains("Lantern.S01E02.srt", Encoding.UTF8.GetString(fetched!.Content.Span), StringComparison.Ordinal);
        Assert.Equal("dl.subdl.com", api.Requests[1].Host);
    }

    [Fact]
    public async Task Errors_never_contain_the_key()
    {
        using var http = new HttpClient(new FakeSubDl { Fail = true });
        var source = new SubDlSource(http, Key, _ => new VideoIds("tt0000001", null, null, null));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => source.SearchAsync(Item, "eng", TestContext.Current.CancellationToken));

        Assert.DoesNotContain(Key, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_source_is_skipped_and_one_out_of_allowance_is_dropped_for_the_run()
    {
        var good = new Listed("Good");
        var broken = new Listed("Broken") { Throw = new HttpRequestException("down") };
        var limited = new Listed("Limited") { Throw = new RateLimitExceededException("download limit reached") };
        var combined = new CombinedSource([broken, limited, good]);

        var found = await combined.SearchAsync(Item, "eng", TestContext.Current.CancellationToken);
        await combined.SearchAsync(Item, "eng", TestContext.Current.CancellationToken);

        Assert.Equal("Good", Assert.Single(found).Source);
        Assert.Equal(1, limited.Searches);
        Assert.Equal(2, broken.Searches);

        var onlyLimited = new CombinedSource([new Listed("Limited") { Throw = new RateLimitExceededException("limit") }]);
        await Assert.ThrowsAsync<RateLimitExceededException>(() => onlyLimited.SearchAsync(Item, "eng", TestContext.Current.CancellationToken));
    }

    // SUB-19: when no source answered, that isn't "nothing offered"
    [Fact]
    public async Task When_no_source_answers_the_search_says_so_instead_of_returning_nothing()
    {
        var outage = new CombinedSource([new Listed("A") { Throw = new HttpRequestException("down") }, new Listed("B") { Throw = new HttpRequestException("down") }]);
        var ex = await Assert.ThrowsAsync<NoSourceAnsweredException>(() => outage.SearchAsync(Item, "eng", TestContext.Current.CancellationToken));
        Assert.False(ex.NoneAvailable);

        var none = new CombinedSource([new Listed("A") { Throw = new NoSourceAnsweredException("none installed") { NoneAvailable = true } }]);
        Assert.True((await Assert.ThrowsAsync<NoSourceAnsweredException>(() => none.SearchAsync(Item, "eng", TestContext.Current.CancellationToken))).NoneAvailable);

        // One answering is enough, even with nothing to offer
        var partly = new CombinedSource([new Listed("A") { Throw = new HttpRequestException("down") }, new Listed("B")]);
        Assert.Single(await partly.SearchAsync(Item, "eng", TestContext.Current.CancellationToken));
    }

    // Named like the OpenSubtitles plugin's exception, which is matched by name
    private sealed class RateLimitExceededException(string message) : Exception(message);

    private sealed class Listed(string name) : ICandidateSource
    {
        public string Name => name;

        public Exception? Throw { get; init; }

        public int Searches { get; private set; }

        public Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(Guid itemId, string language, CancellationToken cancellationToken)
        {
            Searches++;
            return Throw is not null ? Task.FromException<IReadOnlyList<SubtitleCandidate>>(Throw)
                : Task.FromResult<IReadOnlyList<SubtitleCandidate>>([new SubtitleCandidate { Source = "x", Id = "1", ReleaseName = "r" }]);
        }

        public Task<FetchedSubtitle?> FetchAsync(SubtitleCandidate candidate, CancellationToken cancellationToken) => Task.FromResult<FetchedSubtitle?>(null);
    }

    private sealed class FakeSubDl : HttpMessageHandler
    {
        public bool Fail { get; init; }

        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            if (Fail)
            {
                throw new HttpRequestException("Connection refused (" + request.RequestUri + ")");
            }

            if (request.RequestUri!.Host == "api.subdl.com")
            {
                var body = "{\"status\":true,\"subtitles\":["
                    + "{\"release_name\":\"Lantern S01 pack\",\"url\":\"/subtitle/100-200.zip?api_key=" + Key + "\",\"season\":1,\"episode\":null,\"hi\":false},"
                    + "{\"release_name\":\"Lantern.S01E05.WEB\",\"url\":\"/subtitle/101-201.zip?api_key=" + Key + "\",\"season\":1,\"episode\":5},"
                    + "{\"release_name\":\"Lantern.S01E02.WEB\",\"url\":\"/subtitle/102-202.zip?api_key=" + Key + "\",\"season\":1,\"episode\":2},"
                    + "{\"release_name\":\"Odd\",\"url\":\"https://elsewhere.example/x.zip\"}]}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip("Lantern.S01E01.srt", "Lantern.S01E02.srt")) });
        }
    }
}
