using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Subtitles.Discrepancy;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// Stage 4: comparing a whole subtitle with a full transcript. Invented lines throughout.
public sealed class DiscrepancyTests
{
    // Ten lines, four seconds apart; what is heard says the same unless a test changes it
    internal static readonly (double Start, string Text)[] Script =
    [
        (1, "Good morning, everyone."),
        (5, "The train leaves at noon."),
        (9, "Did you pack the blue suitcase?"),
        (13, "I left it by the door."),
        (17, "Then we should hurry up."),
        (21, "Tell Katherine to wait outside."),
        (25, "I have five tickets here."),
        (29, "We can make it in time."),
        (33, "Nobody saw the old map."),
        (37, "Let's go before it rains."),
    ];

    internal static SubtitleDocument Subtitle(IEnumerable<(double Start, string Text)> lines, double shift = 0)
        => new()
        {
            Format = SubtitleFormat.Srt,
            Cues = [.. lines.Select(l => new SubtitleCue { Start = TimeSpan.FromSeconds(l.Start + shift), End = TimeSpan.FromSeconds(l.Start + shift + 2.5), Text = l.Text })],
        };

    // Each word 0.3 s long, 0.35 s apart, from a tenth of a second after the given time
    internal static List<TranscribedWord> Said(IEnumerable<(double Start, string Text)> lines, double confidence = 0.97, double step = 0.35)
        => [.. lines.SelectMany(l => l.Text.Split(' ').Select((w, i) => new TranscribedWord(w, l.Start + 0.1 + (step * i), l.Start + 0.1 + (step * i) + (step * 0.85), confidence)))];

    private static (double, string)[] With(int line, string text, (double Start, string Text)[]? from = null)
    {
        var copy = (from ?? Script).ToArray();
        copy[line] = (copy[line].Start, text);
        return copy;
    }

    private static DiscrepancyReport Find(SubtitleDocument file, IReadOnlyList<TranscribedWord> heard, double minConfidence = 0, Func<double, double>? toAudio = null)
        => DiscrepancyFinder.Find(file, heard, toAudio ?? (t => t), new DiscrepancyOptions { MinConfidence = minConfidence, Language = "en" });

    private static string[] Norms(string text) => [.. SpokenText.Tokens(text.Split(' '), "en", true).Select(t => t.Norm)];

    [Fact]
    public void Numbers_contractions_and_case_are_normalised_alike()
    {
        Assert.Equal(["i", "do", "not", "know"], Norms("I don't know."));
        Assert.Equal(["i", "do", "not", "know"], Norms("i do NOT know"));
        Assert.Equal(["we", "can", "not", "go"], Norms("We can't go"));
        Assert.Equal(["we", "can", "not", "go"], Norms("We cannot go"));
        Assert.Equal(["it", "is", "late"], Norms("It’s late"));
        Assert.Equal(["let", "us", "go"], Norms("Let's go"));
        Assert.Equal(["25", "dogs"], Norms("twenty-five dogs"));
        Assert.Equal(["25", "dogs"], Norms("25 dogs"));
        Assert.Equal(["105"], Norms("one hundred and five"));
        Assert.Equal(["1000", "people"], Norms("a thousand people"));
        Assert.Equal(["1000", "people"], Norms("1,000 people"));
        Assert.Equal(["in", "1995"], Norms("in nineteen ninety-five"));
        Assert.Equal(["2", "3"], Norms("two, three"));
        Assert.Equal(["a", "cat"], Norms("a cat"));
        Assert.Equal(["2007"], Norms("two thousand and seven"));
    }

    [Fact]
    public void Names_numbers_and_negations_are_marked()
    {
        var tokens = SpokenText.Tokens("Tell Katherine I never saw 5 of them.".Split(' '), "en", true);
        Assert.False(tokens[0].Name);
        Assert.True(tokens.Single(t => t.Norm == "katherine").Name);
        Assert.False(tokens.Single(t => t.Norm == "i").Name);
        Assert.True(tokens.Single(t => t.Norm == "never").Negation);
        Assert.True(tokens.Single(t => t.Norm == "5").Number);

        // After a sentence ends, a capital starts the next; all one case (or German nouns) marks nothing
        Assert.False(SpokenText.Tokens("Stop. Where are you?".Split(' '), "en", true)[1].Name);
        Assert.False(SpokenText.CapitalsMarkNames(["TELL", "KATHERINE"], "en"));
        Assert.False(SpokenText.CapitalsMarkNames(["Der", "Hund"], "de"));
    }

