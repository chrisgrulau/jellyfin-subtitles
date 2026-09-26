using System;
using Jellyfin.Plugin.Subtitles.Configuration;
using Jellyfin.Plugin.Subtitles.Pipeline;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// FEAT-03: the library picker. Every walk asks the scope whether a video's library is worked on.
public class LibraryScopeTests
{
    private static readonly string FilmsId = Guid.NewGuid().ToString();
    private static readonly string AnimeId = Guid.NewGuid().ToString("N");
    private static readonly string ShowsId = Guid.NewGuid().ToString();

    private static LibraryInfo[] Libraries() =>
    [
        new(FilmsId, "Films", ["/media/films"]),
        new(AnimeId, "Anime", ["/media/tv/anime/", "/other/anime"]),
        new(ShowsId, "Shows", ["/media/tv"]),
    ];

    [Fact]
    public void Every_library_is_worked_on_by_default()
    {
        var scope = new LibraryScope(Libraries(), null);
        Assert.All(scope.Libraries, l => Assert.True(scope.IsIncluded(l)));
        Assert.True(scope.Includes("/media/films/A (2001)/A.mkv"));
        Assert.True(scope.Includes("/media/tv/anime/B/S01E01.mkv"));
        Assert.True(LibraryScope.Everything.Includes("/anywhere/C.mkv"));
    }

    [Fact]
    public void A_library_left_out_is_skipped_whichever_of_its_folders_holds_the_video()
    {
        // Stored in another form of the same id: still matched
        var scope = new LibraryScope(Libraries(), [Guid.Parse(AnimeId).ToString("D").ToUpperInvariant()]);
        Assert.False(scope.Includes("/media/tv/anime/B/S01E01.mkv"));
        Assert.False(scope.Includes("/other/anime/C/S01E02.mkv"));
        Assert.True(scope.Includes("/media/tv/Drama/S01E01.mkv"));
        Assert.True(scope.Includes("/media/films/A (2001)/A.mkv"));
    }

    [Fact]
    public void The_deepest_folder_decides_and_a_folder_only_holds_what_is_below_it()
    {
        var scope = new LibraryScope(Libraries(), [ShowsId]);
        Assert.Equal("Anime", scope.LibraryOf("/media/tv/anime/B/S01E01.mkv")?.Name);
        Assert.Equal("Shows", scope.LibraryOf("/media/tv/animals/S01E01.mkv")?.Name);
        Assert.Null(scope.LibraryOf("/media/films2/A.mkv"));
        Assert.Null(scope.LibraryOf("/media/films"));
        Assert.True(scope.Includes("/media/tv/anime/B/S01E01.mkv"));
        Assert.False(scope.Includes("/media/tv/animals/S01E01.mkv"));
    }

    [Fact]
    public void A_video_in_no_known_library_and_unknown_ids_are_never_left_out()
    {
        var scope = new LibraryScope(Libraries(), ["not-an-id", string.Empty, Guid.NewGuid().ToString()]);
        Assert.True(scope.Includes("/elsewhere/A.mkv"));
        Assert.True(scope.Includes(null));
        Assert.All(scope.Libraries, l => Assert.True(scope.IsIncluded(l)));
    }

    [Theory]
    [InlineData("not-an-id", null)]
    [InlineData("00000000-0000-0000-0000-000000000000", null)]
    [InlineData(" 6F9619FF-8B86-D011-B42D-00C04FC964FF ", "6f9619ff8b86d011b42d00c04fc964ff")]
    [InlineData("6f9619ff8b86d011b42d00c04fc964ff", "6f9619ff8b86d011b42d00c04fc964ff")]
    public void Library_ids_are_compared_in_one_form(string id, string? expected)
        => Assert.Equal(expected, LibraryScope.NormaliseId(id));
}

// FEAT-06: with no languages of its own, the plugin uses each library's subtitle download languages, then the server's
// preferred metadata language, then English.
public class LibraryLanguageTests
{
    private static readonly LibraryInfo French = new(Guid.NewGuid().ToString(), "Films FR", ["/media/fr"]) { SubtitleLanguages = ["fre", "eng"] };
    private static readonly LibraryInfo Plain = new(Guid.NewGuid().ToString(), "Shows", ["/media/tv"]);

    [Fact]
    public void The_plugins_own_languages_win_everywhere()
    {
        var scope = new LibraryScope([French, Plain], null, ["German", "sv"], "fr");
        var fr = scope.LanguagesForPath("/media/fr/A.mkv");
        Assert.Equal(["deu", "swe"], fr.Codes);
        Assert.Equal(LanguageSource.Plugin, fr.Source);
        Assert.Equal(["deu", "swe"], scope.LanguagesForPath("/media/tv/B.mkv").Codes);
        Assert.Equal(["deu", "swe"], scope.LanguagesForPath("/elsewhere/C.mkv").Codes);
    }

    [Fact]
    public void Without_them_each_library_uses_its_own_then_the_server_then_english()
    {
        var scope = new LibraryScope([French, Plain], null, [], "de");
        var fr = scope.LanguagesForPath("/media/fr/A.mkv");
        Assert.Equal(["fre", "eng"], fr.Codes);
        Assert.Equal(LanguageSource.Library, fr.Source);

        var tv = scope.LanguagesForPath("/media/tv/B.mkv");
        Assert.Equal(["deu"], tv.Codes);
        Assert.Equal(LanguageSource.Server, tv.Source);

        var bare = new LibraryScope([Plain], null, null, null).LanguagesForPath("/media/tv/B.mkv");
        Assert.Equal(["eng"], bare.Codes);
        Assert.Equal(LanguageSource.Default, bare.Source);
    }

    [Fact]
    public void Settings_that_name_no_language_count_as_unset()
    {
        Assert.Equal(LanguageSource.Library, LanguageSettings.Choose(["Elvish", " "], ["fre"], null).Source);
        Assert.Equal(LanguageSource.Server, LanguageSettings.Choose(null, ["Klingon"], "English").Source);
        Assert.Equal(["eng"], LanguageSettings.Choose(null, null, "Elvish").Codes);
        Assert.Empty(LanguageSettings.Recognised(["Elvish", null]));
        Assert.Equal(["fre", "deu"], LanguageSettings.Recognised(["fre", "German", "FRE"]));
    }

    [Fact]
    public void All_languages_are_those_of_the_libraries_worked_on_and_the_fallback()
    {
        var scope = new LibraryScope([French, Plain], [Plain.Id], null, "ja");
        Assert.Equal(["fre", "eng", "jpn"], scope.AllLanguages());
    }
}
