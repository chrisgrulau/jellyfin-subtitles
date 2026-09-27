using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Subtitles.Api;
using Jellyfin.Plugin.Subtitles.Pipeline;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// The results list as the settings page shows it. Invented titles throughout.
public class ResultPresenterTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 15, 0, 0, TimeSpan.Zero);

    private static SubtitleResult R(ResultStatus status, string subtitle = "/media/Invented Film (2019)/Invented Film (2019).en.srt", string explanation = "Why.")
        => new() { Id = "0123456789abcdef", SubtitlePath = subtitle, Name = "Invented Film", Status = status, Explanation = explanation, Time = Now.AddHours(-3) };

    [Theory]
    [InlineData(0.5, "just now")]
    [InlineData(60, "a minute ago")]
    [InlineData(5 * 60, "5 minutes ago")]
    [InlineData(59 * 60, "59 minutes ago")]
    [InlineData(3600, "an hour ago")]
    [InlineData(3 * 3600, "3 hours ago")]
    [InlineData(20 * 3600, "20 hours ago")]
    [InlineData(30 * 3600, "yesterday")]
    [InlineData(2 * 86400, "2 days ago")]
    [InlineData(6 * 86400, "6 days ago")]
    [InlineData(14 * 86400, "12 Sep")]
    [InlineData(400 * 86400, "22 Aug 2025")]
    public void Times_are_relative_then_short_dates(double secondsAgo, string expected)
        => Assert.Equal(expected, RelativeTime.Format(Now.AddSeconds(-secondsAgo), Now, TimeZoneInfo.Utc));

    [Fact]
    public void Yesterday_follows_the_local_calendar()
    {
        // 15:00 UTC is 01:00 the next day at UTC+10: 26 hours earlier is 23:00 two evenings before there
        var zone = TimeZoneInfo.CreateCustomTimeZone("Plus10", TimeSpan.FromHours(10), "Plus10", "Plus10");
        Assert.Equal("2 days ago", RelativeTime.Format(Now.AddHours(-26), Now, zone));
        Assert.Equal("yesterday", RelativeTime.Format(Now.AddHours(-26), Now, TimeZoneInfo.Utc));
    }

    [Theory]
    [InlineData("/tv/Example Show/Season 1/Example Show - S01E05 - The Pilot.mkv", "Example Show", 1, 5, "The Pilot")]
    [InlineData("/tv/Example.Show.S02E10.Another.Title.720p.WEB-DL.x264.mkv", "Example Show", 2, 10, "Another Title")]
    [InlineData("/tv/Example Show/Example.Show.1x05.HDTV.mkv", "Example Show", 1, 5, null)]
    [InlineData("/tv/Example Show/Season 03/S03E07.mkv", "Example Show", 3, 7, null)]
    [InlineData("/tv/Example Show/Season 1/Example Show S01E05.en.forced.srt", "Example Show", 1, 5, null)]
    public void Episodes_are_read_from_file_names(string path, string series, int season, int episode, string? title)
    {
        var id = ResultPresenter.FromFileName(path);
        Assert.True(id.IsEpisode);
        Assert.Equal(series, id.SeriesName);
        Assert.Equal(season, id.Season);
        Assert.Equal(episode, id.Episode);
        Assert.Equal(title, id.EpisodeTitle);
    }

    [Theory]
    [InlineData("/films/Invented Film (2019)/Invented Film (2019).mkv", "Invented Film", 2019)]
    [InlineData("/films/Invented.Film.2019.1080p.BluRay.x264.mkv", "Invented Film", 2019)]
    [InlineData("/films/Invented Film.en.srt", "Invented Film", null)]
    public void Films_are_read_from_file_names(string path, string title, int? year)
    {
        var id = ResultPresenter.FromFileName(path);
        Assert.False(id.IsEpisode);
        Assert.Equal(title, id.Title);
        Assert.Equal(year, id.Year);
    }

    [Fact]
    public void Headlines_are_short_identities_from_the_library_or_the_file_name()
    {
        var episode = R(ResultStatus.InSync, "/tv/Example Show/Season 1/Example Show S01E05.en.srt") with { Name = "The Pilot" };
        Assert.Equal(("Example Show S01E05", "The Pilot"), ResultPresenter.Headline(episode, null));
        Assert.Equal(("Example Show S01E05", "Its Real Title"), ResultPresenter.Headline(episode, new VideoIdentity("Example Show", 1, 5, "Its Real Title", null, null)));
        Assert.Equal(("Library Name S02E11", null), ResultPresenter.Headline(episode, new VideoIdentity("Library Name", 2, 11, null, null, null)));

        var film = R(ResultStatus.InSync);
        Assert.Equal(("Invented Film (2019)", null), ResultPresenter.Headline(film, null));
        Assert.Equal(("Its Library Title (2020)", null), ResultPresenter.Headline(film, new VideoIdentity(null, null, null, null, "Its Library Title", 2020)));

        // A generated subtitle's result names its video; the subtitle's language is a small tag
        var generated = R(ResultStatus.Generated, "/tv/Example Show/Example Show S01E06.en.generated.srt") with { VideoPath = "/tv/Example Show/Example Show S01E06.mkv", Name = "Second" };
        Assert.Equal(("Example Show S01E06", "Second"), ResultPresenter.Headline(generated, null));
        Assert.Equal("EN", ResultPresenter.LanguageOf(generated.SubtitlePath));
        Assert.Null(ResultPresenter.LanguageOf("/films/Invented Film (2019).srt"));
    }

    [Fact]
    public void Changes_are_icon_chips_with_counts_and_words()
    {
        var r = R(ResultStatus.Corrected) with
        {
            Offset = 1.23,
            Cleaned = new Dictionary<string, int> { ["StrippedHearingImpaired"] = 4, ["RemovedAdvert"] = 1, ["RemovedEmpty"] = 2, ["FixedOverlap"] = 3 },
            CleanupPending = new Dictionary<string, int> { ["MergedDuplicate"] = 1 },
            Findings = [new LineFinding(12, "Line", "Better line", "name", "A name differs.")],
        };

        var chips = ResultPresenter.Chips(r);

        var timing = chips.Single(c => c.Kind == "timing");
        Assert.Equal("⏱", timing.Icon);
        Assert.Equal("+1.2 s", timing.Label);
        Assert.Equal("Shifted 1.23 s later", timing.Tooltip);
        Assert.False(timing.Pending);
        Assert.Equal(("🔈", "4", "4 sound descriptions removed"), chips.Where(c => c.Kind == "sounds").Select(c => (c.Icon, c.Label, c.Tooltip)).Single());
        var removed = chips.Where(c => c.Kind == "removed").ToList();
        Assert.Equal("1 advert or credit line, 2 empty lines removed", removed[0].Tooltip);
        Assert.Equal("3", removed[0].Label);
        Assert.True(removed[1].Pending);
        Assert.Equal("1 repeated line removed — waiting for review", removed[1].Tooltip);
        Assert.Equal("3", chips.Single(c => c.Kind == "tidied").Label);
        var wording = chips.Single(c => c.Kind == "wording");
        Assert.True(wording.Pending);
        Assert.Equal("💬", wording.Icon);
        Assert.All(chips, c => Assert.False(string.IsNullOrWhiteSpace(c.Tooltip)));
        Assert.All(chips, c => Assert.Equal(ResultPresenter.Icons[c.Kind], c.Icon));
    }

    [Fact]
    public void Whole_file_findings_encoding_and_queued_work_have_chips()
    {
        var r = R(ResultStatus.InSync, explanation: "Already in sync. The text didn't decode cleanly as windows-1252, so any change waits for review.") with
        {
            Findings =
            [
                new LineFinding(10, string.Empty, "Heard line", "missing-line", "Heard.") { From = "whole file" },
                new LineFinding(20, "Nothing", null, "extra", "Nothing heard.") { From = "whole file" },
            ],
            WholeFileRequested = true,
            RerunWith = "deepgram",
        };

        var kinds = ResultPresenter.Chips(r).Select(c => c.Kind + ":" + c.Pending).ToList();

        Assert.Contains("added:True", kinds);
        Assert.Contains("removed:True", kinds);
        Assert.Contains("encoding:True", kinds);
        Assert.Equal(2, kinds.Count(k => k == "queued:True"));
        Assert.Contains(ResultPresenter.Chips(r), c => c.Tooltip == "Queued to run again with Deepgram on the next check");
    }

    [Fact]
    public void Details_are_one_friendly_sentence()
    {
        Assert.Equal("In sync — no timing change needed", ResultPresenter.Summary(R(ResultStatus.InSync)));
        Assert.Equal("Shifted 1.2 s later to match the speech", ResultPresenter.Summary(R(ResultStatus.Corrected) with { Offset = 1.2 }));
        Assert.Equal("Shifted 0.8 s earlier to match the speech", ResultPresenter.Summary(R(ResultStatus.Corrected) with { Offset = -0.8 }));
        Assert.Equal("Speed corrected for a frame-rate difference and shifted 2.5 s later to match the speech", ResultPresenter.Summary(R(ResultStatus.Corrected) with { Offset = 2.5, Scale = 1.04271 }));
        Assert.Equal("Could be shifted 1.2 s later — waiting for your review", ResultPresenter.Summary(R(ResultStatus.Proposed) with { Offset = 1.2 }));
        Assert.Equal("Nothing fitting was found", ResultPresenter.Summary(R(ResultStatus.NotFound)));
        Assert.Equal("Generated from a transcript (1,037 lines)", ResultPresenter.Summary(R(ResultStatus.Generated, explanation: "No subtitle was found, so one was generated from a full transcript by builtin/base: 1037 lines from 8210 words, in 5 parts. Machine-generated.")));
        Assert.Equal("Added from OpenSubtitles, already in sync", ResultPresenter.Summary(R(ResultStatus.Added) with { Origin = "OpenSubtitles: Invented.Film.2019.WEB (score 0.91)" }));
        Assert.Equal("Couldn't be checked: not a readable text subtitle", ResultPresenter.Summary(R(ResultStatus.Failed, explanation: "Not a readable text subtitle.")));
        Assert.Equal("In sync — no timing change needed — 2 lines to review", ResultPresenter.Summary(R(ResultStatus.InSync) with
        {
            Findings = [new LineFinding(1, "A", "B", "name", "x"), new LineFinding(2, "C", "D", "number", "y")],
        }));
    }

    [Fact]
    public void The_statistics_move_to_nerd_stats()
    {
        var r = R(ResultStatus.InSync, explanation: "Already in sync (+0.02 s; 5 of 6 stretches agree, margin 0.041, z 7.2). Already in sync (+0.01 s; 34 of 40 matched words agree).") with
        {
            Stage = "speech-to-text",
            Confidence = 0.93,
            SpeechSetup = "local",
            SpeechFallback = new SpeechFallbackNote("deepgram", "local", "Deepgram couldn't be reached; used the local service instead."),
            Cleaned = new Dictionary<string, int> { ["RemovedEmpty"] = 2 },
        };

        var stats = ResultPresenter.NerdStats(r, TimeZoneInfo.Utc).ToDictionary(s => s.Name, s => s.Value);

        Assert.Equal("26 Sep 2026, 12:00", stats["Checked"]);
        Assert.Equal("speech-to-text", stats["Decided by"]);
        Assert.Equal("0.93", stats["Confidence"]);
        Assert.Equal("5 of 6", stats["Stretches agreeing"]);
        Assert.Equal("0.041", stats["Margin"]);
        Assert.Equal("7.2", stats["z"]);
        Assert.Equal("34 of 40", stats["Matched words agreeing"]);
        Assert.Equal("the local service", stats["Speech-to-text"]);
        Assert.Equal("Deepgram couldn't be reached; used the local service instead.", stats["Fell back"]);
        Assert.Equal("RemovedEmpty 2", stats["Changes made"]);
        Assert.Equal(r.SubtitlePath, stats["Subtitle file"]);
        Assert.Equal(r.Explanation, stats["Explanation"]);
        Assert.DoesNotContain("Shift", stats.Keys);

        var generated = ResultPresenter.NerdStats(R(ResultStatus.Generated, explanation: "…: 1037 lines from 8210 words, in 5 parts."), TimeZoneInfo.Utc).ToDictionary(s => s.Name, s => s.Value);
        Assert.Equal(("1,037", "8,210", "5"), (generated["Lines"], generated["Words"], generated["Parts"]));
    }

    [Fact]
    public void A_view_carries_the_notice_and_the_rerun_button()
    {
        var r = R(ResultStatus.InSync) with { SpeechFallback = new SpeechFallbackNote("deepgram", "local", "Deepgram couldn't be reached; used the local service instead.") };

        var view = ResultPresenter.Present(r, null, Now, TimeZoneInfo.Utc, canRerun: true);

        Assert.Equal("Invented Film (2019)", view.Headline);
        Assert.Equal("EN", view.Language);
        Assert.Equal("In sync", view.StatusText);
        Assert.Equal("3 hours ago", view.When);
        Assert.Equal("Deepgram couldn't be reached; used the local service instead.", view.Notice);
        Assert.True(view.CanRerun);
        Assert.Equal("Rerun with Deepgram", view.RerunLabel);
        Assert.False(view.RerunQueued);
        Assert.True(ResultPresenter.Present(r with { RerunWith = "deepgram" }, null, Now, TimeZoneInfo.Utc, canRerun: true).RerunQueued);
        Assert.Null(ResultPresenter.Present(R(ResultStatus.InSync), null, Now, TimeZoneInfo.Utc, canRerun: false).RerunLabel);
    }

    [Fact]
    public void Results_are_filtered_searched_and_paged_on_the_server()
    {
        var all = Enumerable.Range(0, 40).Select(i => R(i % 4 == 0 ? ResultStatus.Corrected : ResultStatus.InSync, $"/tv/Example Show/Example Show S01E{i + 1:00}.en.srt") with { Id = "id" + i, Name = "Episode " + i }).ToList();
        all[3] = all[3] with { Status = ResultStatus.Proposed };
        all[5] = all[5] with { SpeechFallback = new SpeechFallbackNote("deepgram", null, "x") };
        ResultView Present(SubtitleResult r) => ResultPresenter.Present(r, null, Now, TimeZoneInfo.Utc, false);

        var first = ResultQuery.Page(all, null, null, 0, 15, Present);
        Assert.Equal(15, first.Items.Count);
        Assert.Equal(40, first.Total);
        Assert.Equal(15, first.Next);
        Assert.Equal("Example Show S01E01", first.Items[0].View.Headline);
        var last = ResultQuery.Page(all, null, null, 30, 15, Present);
        Assert.Equal(10, last.Items.Count);
        Assert.Null(last.Next);

        var corrected = ResultQuery.Page(all, "Corrected", null, 0, 5, Present);
        Assert.Equal(10, corrected.Total);
        Assert.Equal(5, corrected.Next);
        Assert.All(corrected.Items, row => Assert.Equal(ResultStatus.Corrected, row.Result.Status));
        Assert.Equal(1, ResultQuery.Page(all, "waiting", null, 0, 15, Present).Total);
        Assert.Equal("id5", ResultQuery.Page(all, "fellback", null, 0, 15, Present).Items.Single().Result.Id);
        Assert.Equal("id12", ResultQuery.Page(all, "Corrected", "s01e13", 0, 15, Present).Items.Single().Result.Id);
        Assert.Equal(0, ResultQuery.Page(all, "Corrected", "S01E14", 0, 15, Present).Total);
        Assert.Equal(1, ResultQuery.Page(all, null, "EPISODE 7", 0, 15, Present).Total);

        var tally = first.Tally;
        Assert.Equal(40, tally.All);
        Assert.Equal(10, tally.ByStatus["Corrected"]);
        Assert.Equal(1, tally.Waiting);
        Assert.Equal(1, tally.FellBack);
        Assert.Equal(100, ResultQuery.Page(Enumerable.Repeat(all[0], 500).ToList(), null, null, 0, 1000, Present).Items.Count);
    }

    [Fact]
    public void The_identity_cache_asks_the_library_once_for_a_while()
    {
        var asked = 0;
        var cache = new VideoIdentityCache();
        VideoIdentity? LookUp()
        {
            asked++;
            return new VideoIdentity("Example Show", 1, 2, null, null, null);
        }

        Assert.Equal("Example Show", cache.Get("item", LookUp)!.SeriesName);
        Assert.Equal("Example Show", cache.Get("item", LookUp)!.SeriesName);
        Assert.Null(cache.Get("gone", () => { asked++; return null; }));
        Assert.Null(cache.Get("gone", () => { asked++; return null; }));
        Assert.Equal(2, asked);
    }
}
