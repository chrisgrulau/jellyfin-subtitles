using System;
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
