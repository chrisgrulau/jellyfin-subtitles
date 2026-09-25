using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Subtitles.Cleaning;
using Jellyfin.Plugin.Subtitles.Configuration;
using Jellyfin.Plugin.Subtitles.Formats;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// Invented dialogue throughout.
public class CleanerTests
{
    private static SubtitleCue C(double start, double end, string text)
        => new() { Start = TimeSpan.FromSeconds(start), End = TimeSpan.FromSeconds(end), Text = text };

    private static (SubtitleDocument Doc, IReadOnlyList<CleanChange> Changes) Clean(CleanOptions? options = null, params SubtitleCue[] cues)
        => SubtitleCleaner.Clean(new SubtitleDocument { Format = SubtitleFormat.Srt, Cues = cues }, options ?? new CleanOptions());

    [Fact]
    public void Site_adverts_are_removed_anywhere()
    {
        var (doc, changes) = Clean(null,
            C(1, 2, "Hello."), C(3, 4, "Hello again."), C(5, 6, "Hi."), C(10, 12, "Support us and become VIP member\nto remove all ads from www.OpenSubtitles.org"), C(12.5, 13, "Official movies site:\nYTS.BZ"),
            C(13, 14, "Anyway."), C(15, 16, "So."), C(17, 18, "Right."), C(19, 20, "Bye."));
        Assert.DoesNotContain(doc.Cues, c => c.Text.Contains("OpenSubtitles", StringComparison.Ordinal));
        Assert.Equal(2, changes.Count(c => c.Kind == CleanChangeKind.RemovedAdvert));
        Assert.Equal(7, doc.Cues.Count);
    }

    [Fact]
    public void Credit_wording_is_only_removed_near_the_edges()
    {
        var middle = Enumerable.Range(0, 10).Select(i => C(10 + i, 10.5 + i, "Line " + i)).ToList();
        middle.Insert(5, C(30, 32, "It was translated by my uncle."));
        var cues = new List<SubtitleCue> { C(0, 2, "Subtitles by Invented Team") };
        cues.AddRange(middle);
        cues.Add(C(90, 92, "Visit example.com for more"));

        var (doc, changes) = Clean(null, [.. cues]);
        Assert.Contains(doc.Cues, c => c.Text == "It was translated by my uncle.");
        Assert.Equal(2, changes.Count(c => c.Kind == CleanChangeKind.RemovedAdvert));
    }

    [Fact]
    public void Empty_and_duplicate_cues_are_removed_and_merged()
    {
        var (doc, changes) = Clean(null, C(1, 2, "<i></i>"), C(3, 4.5, "Stop!"), C(4.1, 5, "<i>Stop!</i>"), C(8, 9, "Stop!"));
        Assert.Equal(2, doc.Cues.Count);
        Assert.Equal(TimeSpan.FromSeconds(5), doc.Cues[0].End);
        Assert.Contains(changes, c => c.Kind == CleanChangeKind.RemovedEmpty);
        Assert.Contains(changes, c => c.Kind == CleanChangeKind.MergedDuplicate);
    }

    [Fact]
    public void Small_overlaps_are_fixed_but_deliberate_ones_are_left()
    {
        var (doc, _) = Clean(null, C(1, 3.3, "First."), C(3, 5, "Second."), C(10, 14, "Top line."), C(11, 13, "Bottom line."));
        Assert.Equal(TimeSpan.FromSeconds(3) - TimeSpan.FromMilliseconds(42), doc.Cues[0].End);
        Assert.Equal(TimeSpan.FromSeconds(14), doc.Cues[2].End);
    }

    [Fact]
    public void Short_interjections_are_left_alone()
        => Assert.Empty(Clean(null, C(1, 1.6, "Hey!"), C(3, 3.8, "Huh?")).Changes);

