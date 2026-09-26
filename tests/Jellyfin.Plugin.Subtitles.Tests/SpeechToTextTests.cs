using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Jellyfin.Plugin.Subtitles.Sync;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// Invented dialogue throughout.
public class SpeechToTextTests
{
    private sealed class FakeHandler(HttpStatusCode status, string body, Action<HttpRequestMessage, string>? inspect = null) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var content = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            inspect?.Invoke(request, content);
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (status == HttpStatusCode.TooManyRequests)
            {
                response.Headers.TryAddWithoutValidation("Retry-After", "42");
            }

            return response;
        }
    }

    private const string VerboseJson = """
        {"language":"english","duration":3.0,"text":"Where did you park",
         "words":[{"word":" Where","start":0.5,"end":0.7,"probability":0.9},{"word":" did","start":0.7,"end":0.8,"probability":0.95},
                  {"word":" you","start":0.8,"end":0.9},{"word":" park?","start":0.9,"end":1.3,"probability":0.8},
                  {"word":"bad","start":-1,"end":2},{"word":"worse","start":3,"end":1},{"start":1,"end":2}]}
        """;

    [Fact]
    public async Task A_local_service_is_asked_for_word_timings_and_its_reply_is_checked()
    {
        var sent = string.Empty;
        using var handler = new FakeHandler(HttpStatusCode.OK, VerboseJson, (_, body) => sent = body);
        using var http = new HttpClient(handler);
        var stt = new OpenAiCompatibleSpeechToText(http, "local", new Uri("http://127.0.0.1:8000/v1"), null, string.Empty);

        var transcript = await stt.TranscribeAsync(new float[16000], "en", TestContext.Current.CancellationToken);

        Assert.Equal("http://127.0.0.1:8000/v1/audio/transcriptions", handler.Requests[0].RequestUri!.AbsoluteUri);
        Assert.Null(handler.Requests[0].Headers.Authorization);
        Assert.Contains("verbose_json", sent, StringComparison.Ordinal);
        Assert.Contains("timestamp_granularities[]", sent, StringComparison.Ordinal);
        Assert.Equal(["Where", "did", "you", "park?"], transcript.Words.Select(w => w.Text));
        Assert.Null(transcript.Words[2].Confidence);
        Assert.Equal("en", transcript.Language);
        Assert.Equal(1.0, transcript.AudioSeconds, 3);
    }

    [Fact]
    public async Task Deepgram_is_called_with_its_token_and_its_words_are_read()
    {
        const string Reply = """{"metadata":{"duration":3.0},"results":{"channels":[{"alternatives":[{"transcript":"hi there","words":[{"word":"hi","start":0.2,"end":0.4,"confidence":0.99},{"word":"there","start":0.4,"end":0.8,"confidence":0.97}]}]}]}}""";
        using var handler = new FakeHandler(HttpStatusCode.OK, Reply);
        using var http = new HttpClient(handler);
        var stt = new DeepgramSpeechToText(http, "dg-key-0123456789", string.Empty);

        var transcript = await stt.TranscribeAsync(new float[16000], "en", TestContext.Current.CancellationToken);

        var request = handler.Requests[0];
        Assert.Equal("Token", request.Headers.Authorization!.Scheme);
        Assert.Contains("model=nova-3", request.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("language=en", request.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Equal(["hi", "there"], transcript.Words.Select(w => w.Text));
        Assert.Equal(0.99, transcript.Words[0].Confidence);
    }

    [Fact]
    public async Task Failures_are_classified_and_never_show_the_key()
    {
        const string Key = "sk-secret-0123456789abcdef";
        using var handler = new FakeHandler(HttpStatusCode.Unauthorized, $$"""{"error":"Incorrect API key provided: {{Key}}"}""");
        using var http = new HttpClient(handler);
        var stt = new OpenAiCompatibleSpeechToText(http, "openai", new Uri("https://api.example.test/v1/"), Key, string.Empty);

        var ex = await Assert.ThrowsAsync<SpeechToTextException>(() => stt.TranscribeAsync(new float[1600], null, TestContext.Current.CancellationToken));

        Assert.Equal(Jellyfin.Plugin.Common.Resilience.FailureClass.Authentication, ex.Failure);
        Assert.DoesNotContain(Key, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Key[^12..], ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rate_limit_is_transient()
    {
        using var handler = new FakeHandler(HttpStatusCode.TooManyRequests, """{"error":"slow down"}""");
        using var http = new HttpClient(handler);
        var stt = new DeepgramSpeechToText(http, "dg-key-0123456789", string.Empty);

        var ex = await Assert.ThrowsAsync<SpeechToTextException>(() => stt.TranscribeAsync(new float[1600], null, TestContext.Current.CancellationToken));

        Assert.Equal(Jellyfin.Plugin.Common.Resilience.FailureClass.Transient, ex.Failure);
    }

    [Theory]
    [InlineData("http://api.example.test/v1", true)]
    [InlineData("http://192.168.1.20:8000/v1", false)]
    [InlineData("http://localhost:8000/v1", false)]
    [InlineData("https://api.example.test/v1", false)]
    [InlineData("ftp://example.test/v1", true)]
    public void A_key_is_never_sent_over_plain_http_to_the_internet(string address, bool refused)
    {
        void Check() => OpenAiCompatibleSpeechToText.CheckAddress(new Uri(address), "key-0123456789");

        if (refused)
        {
            Assert.Throws<SpeechToTextException>(Check);
        }
        else
        {
            Check();
        }
    }

    [Fact]
    public void Wav_files_have_the_right_header_and_samples()
    {
        var wav = WavEncoder.Encode([0f, 1f, -1f, 2f]);

        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(44 + 8, wav.Length);
        Assert.Equal(32767, BitConverter.ToInt16(wav, 46));
        Assert.Equal(-32767, BitConverter.ToInt16(wav, 48));
        Assert.Equal(32767, BitConverter.ToInt16(wav, 50));
    }

    [Theory]
    [InlineData("eng", "en")]
    [InlineData("fre", "fr")]
    [InlineData("ger", "de")]
    [InlineData("en-US", "en")]
    [InlineData("English", "en")]
    [InlineData("Klingon-ish", null)]
    [InlineData(null, null)]
    public void Language_codes_become_two_letters(string? code, string? expected) => Assert.Equal(expected, Languages.ToTwoLetter(code));

    private static SubtitleDocument Script(double shift, double scale)
    {
        // A long invented script: every line different, so three-word runs are unique
        var cues = Enumerable.Range(0, 400).Select(i =>
        {
            var start = 10 + (i * 4.0);
            return new SubtitleCue { Start = TimeSpan.FromSeconds((start - shift) / scale), End = TimeSpan.FromSeconds((start + 2.5 - shift) / scale), Text = $"Line number {i} says word{i}a then word{i}b" };
        });
        return new SubtitleDocument { Format = SubtitleFormat.Srt, Cues = [.. cues] };
    }

    private static List<(double Start, Transcript Transcript)> Heard(params double[] snippetStarts)
        => [.. snippetStarts.Select(s =>
        {
            var words = new List<TranscribedWord>();
            for (var i = 0; i < 400; i++)
            {
                var t = 10 + (i * 4.0);
                if (t < s || t > s + 60)
                {
                    continue;
                }

                var parts = new[] { "Line", "number", i.ToString(System.Globalization.CultureInfo.InvariantCulture), "says", $"word{i}a", "then", $"word{i}b" };
                words.AddRange(parts.Select((p, k) => new TranscribedWord(p, t - s + (k * 0.3), t - s + (k * 0.3) + 0.25, 0.9)));
            }

            return (s, new Transcript(words, "en", "fake", "fake", 60));
        })];

    [Theory]
    [InlineData(0, 1)]
    [InlineData(12.5, 1)]
    [InlineData(-3, 25 / (24000 / 1001.0))]
    public void Words_heard_line_the_subtitles_up(double shift, double scale)
    {
        var model = TranscriptAligner.Solve(TranscriptAligner.Anchors(Script(shift, scale), Heard(300, 800, 1400)), wordLag: 0);

        if (shift == 0 && scale == 1)
        {
            Assert.Equal(SyncStatus.InSync, model.Status);
        }
        else
        {
            Assert.Equal(SyncStatus.Corrected, model.Status);
            Assert.Equal(scale, model.Scale, 6);
            Assert.Equal(shift, model.Offset, 0.05);
        }
    }

    [Fact]
    public void Another_films_subtitles_match_nothing()
    {
        var other = new SubtitleDocument { Format = SubtitleFormat.Srt, Cues = [.. Enumerable.Range(0, 300).Select(i => new SubtitleCue { Start = TimeSpan.FromSeconds(i * 5), End = TimeSpan.FromSeconds((i * 5) + 2), Text = $"Something else entirely {i} here" })] };

        var model = TranscriptAligner.Solve(TranscriptAligner.Anchors(other, Heard(300, 800)));

        Assert.Equal(SyncStatus.Unreliable, model.Status);
    }

    [Fact]
    public void Sound_descriptions_and_speaker_labels_are_not_words_to_match()
    {
        Assert.Equal("dont", TranscriptAligner.Normalise("Don't"));
        Assert.Equal("café", TranscriptAligner.Normalise("Café!"));
        var doc = new SubtitleDocument { Format = SubtitleFormat.Srt, Cues = [new SubtitleCue { Start = TimeSpan.FromSeconds(1), End = TimeSpan.FromSeconds(3), Text = "JOHN: [door slams] Where did you park?" }] };
        var heard = new List<(double, Transcript)> { (0, new Transcript([new("where", 1, 1.2, 1), new("did", 1.2, 1.3, 1), new("you", 1.3, 1.4, 1), new("park", 1.4, 1.8, 1)], "en", "f", "f", 5)) };

        var anchors = TranscriptAligner.Anchors(doc, heard);

        Assert.Equal(2, anchors.Count);
        Assert.True(anchors[0].AtCueStart);
        Assert.Equal(1, anchors[0].SubtitleTime, 3);
    }

    [Fact]
    public void Snippets_go_where_the_dialogue_is()
    {
        var doc = Script(0, 1);

        var plan = TranscriptSynchroniser.Plan(doc, TimeSpan.FromMinutes(30));

        Assert.Equal(TranscriptSynchroniser.Snippets, plan.Count);
        Assert.All(plan, p => Assert.InRange(p.TotalMinutes, 2.4, 26.6));
        Assert.True(plan.Zip(plan.Skip(1)).All(p => (p.Second - p.First).TotalMinutes >= 3));
    }
}
