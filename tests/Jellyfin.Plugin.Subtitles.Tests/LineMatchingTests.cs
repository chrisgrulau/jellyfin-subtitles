using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Ai;
using Jellyfin.Plugin.Subtitles.Ai;
using Jellyfin.Plugin.Subtitles.Audio;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.Pipeline;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Jellyfin.Plugin.Subtitles.Sync;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// Subtitles whose wording differs from what is said (a translation here), matched by meaning. Invented text throughout.
public partial class LineMatchingTests
{
    private static SubtitleDocument Story()
        => new() { Format = SubtitleFormat.Srt, Cues = [.. Enumerable.Range(0, 300).Select(i => new SubtitleCue { Start = TimeSpan.FromSeconds(10 + (i * 5)), End = TimeSpan.FromSeconds(12 + (i * 5)), Text = $"Line {i} of the invented story" })] };

    // Audio that says each line in another language, a fixed time later than the subtitles say
    private sealed class Translated(SubtitleDocument said, double lateBy) : IAudioSource, ISpeechToText
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
            for (var i = 0; i < said.Cues.Count; i++)
            {
                var at = said.Cues[i].Start.TotalSeconds + lateBy - from;
                if (at < 0 || at > 58)
                {
                    continue;
                }

                var parts = $"Linea {i} de la historia inventada.".Split(' ');
                words.AddRange(parts.Select((w, k) => new TranscribedWord(w, at + (k * 0.3), at + (k * 0.3) + 0.25, 0.9)));
            }

