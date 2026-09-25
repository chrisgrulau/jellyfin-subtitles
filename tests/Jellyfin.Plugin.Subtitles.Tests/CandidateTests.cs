using System;
using System.Linq;
using Jellyfin.Plugin.Subtitles.Candidates;
using Jellyfin.Plugin.Subtitles.Formats;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// Invented titles, groups and dialogue throughout.
public class CandidateTests
{
    private static readonly VideoFacts Episode = new()
    {
        FileName = "Harbour Lights S02E05 - The Crane.mkv",
        FolderName = "Season 02",
        Duration = TimeSpan.FromMinutes(44),
        FrameRate = 23.976,
        Season = 2,
        Episode = 5,
    };

    private static readonly VideoFacts Film = new()
    {
        FileName = "Rocket.Club.2019.1080p.BluRay.x264-KITE.mkv",
        Duration = TimeSpan.FromMinutes(110),
        FrameRate = 23.976,
    };

    private static SubtitleCandidate C(string release, Action<SubtitleCandidate>? _ = null) => new() { Source = "Test", Id = release, ReleaseName = release };

    [Theory]
    [InlineData("Show.Name.S01E02.1080p.AMZN.WEB-DL.DDP5.1.H.264-NTB", ReleaseSource.Web, "AMZN", 1080, "NTB", 1, 2, null)]
    [InlineData("Movie.Title.2019.Directors.Cut.2160p.UHD.BluRay.x265-GRP", ReleaseSource.BluRay, null, 2160, "GRP", null, null, "Director's Cut")]
    [InlineData("show_name_s03e10e11_hdtv_x264-lol", ReleaseSource.Hdtv, null, null, "LOL", 3, 10, null)]
    [InlineData("Movie Title (1999) Extended Edition DVDRip", ReleaseSource.Dvd, null, null, null, null, null, "Extended")]
    [InlineData("Movie.Title.2019.1080p.WEBRip.x264-DDP5", ReleaseSource.Web, null, 1080, null, null, null, null)]
    public void Release_names_are_read(string name, ReleaseSource source, string? service, int? res, string? group, int? season, int? episode, string? edition)
    {
        var t = ReleaseTags.Parse(name);
        Assert.Equal(source, t.Source);
        Assert.Equal(service, t.Service);
        Assert.Equal(res, t.Resolution);
        Assert.Equal(group, t.Group);
        Assert.Equal(season, t.Season);
        Assert.Equal(episode, t.Episode);
        Assert.Equal(edition, t.Edition);
    }

    [Fact]
    public void Multi_episode_codes_and_file_extensions_are_handled()
    {
        var t = ReleaseTags.Parse("Show.S01E01-E02.720p.WEB.mkv");
        Assert.Equal((1, 1, 2), (t.Season!.Value, t.Episode!.Value, t.EndingEpisode!.Value));
        Assert.Null(ReleaseTags.Parse("Show.S01E01.1080p.WEB.mkv").EndingEpisode);
    }

    [Fact]
    public void Title_words_that_look_like_tags_are_not_mistaken_for_them()
    {
        var t = ReleaseTags.Parse("DC.League.of.Pets.2022.1080p.WEB.mkv");
        Assert.Null(t.Edition);
        Assert.Null(t.Service);
    }

    [Fact]
    public void A_fingerprint_match_beats_everything_else()
    {
        var ranked = CandidateScorer.Rank(Film,
        [
            C("Rocket.Club.2019.1080p.BluRay.x264-KITE") with { DownloadCount = 90000 },
            C("Rocket.Club.2019.720p.WEB-DL") with { IsHashMatch = true },
        ]);
        Assert.True(ranked[0].Candidate.IsHashMatch);
    }

    [Fact]
    public void Same_release_group_and_source_score_higher()
    {
        var same = CandidateScorer.Score(Film, C("Rocket.Club.2019.1080p.BluRay.x264-KITE"));
        var other = CandidateScorer.Score(Film, C("Rocket.Club.2019.1080p.WEB-DL.x264-NOPE"));
        Assert.True(same.Score > other.Score);
        Assert.Contains(same.Reasons, r => r.Contains("same release group", StringComparison.Ordinal));
        Assert.Contains(other.Reasons, r => r.Contains("different kind of source", StringComparison.Ordinal));
    }

    [Fact]
    public void The_wrong_episode_is_rejected_and_the_right_one_is_not()
    {
        Assert.True(CandidateScorer.Score(Episode, C("Harbour.Lights.S02E06.WEB")).Rejected);
        Assert.True(CandidateScorer.Score(Episode, C("Harbour.Lights.S01E05.WEB")).Rejected);
        Assert.False(CandidateScorer.Score(Episode, C("Harbour.Lights.S02E05.WEB")).Rejected);
        Assert.False(CandidateScorer.Score(Episode, C("Harbour.Lights.S02E04E05.WEB")).Rejected);
    }

