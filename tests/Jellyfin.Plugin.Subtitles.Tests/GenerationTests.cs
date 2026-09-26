using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Subtitles.Generation;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// Stage 4: cues built from a full transcript, and chunks merged at their seams. Invented speech throughout.
public sealed class GenerationTests
{
    // Words spoken one after another, each lasting `each` seconds with `gap` seconds between them
    private static List<TranscribedWord> Say(double at, string text, double each = 0.3, double gap = 0.05)
    {
        var words = new List<TranscribedWord>();
        foreach (var w in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            words.Add(new TranscribedWord(w, at, at + each, 0.9));
            at += each + gap;
        }

        return words;
    }

    private static void AssertWellFormed(IReadOnlyList<Formats.SubtitleCue> cues)
    {
        for (var i = 0; i < cues.Count; i++)
        {
            var c = cues[i];
            Assert.True(c.End > c.Start);
            Assert.True(c.Duration.TotalSeconds <= 7.0 + 1e-9, "longer than 7 s: " + c.Text);
            var lines = c.Text.Split('\n');
            Assert.InRange(lines.Length, 1, 2);
            Assert.All(lines, l => Assert.True(l.Length <= 42, "line too long: " + l));
            if (i + 1 < cues.Count)
            {
                Assert.True((cues[i + 1].Start - c.End).TotalMilliseconds >= 80 - 1e-6, "too close: " + c.Text);
            }
        }
    }

    [Fact]
    public void A_short_sentence_becomes_one_cue_lengthened_to_a_second()
    {
        var cues = TranscriptCues.Build(Say(10, "Hello there."));
        var cue = Assert.Single(cues);
        Assert.Equal("Hello there.", cue.Text);
        Assert.Equal(TimeSpan.FromSeconds(10), cue.Start);
        Assert.Equal(TimeSpan.FromSeconds(11), cue.End);
    }

    [Fact]
    public void Long_speech_is_wrapped_into_two_balanced_lines_of_at_most_42()
    {
        var words = Say(0, "we walked along the river until the lights of the town came into view and nobody said a word about it at all", 0.2, 0.02);
        var cues = TranscriptCues.Build(words);
        AssertWellFormed(cues);
        Assert.True(cues.Count >= 2);
        Assert.Contains(cues, c => c.Text.Contains('\n', StringComparison.Ordinal));
        Assert.Equal(string.Join(' ', words.Select(w => w.Text)), string.Join(' ', cues.Select(c => c.Text.Replace('\n', ' '))));
        var two = cues.First(c => c.Text.Contains('\n', StringComparison.Ordinal)).Text.Split('\n');
        Assert.True(Math.Abs(two[0].Length - two[1].Length) <= 12);
    }

