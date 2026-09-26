using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Costs;
using Jellyfin.Plugin.Subtitles.Audio;
using Jellyfin.Plugin.Subtitles.Generation;
using Jellyfin.Plugin.Subtitles.Pricing;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// Stage 4: whole videos transcribed in chunks, and paid services metered per video. Invented speech throughout.
public sealed class TranscriberTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "subs-transcriber-" + Guid.NewGuid().ToString("N"));

    public TranscriberTests()
    {
        Directory.CreateDirectory(_dir);
        var today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(_dir, "rates.json"), "{\"Date\":\"" + today + "\",\"Source\":\"test\",\"Rates\":{\"EUR\":1,\"USD\":1.10,\"AUD\":1.65}}");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static SpendLimits Aud(decimal? limit) => new("AUD", limit, new Dictionary<string, decimal>(), 0m);

    // Audio of a given length; remembers what was read
    internal sealed class FakeAudio(TimeSpan total) : IAudioSource
    {
        public List<(TimeSpan Start, TimeSpan Length)> Reads { get; } = [];

        public Task<float[]> ReadAsync(TimeSpan start, TimeSpan length, CancellationToken cancellationToken)
        {
            Reads.Add((start, length));
            var available = Math.Max(0, Math.Min(length.TotalSeconds, total.TotalSeconds - start.TotalSeconds));
            return Task.FromResult(new float[(int)(available * AudioFormat.SampleRate)]);
        }
    }

    // Hears one word every 20 s of the chunk, named by the chunk's number and the time within it
    internal sealed class FakeSpeech(string id = "local") : ISpeechToText
    {
        public List<string?> Languages { get; } = [];

        public int FailOnCall { get; init; } = -1;

        public string Id => id;

        public Task<Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken cancellationToken)
        {
            Languages.Add(language);
            if (Languages.Count - 1 == FailOnCall)
            {
                throw new SpeechToTextException("service down");
            }

            var seconds = samples.Length / (double)AudioFormat.SampleRate;
            var words = new List<TranscribedWord>();
            for (double t = 1; t < seconds - 1; t += 20)
            {
                words.Add(new TranscribedWord("word" + Languages.Count, t, t + 0.4, 0.9));
            }

            return Task.FromResult(new Transcript(words, language, id, "m", seconds));
        }
    }

    [Fact]
    public async Task A_video_is_read_and_transcribed_in_chunks_on_its_own_clock()
    {
        var audio = new FakeAudio(TimeSpan.FromMinutes(25));
        var speech = new FakeSpeech();
        var full = await FullTranscriber.TranscribeAsync(audio, TimeSpan.FromMinutes(25), speech, "en", TestContext.Current.CancellationToken);

        Assert.Equal([TimeSpan.Zero, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20)], audio.Reads.Select(r => r.Start));
        Assert.All(speech.Languages, l => Assert.Equal("en", l));
        Assert.Equal(3, full.Chunks);
        Assert.Equal("local", full.Provider);
        Assert.Equal((605 * 2) + 300, full.SecondsSent, 3);

        // The second chunk's words are 600 s later on the video's clock (its word at 601 s falls in the first chunk's half
        // of the overlap, so the first chunk's word at 601 s stands); none are repeated
        Assert.Contains(full.Words, w => w.Text == "word1" && Math.Abs(w.Start - 601) < 1e-9);
        Assert.Contains(full.Words, w => w.Text == "word2" && Math.Abs(w.Start - 621) < 1e-9);
        Assert.DoesNotContain(full.Words, w => w.Text == "word2" && w.Start < 610);
        Assert.Equal(full.Words.Count, full.Words.Select(w => w.Start).Distinct().Count());
        Assert.True(full.Words.Zip(full.Words.Skip(1)).All(p => p.First.Start <= p.Second.Start));
    }

    [Fact]
    public async Task A_paid_service_reserves_the_whole_video_first_and_records_what_was_sent()
    {
        using var spending = new Spending(_dir);

        // 25 minutes planned (25 min 10 s with overlaps) of Nova-3 is about USD 0.108 = AUD 0.16: a limit of AUD 0.10 refuses it
        // before any audio is sent
        var refused = new FakeSpeech("deepgram");
        var small = new MeteredSpeechToText(refused, "nova-3", spending, Aud(0.10m), "subtitles.generate");
        var ex = await Assert.ThrowsAsync<SpeechToTextException>(() => FullTranscriber.TranscribeAsync(new FakeAudio(TimeSpan.FromMinutes(25)), TimeSpan.FromMinutes(25), small, "en", TestContext.Current.CancellationToken));
        Assert.Contains("monthly limit", ex.Message, StringComparison.Ordinal);
        Assert.Empty(refused.Languages);

        // Within the limit: one reservation, settled at the audio sent
        var metered = new MeteredSpeechToText(new FakeSpeech("deepgram"), "nova-3", spending, Aud(5m), "subtitles.generate");
        var full = await FullTranscriber.TranscribeAsync(new FakeAudio(TimeSpan.FromMinutes(25)), TimeSpan.FromMinutes(25), metered, "en", TestContext.Current.CancellationToken);
        var expected = 0.0043m * (decimal)full.SecondsSent / 60m * 1.5m;
        Assert.InRange(spending.Ledger.ThisMonth(Aud(5m), spending.Rates.Current).Total!.Value, expected - 0.00001m, expected + 0.00001m);
    }

    [Fact]
    public async Task A_paid_service_failing_part_way_is_still_recorded_for_the_audio_it_heard()
    {
        using var spending = new Spending(_dir);
        var metered = new MeteredSpeechToText(new FakeSpeech("deepgram") { FailOnCall = 1 }, "nova-3", spending, Aud(5m), "subtitles.generate");
        await Assert.ThrowsAsync<SpeechToTextException>(() => FullTranscriber.TranscribeAsync(new FakeAudio(TimeSpan.FromMinutes(25)), TimeSpan.FromMinutes(25), metered, "en", TestContext.Current.CancellationToken));

        // Only the first chunk (605 s) was heard and billed; the second failed
        var expected = 0.0043m * 605m / 60m * 1.5m;
        Assert.InRange(spending.Ledger.ThisMonth(Aud(5m), spending.Rates.Current).Total!.Value, expected - 0.00001m, expected + 0.00001m);
    }

    [Fact]
    public void Bare_words_take_their_punctuation_from_segments()
    {
        var transcript = new Transcript(
            [new("hello", 0, 0.4, null), new("there", 0.5, 0.9, null), new("how", 1.5, 1.7, null), new("are", 1.7, 1.8, null), new("you", 1.8, 2.0, null)],
            "en",
            "openai",
            "whisper-1",
            3)
        {
            Segments = [new("Hello there.", 0, 1.0), new("How are you?", 1.5, 2.0)],
        };

        Assert.Equal(["Hello", "there.", "How", "are", "you?"], FullTranscriber.WordsOf(transcript).Select(w => w.Text));

        // Words that already have punctuation are kept as they are
        var punctuated = transcript with { Words = [new("Hi,", 0, 0.3, null), new("you", 0.4, 0.6, null)] };
        Assert.Equal(["Hi,", "you"], FullTranscriber.WordsOf(punctuated).Select(w => w.Text));
    }

    [Fact]
    public void Segments_without_word_times_are_spread_by_length()
    {
        var words = FullTranscriber.WordsOf(new Transcript([], "en", "local", "m", 10) { Segments = [new("One two three.", 2, 5)] });
        Assert.Equal(["One", "two", "three."], words.Select(w => w.Text));
        Assert.Equal(2, words[0].Start, 6);
        Assert.True(words[2].End <= 5);
        Assert.True(words[1].Start > words[0].Start && words[2].Start > words[1].Start);
    }

    private sealed class Handler(string body) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add((request, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task For_subtitles_deepgram_is_asked_to_punctuate_and_checks_are_not()
    {
        const string Reply = """{"results":{"channels":[{"alternatives":[{"words":[{"word":"hi","punctuated_word":"Hi.","start":0.2,"end":0.4,"confidence":0.99}]}]}]}}""";
        using var handler = new Handler(Reply);
        using var http = new HttpClient(handler);

        var forSubtitles = await new DeepgramSpeechToText(http, "dg-key-0123456789", string.Empty, punctuate: true).TranscribeAsync(new float[16000], "en", TestContext.Current.CancellationToken);
        var forChecks = await new DeepgramSpeechToText(http, "dg-key-0123456789", string.Empty).TranscribeAsync(new float[16000], "en", TestContext.Current.CancellationToken);

        Assert.Contains("punctuate=true", handler.Seen[0].Request.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("punctuate=false", handler.Seen[1].Request.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Equal("Hi.", forSubtitles.Words[0].Text);
        Assert.Equal("hi", forChecks.Words[0].Text);
    }

    [Fact]
    public async Task For_subtitles_openai_compatible_services_are_asked_for_segments_too()
    {
        const string Reply = """{"language":"en","words":[{"word":"hi","start":0.1,"end":0.3}],"segments":[{"text":" Hi.","start":0.1,"end":0.3},{"text":"","start":1,"end":2},{"text":"bad","start":3,"end":1}]}""";
        using var handler = new Handler(Reply);
        using var http = new HttpClient(handler);

        var t = await new OpenAiCompatibleSpeechToText(http, "local", new Uri("http://127.0.0.1:8000/v1"), null, string.Empty, withSegments: true).TranscribeAsync(new float[16000], "en", TestContext.Current.CancellationToken);
        var plain = await new OpenAiCompatibleSpeechToText(http, "local", new Uri("http://127.0.0.1:8000/v1"), null, string.Empty).TranscribeAsync(new float[16000], "en", TestContext.Current.CancellationToken);

        Assert.Contains("segment", handler.Seen[0].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\nsegment\r\n", handler.Seen[1].Body, StringComparison.Ordinal);
        Assert.Equal("Hi.", Assert.Single(t.Segments).Text);
        Assert.Empty(plain.Segments);
        Assert.Equal(["Hi."], FullTranscriber.WordsOf(t).Select(w => w.Text));
    }
}