    [Fact]
    public void A_different_cut_is_penalised_heavily()
    {
        var extended = CandidateScorer.Score(Film, C("Rocket.Club.2019.Extended.1080p.BluRay-KITE"));
        var plain = CandidateScorer.Score(Film, C("Rocket.Club.2019.1080p.BluRay-KITE"));
        Assert.True(plain.Score - extended.Score >= 0.3 - 1e-9);
    }

    [Fact]
    public void Frame_rate_translation_and_forced_are_taken_into_account()
    {
        var baseline = CandidateScorer.Score(Film, C("x")).Score;
        Assert.True(CandidateScorer.Score(Film, C("x") with { FrameRate = 25 }).Score < baseline);
        Assert.True(CandidateScorer.Score(Film, C("x") with { MachineTranslated = true }).Score < baseline);
        Assert.True(CandidateScorer.Score(Film, C("x") with { Forced = true }).Rejected);
        Assert.True(CandidateScorer.Score(Film, C("x") with { Format = "sup" }).Rejected);
        Assert.False(CandidateScorer.Score(Film, C("x") with { FrameRate = 23.976 }).Rejected);
    }

    [Fact]
    public void Hearing_impaired_preference_only_matters_when_set()
    {
        var sdh = C("x") with { HearingImpaired = true };
        Assert.Equal(CandidateScorer.Neutral, CandidateScorer.Score(Film, sdh).Score, 6);
        Assert.True(CandidateScorer.Score(Film, sdh, HearingImpairedPreference.Prefer).Score > CandidateScorer.Score(Film, sdh, HearingImpairedPreference.Avoid).Score);
    }

    [Fact]
    public void Rejected_candidates_rank_last()
    {
        var ranked = CandidateScorer.Rank(Episode, [C("Harbour.Lights.S02E06.BluRay-KITE") with { DownloadCount = 999999 }, C("Harbour.Lights.S02E05")]);
        Assert.False(ranked[0].Rejected);
        Assert.True(ranked[^1].Rejected);
    }

    private const string English = "I don't know what you want from me. You said the boat was in the harbour, and it was not there. We have to go back and look again, because if we don't find it tonight, it is gone. Do you understand me? This is not a game, and I am not going to wait for you all night.";

    private const string French = "Je ne sais pas ce que tu veux de moi. Tu as dit que le bateau était dans le port, et il n'était pas là. Nous devons y retourner et chercher encore, parce que si on ne le trouve pas ce soir, c'est fini. Est-ce que tu me comprends? Ce n'est pas un jeu, et je ne vais pas t'attendre toute la nuit.";

    [Fact]
    public void Language_is_guessed_from_common_words()
    {
        Assert.Equal("eng", LanguageGuesser.Guess(English)?.Language);
        Assert.Equal("fre", LanguageGuesser.Guess(French)?.Language);
        Assert.Null(LanguageGuesser.Guess("Too short to tell."));
    }

    private static SubtitleDocument Doc(string text, TimeSpan lastEnd, int cues)
        => new()
        {
            Format = SubtitleFormat.Srt,
            Cues = [.. Enumerable.Range(0, cues).Select(i => new SubtitleCue
            {
                Start = lastEnd * i / cues,
                End = (lastEnd * (i + 1) / cues) - TimeSpan.FromMilliseconds(100),
                Text = text,
            })],
        };

    [Fact]
    public void Content_that_covers_the_running_time_in_the_right_language_passes()
    {
        var a = ContentChecks.Assess(Film, Doc(English, TimeSpan.FromMinutes(105), 900), "eng");
        Assert.False(a.Rejected);
        Assert.True(a.Adjustment > 0);
    }

    [Fact]
    public void Content_in_the_wrong_language_or_for_a_longer_cut_is_rejected()
    {
        Assert.True(ContentChecks.Assess(Film, Doc(French, TimeSpan.FromMinutes(105), 900), "eng").Rejected);
        Assert.True(ContentChecks.Assess(Film, Doc(English, TimeSpan.FromMinutes(150), 900), "eng").Rejected);
        Assert.True(ContentChecks.Assess(Film, Doc(English, TimeSpan.FromMinutes(20), 900), "eng").Rejected);
    }

    [Fact]
    public void Sparse_subtitles_are_marked_down()
    {
        var a = ContentChecks.Assess(Film, Doc(English, TimeSpan.FromMinutes(100), 40), "eng");
        Assert.True(a.Adjustment < 0);
        Assert.Contains(a.Reasons, r => r.StartsWith("sparse", StringComparison.Ordinal));
    }
}