    [Fact]
    public void Flash_cues_are_lengthened_but_never_into_the_next()
    {
        var (doc, changes) = Clean(null, C(1, 1.1, "Quick."), C(5, 5.2, "Also quick."), C(5.5, 7, "Next."));
        Assert.Equal(TimeSpan.FromSeconds(1) + TimeSpan.FromMilliseconds(833), doc.Cues[0].End);
        Assert.Equal(TimeSpan.FromSeconds(5.5) - TimeSpan.FromMilliseconds(42), doc.Cues[1].End);
        Assert.Equal(2, changes.Count(c => c.Kind == CleanChangeKind.ExtendedShortCue));
    }

    [Fact]
    public void Hearing_impaired_text_is_only_stripped_when_asked()
    {
        var cue = C(1, 3, "[door slams]\n- ANA: Who's there?\n- (whispering) Nobody.");
        Assert.Empty(Clean(null, cue).Changes);

        var (doc, changes) = Clean(new CleanOptions { StripHearingImpaired = true }, cue, C(4, 6, "♪ ♪"), C(7, 9, "(sighs)"));
        Assert.Equal("- Who's there?\n- Nobody.", Assert.Single(doc.Cues).Text);
        Assert.Contains(changes, c => c.Kind == CleanChangeKind.StrippedHearingImpaired);
        Assert.Equal(2, changes.Count(c => c.Kind == CleanChangeKind.RemovedEmpty));
    }

    [Fact]
    public void A_clean_file_is_left_untouched()
    {
        var cues = new[] { C(1, 3, "Hello."), C(4, 6, "How are you?") };
        var (doc, changes) = Clean(null, cues);
        Assert.Empty(changes);
        Assert.Equal(cues, doc.Cues);
    }

    private const string Typeset =
        "[Script Info]\nScriptType: v4.00+\n\n[Events]\n" +
        "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n" +
        "Dialogue: 0,0:00:01.00,0:00:03.00,Main,,0,0,0,,First line\n" +
        "Dialogue: 0,0:00:02.50,0:00:04.00,Main,,0,0,0,,Second line runs in\n" +
        "Dialogue: 0,0:00:05.00,0:00:06.00,Main,,0,0,0,,Third\n" +
        "Dialogue: 0,0:00:07.00,0:00:07.20,Main,,0,0,0,,{\\pos(320,50)}EXIT\n" +
        "Dialogue: 0,0:00:08.00,0:00:08.30,Main,,0,0,0,,{\\k20}La{\\k10}la\n" +
        "Dialogue: 1,0:00:09.00,0:00:09.10,Main,,0,0,0,,On a layer\n" +
        "Dialogue: 0,0:00:10.00,0:00:10.10,Sign,,0,0,0,,Shop name\n" +
        "Dialogue: 0,0:00:11.00,0:00:11.10,Main,,0,0,0,,Hey!\n";