    [Fact]
    public void Sound_descriptions_music_and_labels_are_not_spoken()
    {
        Assert.Equal(["Hello", "there."], SpokenText.SubtitleWords("[door slams]\nJOHN: Hello there."));
        Assert.Empty(SpokenText.SubtitleWords("(laughs)"));
        Assert.Equal(["Yes."], SpokenText.SubtitleWords("<i>- Yes.</i>"));
        Assert.True(SpokenText.IsMusic("♪ la la la ♪"));
        Assert.False(SpokenText.IsMusic("Hello"));
    }

    [Fact]
    public void A_subtitle_that_says_what_is_heard_has_no_findings()
    {
        var report = Find(Subtitle(Script), Said(Script));
        Assert.Null(report.Problem);
        Assert.Empty(report.Findings);
        Assert.True(report.Shared > 0.95);
    }

    [Fact]
    public void Digits_and_words_and_contractions_are_not_differences()
    {
        var heard = Said(With(6, "I have 5 tickets here.", With(9, "Let us go before it rains.")));
        var report = Find(Subtitle(Script), heard);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void A_different_number_is_flagged_with_the_heard_number_as_the_fix()
    {
        var report = Find(Subtitle(Script), Said(With(6, "I have four tickets here.")));
        var f = Assert.Single(report.Findings);
        Assert.Equal(DiscrepancyFinder.Number, f.Kind);
        Assert.Equal(6, f.Cue);
        Assert.Equal("I have four tickets here.", f.Suggestion);
        Assert.Contains("four", f.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_different_name_is_flagged()
    {
        var report = Find(Subtitle(Script), Said(With(5, "Tell Margaret to wait outside.")));
        var f = Assert.Single(report.Findings);
        Assert.Equal(DiscrepancyFinder.Name, f.Kind);
        Assert.Equal("Tell Margaret to wait outside.", f.Suggestion);
    }

    [Fact]
    public void A_missing_negation_is_flagged()
    {
        var report = Find(Subtitle(Script), Said(With(7, "We can not make it in time.")));
        var f = Assert.Single(report.Findings);
        Assert.Equal(DiscrepancyFinder.Negation, f.Kind);
        Assert.Equal("We can not make it in time.", f.Suggestion);
    }

    [Fact]
    public void A_line_missing_most_of_what_is_heard_differs_in_words()
    {
        var heard = Said(Script.Where((_, i) => i != 4)).Concat(Said([(17, "Then we should hurry up because my brother refuses every single delay.")], step: 0.16)).OrderBy(w => w.Start).ToList();
        var report = Find(Subtitle(Script), heard);
        var f = Assert.Single(report.Findings);
        Assert.Equal(DiscrepancyFinder.Words, f.Kind);
        Assert.StartsWith("Then we should hurry up because", f.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void Speech_no_line_shows_is_a_missing_line_but_a_short_interjection_is_not()
    {
        var heard = Said([.. Script, (41, "Wait, where did my other shoe go now?"), (46, "Oh, well.")]);
        var report = Find(Subtitle(Script), heard);
        var f = Assert.Single(report.Findings);
        Assert.Equal(DiscrepancyFinder.MissingLine, f.Kind);
        Assert.Equal(-1, f.Cue);
        Assert.Equal("Wait, where did my other shoe go now?", f.Suggestion);
        Assert.Equal(41.1, f.AudioStart, 3);
        Assert.True(f.AudioEnd > f.AudioStart + 2);
    }

    [Fact]
    public void A_line_with_nothing_heard_is_extra_but_sound_descriptions_music_and_interjections_are_not()
    {
        var file = Subtitle([.. Script, (45, "This line was never spoken aloud."), (50, "[door slams]"), (55, "♪ la la la la ♪"), (60, "Hey!")]);
        var report = Find(file, Said(Script));
        var f = Assert.Single(report.Findings);
        Assert.Equal(DiscrepancyFinder.Extra, f.Kind);
        Assert.Equal(10, f.Cue);
        Assert.Null(f.Suggestion);
    }

    [Fact]
    public void Timing_off_within_the_tolerance_still_aligns()
    {
        var file = Subtitle(With(6, "I have five tickets here."), shift: 2.0);
        var report = Find(file, Said(With(6, "I have four tickets here.")));
        var f = Assert.Single(report.Findings);
        Assert.Equal(DiscrepancyFinder.Number, f.Kind);

        // Nothing is taken for missing or extra because of the shift
        Assert.Empty(Find(Subtitle(Script, shift: 2.0), Said(Script)).Findings);
    }

    [Fact]
    public void Timing_far_outside_the_tolerance_compares_nothing()
    {
        var report = Find(Subtitle(Script, shift: 12), Said(Script));
        Assert.NotNull(report.Problem);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void A_known_correction_moves_the_file_onto_the_audio_clock()
    {
        // The file runs 12 s early; the proposed correction moves it back
        var report = Find(Subtitle(With(6, "I have five tickets here."), shift: -12), Said(With(6, "I have four tickets here.")), toAudio: t => t + 12);
        Assert.Null(report.Problem);
        Assert.Equal(DiscrepancyFinder.Number, Assert.Single(report.Findings).Kind);
    }

    [Fact]
    public void Differences_heard_with_low_confidence_are_left_out()
    {
        var heard = Said(Script);
        heard = [.. heard.Select(w => w.Text == "five" ? w with { Text = "four", Confidence = 0.5 } : w)];
        var report = Find(Subtitle(Script), heard, minConfidence: 0.9);
        Assert.Empty(report.Findings);
        Assert.Equal(1, report.Suppressed);

        // Confident enough, it is flagged; a service that gives no confidence isn't second-guessed
        Assert.Single(Find(Subtitle(Script), heard, minConfidence: 0.4).Findings);
        Assert.Single(Find(Subtitle(Script), [.. heard.Select(w => w with { Confidence = null })], minConfidence: 0.9).Findings);
    }

    [Fact]
    public void A_number_heard_next_to_the_line_break_is_not_a_difference()
    {
        // The subtitle puts "five" at the start of the next line; the transcript hears it at the end of this one
        var file = Subtitle(With(7, "Five of us can make it.", With(6, "I have tickets here.")));
        var heard = Said(With(7, "of us can make it.", With(6, "I have tickets here five")));
        Assert.DoesNotContain(Find(file, heard).Findings, f => f.Kind == DiscrepancyFinder.Number);
    }

    [Fact]
    public void Samples_mark_the_words_behind_flagged_differences()
    {
        var report = Find(Subtitle(Script), Said(With(6, "I have four tickets here.")));
        Assert.Contains(report.Samples, s => s.Flagged);
        Assert.Single(report.Samples, s => s.Flagged);
        Assert.True(report.Samples.Count > 30);
    }

    [Fact]
    public void Too_many_lines_with_nothing_heard_blame_the_transcript()
    {
        var heard = Said(Script.Take(6));
        var report = Find(Subtitle(Script), heard);
        Assert.NotNull(report.Problem);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void A_film_length_subtitle_is_compared_line_by_line()
    {
        // Two thousand lines over about two and a half hours, one line changed
        var lines = Enumerable.Range(0, 2000).Select(i => (Start: 1 + (4.5 * i), Text: Script[i % Script.Length].Text)).ToArray();
        var heard = Said(With(1234, "I have four tickets here.", lines));
        var report = Find(Subtitle(With(1234, "I have five tickets here.", lines)), heard);
        var f = Assert.Single(report.Findings);
        Assert.Equal(1234, f.Cue);
        Assert.Equal(DiscrepancyFinder.Number, f.Kind);
    }

    [Fact]
    public void Words_are_replaced_in_place_keeping_markup()
    {
        Assert.Equal("<i>I have four\ntickets.</i>", DiscrepancyFinder.ReplaceWords("<i>I have five\ntickets.</i>", "five", "four"));
        Assert.Equal("It's 25 now", DiscrepancyFinder.ReplaceWords("It's twenty-five now", "twenty five", "25"));
        Assert.Null(DiscrepancyFinder.ReplaceWords("fivefold", "five", "four"));
    }
}