    [Fact]
    public void Wrap_prefers_balanced_lines_and_refuses_what_needs_three()
    {
        Assert.Equal("short line", TranscriptCues.Wrap("short line", 42));
        var wrapped = TranscriptCues.Wrap("the quick brown fox jumps over the lazy dog again and again", 42)!;
        var lines = wrapped.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.True(l.Length <= 42));
        Assert.Null(TranscriptCues.Wrap(string.Join(' ', Enumerable.Repeat("wordy", 20)), 42));
    }

    [Fact]
    public void A_pause_starts_a_new_cue()
    {
        var words = Say(1, "are you coming");
        words.AddRange(Say(words[^1].End + 0.9, "in a minute"));
        var cues = TranscriptCues.Build(words);
        Assert.Equal(2, cues.Count);
        Assert.Equal("are you coming", cues[0].Text);
        Assert.Equal("in a minute", cues[1].Text);
        AssertWellFormed(cues);
    }

    [Fact]
    public void A_sentence_end_starts_a_new_cue_unless_the_cue_is_tiny()
    {
        var cues = TranscriptCues.Build(Say(0, "I told you about the boat. She never listened to me."));
        Assert.Equal(["I told you about the boat.", "She never listened to me."], cues.Select(c => c.Text));

        var tiny = TranscriptCues.Build(Say(0, "Yes. I know."));
        Assert.Equal("Yes. I know.", Assert.Single(tiny).Text);
    }

    [Fact]
    public void A_cue_cut_by_length_ends_after_a_comma_in_its_second_half()
    {
        var cues = TranscriptCues.Build(Say(0, "when the morning came and the fog had lifted from the harbour, we loaded the cart and set off towards the hills", 0.15, 0.02));
        AssertWellFormed(cues);
        Assert.EndsWith("harbour,", cues[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Cues_last_at_most_seven_seconds_even_for_slow_speech()
    {
        var cues = TranscriptCues.Build(Say(0, "one two three four five six seven eight nine ten", 0.6, 0.5));
        AssertWellFormed(cues);
        Assert.True(cues.Count >= 2);
    }

    [Fact]
    public void Reading_speed_lengthens_into_silence_but_not_into_the_next_cue()
    {
        // 38 characters spoken in about a second: at 20 characters a second it wants 1.9 s
        var fast = Say(0, "absolutely everything happened at once", 0.18, 0.02);
        var alone = Assert.Single(TranscriptCues.Build(fast));
        Assert.Equal(1.9, alone.Duration.TotalSeconds, 2);

        // The next line starts 1.2 s in: this one stops 80 ms before it
        var crowded = new List<TranscribedWord>(fast);
        crowded.AddRange(Say(fast[^1].End + 0.65, "Really?"));
        var cues = TranscriptCues.Build(crowded);
        Assert.Equal(2, cues.Count);
        Assert.Equal(cues[1].Start - TimeSpan.FromMilliseconds(80), cues[0].End);
        AssertWellFormed(cues);
    }

    [Fact]
    public void Overlapping_word_times_never_make_overlapping_cues()
    {
        var words = new List<TranscribedWord>
        {
            new("First.", 5.0, 5.9, null),
            new("Second", 5.85, 6.2, null),
            new("line.", 6.1, 6.4, null),
            new("Third", 6.41, 6.5, null),
        };
        var cues = TranscriptCues.Build(words, CueRules.Default with { MinSentenceCue = 1 });
        Assert.Equal(3, cues.Count);
        AssertWellFormed(cues);
    }

    [Fact]
    public void Sound_descriptions_and_music_are_not_speech()
    {
        var words = new List<TranscribedWord>
        {
            new("[Music]", 0, 2, null),
            new("♪", 2, 3, null),
            new("(door", 3, 3.2, null),
            new("closes)", 3.2, 3.4, null),
            new("Hello.", 4, 4.4, null),
            new("--", 4.5, 4.6, null),
        };
        var speech = TranscriptCues.SpeechWords(words);
        Assert.Equal(["Hello."], speech.Select(w => w.Text));
        Assert.Empty(TranscriptCues.Build([new TranscribedWord("[BLANK_AUDIO]", 0, 30, null)]));
    }

    [Fact]
    public void Chinese_and_japanese_words_are_joined_without_spaces()
    {
        Assert.Equal("你好世界", TranscriptCues.Join([new("你好", 0, 1, null), new("世界", 1, 2, null)]));
        Assert.Equal("hello world", TranscriptCues.Join([new("hello", 0, 1, null), new("world", 1, 2, null)]));
    }

    [Fact]
    public void Chunks_cover_the_video_with_overlap_and_no_useless_tail()
    {
        var plan = TranscriptChunks.Plan(TimeSpan.FromMinutes(25));
        Assert.Equal(3, plan.Count);
        Assert.Equal(TimeSpan.Zero, plan[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(605), plan[0].Length);
        Assert.Equal(TimeSpan.FromMinutes(10), plan[1].Start);
        Assert.Equal(TimeSpan.FromMinutes(25), plan[2].End);

        // 20 min + 3 s: the last 3 s were heard by the second chunk's overlap
        var tail = TranscriptChunks.Plan(TimeSpan.FromSeconds(1203));
        Assert.Equal(2, tail.Count);
        Assert.Equal(TimeSpan.FromSeconds(1203), tail[1].End);

        Assert.Single(TranscriptChunks.Plan(TimeSpan.FromMinutes(3)));
        Assert.Empty(TranscriptChunks.Plan(TimeSpan.Zero));

        // 10 minutes of 16 kHz mono 16-bit WAV stays well under a 25 MB upload limit
        Assert.True((plan[0].Length.TotalSeconds * 16000 * 2) + 44 < 20 * 1024 * 1024);
    }

    [Fact]
    public void Merging_moves_words_to_the_video_clock_and_keeps_seam_words_once()
    {
        var a = new AudioChunk(TimeSpan.Zero, TimeSpan.FromSeconds(605));
        var b = new AudioChunk(TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(300));

        // Both chunks heard the overlap (600–605 s); the seam is at 602.5 s
        var first = new List<TranscribedWord>
        {
            new("before", 590, 590.5, null),
            new("across", 601.0, 601.5, null),
            new("seam", 602.0, 602.6, null),       // midpoint 602.3: the first chunk's half
            new("cutoff", 604.8, 605.0, null),     // cut off by the chunk's end
        };
        var second = new List<TranscribedWord>
        {
            new("across", 1.0, 1.5, null),         // 601.0: belongs to the first chunk's half
            new("Seam", 2.4, 2.9, null),           // 602.4–602.9, midpoint 602.65: the second chunk's half, yet the same word
            new("cutoff", 4.7, 5.2, null),
            new("after", 20, 20.5, null),
        };

        var merged = TranscriptChunks.Merge([(a, first), (b, second)]);
        Assert.Equal(["before", "across", "seam", "cutoff", "after"], merged.Select(w => w.Text));
        Assert.Equal(604.7, merged.Single(w => w.Text == "cutoff").Start, 6);
        Assert.Equal(620, merged[^1].Start, 6);
    }

    [Fact]
    public void Merging_a_single_chunk_moves_it_by_its_start_only()
    {
        var merged = TranscriptChunks.Merge([(new AudioChunk(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)), [new TranscribedWord("hi", 1, 2, null)])]);
        Assert.Equal(31, Assert.Single(merged).Start);
    }
}