    [Fact]
    public void Ass_signs_karaoke_layers_and_other_styles_keep_their_timing()
    {
        var doc = SubtitleReader.Parse(Typeset, SubtitleFormat.Ass);

        var (cleaned, changes) = SubtitleCleaner.Clean(doc, new CleanOptions());

        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, c => c.Kind == CleanChangeKind.FixedOverlap && c.Before == "First line");
        Assert.Contains(changes, c => c.Kind == CleanChangeKind.ExtendedShortCue && c.Before == "Hey!");
        foreach (var text in new[] { "{\\pos(320,50)}EXIT", "{\\k20}La{\\k10}la", "On a layer", "Shop name" })
        {
            Assert.Equal(doc.Cues.Single(c => c.Text == text).End, cleaned.Cues.Single(c => c.Text == text).End);
        }
    }

    [Fact]
    public void Typeset_timing_can_be_fixed_on_request()
    {
        var doc = SubtitleReader.Parse(Typeset, SubtitleFormat.Ass);

        var (_, changes) = SubtitleCleaner.Clean(doc, new CleanOptions { FixTypesetTiming = true });

        Assert.Equal(5, changes.Count(c => c.Kind == CleanChangeKind.ExtendedShortCue));
    }

    [Fact]
    public void Timing_changes_record_the_old_and_new_end()
    {
        var (_, changes) = Clean(null, C(1, 3, "One."), C(2.5, 4, "Two."), C(10, 10.2, "Hey!"));

        var overlap = changes.Single(c => c.Kind == CleanChangeKind.FixedOverlap);
        Assert.Equal(TimeSpan.FromSeconds(3), overlap.EndBefore);
        Assert.Equal(TimeSpan.FromSeconds(2.5) - TimeSpan.FromMilliseconds(42), overlap.EndAfter);
        var flash = changes.Single(c => c.Kind == CleanChangeKind.ExtendedShortCue);
        Assert.Equal(TimeSpan.FromSeconds(10.2), flash.EndBefore);
        Assert.Equal(TimeSpan.FromSeconds(10) + TimeSpan.FromMilliseconds(833), flash.EndAfter);
    }

    [Fact]
    public void Repeated_lines_can_be_kept()
    {
        var (doc, changes) = Clean(new CleanOptions { MergeDuplicates = false }, C(1, 2, "Again."), C(1.5, 3, "Again."));

        Assert.Equal(2, doc.Cues.Count);
        Assert.Empty(changes);
        Assert.Equal(TimeSpan.FromSeconds(2), doc.Cues[0].End);
    }

    [Theory]
    [InlineData(CleanChangeKind.RemovedAdvert, ChangePolicy.Automatic)]
    [InlineData(CleanChangeKind.RemovedEmpty, ChangePolicy.Automatic)]
    [InlineData(CleanChangeKind.MergedDuplicate, ChangePolicy.Review)]
    [InlineData(CleanChangeKind.StrippedHearingImpaired, ChangePolicy.Review)]
    [InlineData(CleanChangeKind.FixedOverlap, ChangePolicy.Automatic)]
    [InlineData(CleanChangeKind.ExtendedShortCue, ChangePolicy.Automatic)]
    public void Wording_changes_follow_the_wording_policy_by_default(CleanChangeKind kind, ChangePolicy expected)
    {
        Assert.Equal(expected, CleanupPolicy.For(kind, new CleanupSettings(), textChanges: ChangePolicy.Review, timingFixes: ChangePolicy.Automatic));
    }

    [Fact]
    public void Advert_removal_can_be_held_for_review()
    {
        Assert.Equal(ChangePolicy.Review, CleanupPolicy.For(CleanChangeKind.RemovedAdvert, new CleanupSettings { AdvertRemoval = ChangePolicy.Review }, ChangePolicy.Automatic, ChangePolicy.Automatic));
    }

    [Fact]
    public void Settings_become_clean_options()
    {
        var defaults = CleanupPolicy.Options(new CleanupSettings());
        Assert.True(defaults.RemoveAdverts);
        Assert.False(defaults.StripHearingImpaired);
        Assert.True(defaults.MergeDuplicates);
        Assert.False(defaults.FixTypesetTiming);
        Assert.Equal(TimeSpan.FromMilliseconds(500), defaults.FlashThreshold);
        Assert.Equal(TimeSpan.FromMilliseconds(833), defaults.MinimumDuration);

        var odd = CleanupPolicy.Options(new CleanupSettings { FlashThresholdMs = 900, FlashTargetMs = 100 });
        Assert.Equal(odd.FlashThreshold, odd.MinimumDuration);
    }

    [Fact]
    public void A_chant_or_echo_is_not_a_duplicate()
    {
        var (doc, changes) = Clean(null, C(1, 2, "So say we all!"), C(2, 3, "So say we all!"), C(3.1, 4, "So say we all!"));

        Assert.Equal(3, doc.Cues.Count);
        Assert.DoesNotContain(changes, c => c.Kind == CleanChangeKind.MergedDuplicate);
    }
}