            return Task.FromResult(new Transcript(words, "es", "fake", "fake", samples.Length / (double)AudioFormat.SampleRate));
        }
    }

    private sealed class Matcher(Func<IReadOnlyList<HeardPhrase>, IReadOnlyList<CueLine>, LineMatch> answer) : ILineMatcher
    {
        public int Calls { get; private set; }

        public Task<LineMatch> MatchAsync(IReadOnlyList<HeardPhrase> phrases, IReadOnlyList<CueLine> cues, string? language, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(answer(phrases, cues));
        }
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex Number();

    // What a good matcher would do: pair "Linea N …" with "Line N …"
    private static LineMatch ByNumber(IReadOnlyList<HeardPhrase> phrases, IReadOnlyList<CueLine> cues)
        => new(LineVerdict.SameContent, [.. phrases.Select(p => new LinePair(p.Index, cues.ToList().FindIndex(c => Number().Match(c.Text).Value == Number().Match(p.Text).Value))).Where(p => p.Cue >= 0)], "A Spanish soundtrack with English subtitles.", "AI (test)");

    private static Task<SyncOutcome> Check(double lateBy, ILineMatcher? matcher)
    {
        var story = Story();
        var audio = new Translated(story, lateBy);
        return new SyncCheck(audio, audio, refine: false, matcher: matcher).RunAsync(story, TimeSpan.FromMinutes(25), "en", CancellationToken.None);
    }

    [Fact]
    public async Task A_translation_is_timed_from_lines_matched_by_meaning()
    {
        var matcher = new Matcher(ByNumber);

        var outcome = await Check(7, matcher);

        Assert.Equal(1, matcher.Calls);
        Assert.Equal(SyncStatus.Corrected, outcome.Model.Status);
        Assert.Equal(7, outcome.Model.Offset, 1);
        Assert.Equal("lines by meaning", outcome.Stage);
        Assert.False(outcome.WrongLanguageSuspected);
        Assert.Contains("AI (test)", outcome.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_matcher_the_translation_is_still_flagged_as_another_language()
    {
        var outcome = await Check(7, null);

        Assert.True(outcome.WrongLanguageSuspected);
        Assert.Equal(SyncStatus.Unreliable, outcome.Model.Status);
    }

    [Fact]
    public async Task Different_content_confirms_the_mismatch()
    {
        var outcome = await Check(7, new Matcher((_, _) => new LineMatch(LineVerdict.Different, [], "Commentary, not dialogue.", "AI (test)")));

        Assert.True(outcome.WrongLanguageSuspected);
        Assert.Contains("don't say what is said", outcome.Note, StringComparison.Ordinal);
        Assert.Contains("Commentary", outcome.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pairs_that_dont_agree_on_one_timing_are_not_used()
    {
        // Every phrase paired with a random line: no shift fits most of them
        var random = new Random(4);
        var outcome = await Check(7, new Matcher((phrases, cues) => new LineMatch(LineVerdict.SameContent, [.. phrases.Select(p => new LinePair(p.Index, random.Next(cues.Count)))], "x", "AI (test)")));

        Assert.Equal(SyncStatus.Unreliable, outcome.Model.Status);
        Assert.Contains("don't agree on one timing", outcome.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsure_keeps_the_usual_answer_with_the_note()
    {
        var outcome = await Check(7, new Matcher((_, _) => new LineMatch(LineVerdict.Unsure, [], "The AI plugin couldn't tell: too little dialogue.", null)));

        Assert.True(outcome.WrongLanguageSuspected);
        Assert.Contains("too little dialogue", outcome.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Heard_words_become_phrases_at_pauses_and_sentence_ends()
    {
        var t = new Transcript(
            [new("Where", 0, 0.2, null), new("were", 0.25, 0.4, null), new("you?", 0.45, 0.7, null), new("Out", 0.8, 1.0, null), new("walking", 2.5, 2.9, null)],
            "en",
            "fake",
            "fake",
            60);

        var phrases = MeaningAligner.Phrases([(100, t)]);

        Assert.Equal(["Where were you?", "Out", "walking"], phrases.Select(p => p.Text));
        Assert.Equal([100, 100.8, 102.5], phrases.Select(p => p.Start));
        Assert.Equal([0, 1, 2], phrases.Select(p => p.Index));
    }

    [Fact]
    public void Only_lines_near_the_heard_stretches_are_offered()
    {
        var cues = MeaningAligner.Cues(Story(), [(600, 60)]);

        Assert.All(cues, c => Assert.InRange(c.Start, 600 - MeaningAligner.Margin, 660 + MeaningAligner.Margin));
        Assert.Equal(Enumerable.Range(0, cues.Count), cues.Select(c => c.Index));
    }

    [Fact]
    public void Pairs_naming_lines_not_offered_or_reusing_one_are_ignored()
    {
        IReadOnlyList<HeardPhrase> phrases = [new(0, 10, "a"), new(1, 20, "b")];
        IReadOnlyList<CueLine> cues = [new(0, 5, "A"), new(1, 15, "B")];

        var anchors = MeaningAligner.Anchors(phrases, cues, [new(0, 0), new(1, 0), new(1, 5), new(-1, 1), new(1, 1)]);

        Assert.Equal([(5.0, 10.0), (15.0, 20.0)], anchors.Select(a => (a.SubtitleTime, a.AudioTime)));
    }

    private static AiReply Ok(string json) => new(true, JsonDocument.Parse(json).RootElement.Clone(), "claude-opus-5-5", null, null);

    [Fact]
    public void Replies_keep_only_offered_pairs()
    {
        var same = AiLineMatcher.Read(Ok("{\"verdict\":\"same\",\"pairs\":[{\"heard\":0,\"line\":1},{\"heard\":9,\"line\":0},{\"heard\":\"1\",\"line\":0}],\"reason\":\"Translated.\"}"), 2, 2);
        Assert.Equal(LineVerdict.SameContent, same.Verdict);
        Assert.Equal([new LinePair(0, 1)], same.Pairs);
        Assert.Equal("AI (claude-opus-5-5)", same.By);

        Assert.Equal(LineVerdict.Different, AiLineMatcher.Read(Ok("{\"verdict\":\"different\",\"pairs\":[],\"reason\":\"x\"}"), 2, 2).Verdict);
        Assert.Equal(LineVerdict.Unsure, AiLineMatcher.Read(Ok("{\"verdict\":\"maybe\",\"pairs\":[],\"reason\":\"x\"}"), 2, 2).Verdict);
        Assert.Equal(string.Empty, AiLineMatcher.Read(new AiReply(false, null, null, "Not installed.", "not-installed"), 2, 2).Note);
        Assert.Contains("couldn't help", AiLineMatcher.Read(new AiReply(false, null, null, "Over the limit.", "provider-limit"), 2, 2).Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_phrases_and_lines_are_sent_and_the_run_limit_holds()
    {
        string? sent = null;
        var calls = 0;
        var matcher = new AiLineMatcher(1, (caller, purpose, instructions, data, schema, max, effort, ct) =>
        {
            calls++;
            sent = JsonSerializer.Serialize(data);
            Assert.Equal("subtitles", caller);
            Assert.Equal("subtitles.lines", purpose);
            return Task.FromResult(Ok("{\"verdict\":\"unsure\",\"pairs\":[],\"reason\":\"x\"}"));
        });

        await matcher.MatchAsync([new(0, 1, "hola")], [new(0, 2, "hello")], "en", TestContext.Current.CancellationToken);
        var second = await matcher.MatchAsync([new(0, 1, "hola")], [new(0, 2, "hello")], "en", TestContext.Current.CancellationToken);

        Assert.Equal(1, calls);
        Assert.Contains("limit", second.Note, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(sent!);
        Assert.Equal(["subtitleLanguage", "heard", "lines"], doc.RootElement.EnumerateObject().Select(p => p.Name));
    }
}
