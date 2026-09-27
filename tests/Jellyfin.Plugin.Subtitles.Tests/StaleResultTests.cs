using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.Subtitles.Api;
using Jellyfin.Plugin.Subtitles.Configuration;
using Jellyfin.Plugin.Subtitles.Pipeline;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// Results whose subtitle file or video was replaced or removed (a copy filed under a new name by another tool): actions
// answer 409 and clear them, the list hides them, library events drop them. Invented names and dialogue throughout.
public sealed class StaleResultTests : IDisposable
{
    private const string Original = "1\r\n00:00:01,000 --> 00:00:02,000\r\nHello there.\r\n\r\n2\r\n00:00:05,000 --> 00:00:06,000\r\nGoodbye now.\r\n\r\n";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "subs-stale-" + Guid.NewGuid().ToString("N"));
    private readonly ResultStore _store;
    private readonly SubtitleFiles _files;
    private readonly SubtitleProcessor _processor;

    public StaleResultTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new ResultStore(Path.Combine(_dir, "results.json"));
        _files = new SubtitleFiles(Path.Combine(_dir, "originals"));
        _processor = new SubtitleProcessor(_store, _files);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    public static TheoryData<string> Actions => ["Apply", "Decline", "Undo", "CheckAgain", "ApplyFinding", "DeclineFinding", "ApplyFindings", "DeclineFindings", "Edit", "SaveEdit", "Rerun", "CheckWholeFile"];

    [Theory]
    [MemberData(nameof(Actions))]
    public void Every_action_on_a_subtitle_file_that_is_gone_answers_with_the_reason_and_clears_the_result(string action)
    {
        var path = Waiting("Example Show S01E05 - Pilot.en.srt");
        File.Delete(path);

        var ex = Assert.Throws<StaleResultException>(() => Act(action, ResultStore.IdFor(path)));

        // An InvalidOperationException: every action answers it with 409 and the message
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
        Assert.Equal("This subtitle file no longer exists (replaced or removed) — the result has been cleared.", ex.Message);
        Assert.Null(_store.Get(ResultStore.IdFor(path)));
    }

    [Fact]
    public void Actions_on_results_whose_files_are_there_are_unchanged()
    {
        var path = Waiting("Film.en.srt");
        var r = _processor.Apply(ResultStore.IdFor(path), Policies());
        Assert.Equal(ResultStatus.Corrected, r.Status);
        Assert.Equal(ResultStatus.Undone, _processor.Undo(ResultStore.IdFor(path)).Status);
    }

    [Fact]
    public void A_search_that_found_nothing_has_no_file_and_is_left_alone()
    {
        var video = Path.Combine(_dir, "Invented Film (2020).mkv");
        File.WriteAllText(video, "video");
        var id = SubtitleFinder.IdFor(video, "eng");
        _store.Put(new SubtitleResult { Id = id, SubtitlePath = SubtitleFinder.PathFor(video, "eng", false, Formats.SubtitleFormat.Srt), Status = ResultStatus.NotFound, Time = DateTimeOffset.UtcNow });

        var ex = Assert.Throws<InvalidOperationException>(() => _processor.Apply(id, Policies()));
        Assert.IsNotType<StaleResultException>(ex);
        Assert.Throws<InvalidOperationException>(() => _processor.Decline(id));
        Assert.NotNull(_store.Get(id));
        Assert.Equal(0, _processor.PruneGone());
        Assert.Null(_processor.ClearIfStale(id));
        Assert.Equal(Staleness.None, _processor.StalenessOf(_store.Get(id)!));
    }

    [Theory]
    [InlineData(ResultStatus.Deferred)]
    [InlineData(ResultStatus.CantWrite)]
    [InlineData(ResultStatus.Failed)]
    [InlineData(ResultStatus.Undone)]
    public void Searches_that_added_no_file_stand_for_their_video(ResultStatus status)
    {
        var r = new SubtitleResult { Id = SubtitleFinder.IdFor("/x/Film.mkv", "eng"), SubtitlePath = "/x/Film.en.srt", Status = status };
        Assert.True(StaleResults.StandsForVideo(r));
        Assert.False(StaleResults.StandsForVideo(r with { Status = ResultStatus.Added }));
        Assert.False(StaleResults.StandsForVideo(new SubtitleResult { Id = ResultStore.IdFor("/x/Film.en.srt"), SubtitlePath = "/x/Film.en.srt", Status = status }));
    }

    [Fact]
    public void A_generated_subtitle_someone_deleted_stays_while_its_video_does_and_goes_with_it()
    {
        var video = Path.Combine(_dir, "Film.mkv");
        File.WriteAllText(video, "video");
        var path = Path.Combine(_dir, "Film.en.generated.srt");
        var id = SubtitleGenerator.IdPrefix + "abc";
        _store.Put(new SubtitleResult { Id = id, SubtitlePath = path, VideoPath = video, Status = ResultStatus.Generated, Changed = true, Fingerprint = "x", Time = DateTimeOffset.UtcNow });

        // No file: undoing it simply records it as undone
        Assert.Equal(ResultStatus.Undone, _processor.Undo(id).Status);

        File.Delete(video);
        var ex = Assert.Throws<StaleResultException>(() => _processor.CheckAgain(id));
        Assert.Equal(StaleResults.VideoGoneMessage, ex.Message);
        Assert.Null(_store.Get(id));
    }

    [Fact]
    public void A_file_whose_folder_is_missing_too_is_never_cleared()
    {
        var folder = Path.Combine(_dir, "offline");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "Film.en.srt");
        File.WriteAllText(path, Original);
        Put(path, ResultStatus.Proposed);
        Directory.Delete(folder, recursive: true);

        var ex = Assert.Throws<InvalidOperationException>(() => _processor.Apply(ResultStore.IdFor(path), Policies()));

        Assert.IsNotType<StaleResultException>(ex);
        Assert.Equal(StaleResults.UnreachableMessage, ex.Message);
        Assert.NotNull(_store.Get(ResultStore.IdFor(path)));
        Assert.Equal(0, _processor.PruneGone());
    }

    [Fact]
    public void A_file_that_vanishes_between_the_check_and_the_read_is_answered_not_thrown()
    {
        var path = Waiting("Film.en.srt");
        var calls = 0;

        // Seen at the check (the rule, then the action's own need), gone when read
        var processor = new SubtitleProcessor(_store, _files) { FileExists = p => p != path || calls++ < 2 };
        File.Delete(path);

        var ex = Assert.Throws<StaleResultException>(() => processor.Apply(ResultStore.IdFor(path), Policies()));

        Assert.Equal(StaleResults.SubtitleGoneMessage, ex.Message);
        Assert.Null(_store.Get(ResultStore.IdFor(path)));
    }

    [Fact]
    public void A_whole_file_check_of_a_video_that_is_gone_clears_the_result()
    {
        var path = Waiting("Film.en.srt");
        var video = Path.Combine(_dir, "Film.mkv");
        var job = new SubtitleJob(Guid.NewGuid(), "Film", video, path, "eng", TimeSpan.FromMinutes(25), 0);

        var ex = Assert.Throws<StaleResultException>(() => _processor.RequestWholeFileCheck(ResultStore.IdFor(path), _ => job, ["en"]));

        Assert.Equal(StaleResults.VideoGoneMessage, ex.Message);
        Assert.Null(_store.Get(ResultStore.IdFor(path)));
    }

    [Fact]
    public void Removing_a_video_from_the_library_drops_its_results_and_its_subtitles_results()
    {
        var item = Guid.NewGuid();
        var video = Path.Combine(_dir, "Example Show S01E05 - Pilot.mkv");
        var subtitle = Path.Combine(_dir, "Example Show S01E05 - Pilot.en.srt");
        var legacy = Path.Combine(_dir, "Example Show S01E05 - Pilot.fr.srt");
        var other = Path.Combine(_dir, "Example Show S01E06 - Next.en.srt");
        File.WriteAllText(subtitle, Original);
        File.WriteAllText(other, Original);
        _store.Put(new SubtitleResult { Id = ResultStore.IdFor(subtitle), ItemId = item, SubtitlePath = subtitle, Status = ResultStatus.Proposed, Offset = 1 });
        _store.Put(new SubtitleResult { Id = ResultStore.IdFor(legacy), SubtitlePath = legacy, Status = ResultStatus.InSync });
        _store.Put(new SubtitleResult { Id = SubtitleFinder.IdFor(video, "deu"), ItemId = item, SubtitlePath = Path.Combine(_dir, "Example Show S01E05 - Pilot.de.srt"), Status = ResultStatus.NotFound });
        _store.Put(new SubtitleResult { Id = ResultStore.IdFor(other), ItemId = Guid.NewGuid(), SubtitlePath = other, Status = ResultStatus.InSync });

        var dropped = _processor.DropForRemovedVideos([new RemovedVideo(item, video)], []);

        Assert.Equal(3, dropped);
        Assert.Null(_store.ForPath(subtitle));
        Assert.Null(_store.ForPath(legacy));
        Assert.Null(_store.Get(SubtitleFinder.IdFor(video, "deu")));
        Assert.NotNull(_store.ForPath(other));
    }

    [Fact]
    public void A_video_back_at_the_same_path_keeps_its_results()
    {
        var item = Guid.NewGuid();
        var video = Path.Combine(_dir, "Film.mkv");
        File.WriteAllText(video, "video");
        var subtitle = Waiting("Film.en.srt");
        _store.Put(_store.ForPath(subtitle)! with { ItemId = item });

        Assert.Equal(0, _processor.DropForRemovedVideos([new RemovedVideo(item, video)], []));
        Assert.NotNull(_store.ForPath(subtitle));
    }

    [Fact]
    public void A_replacement_filed_under_a_new_name_clears_the_stale_results_in_its_folder()
    {
        var season = Path.Combine(_dir, "Season 01");
        Directory.CreateDirectory(season);
        var old = Path.Combine(season, "Example Show S01E05 - Pilot.en.srt");
        var kept = Path.Combine(season, "Example Show S01E06 - Next.en.srt");
        File.WriteAllText(old, Original);
        File.WriteAllText(kept, Original);
        Put(old, ResultStatus.Proposed);
        Put(kept, ResultStatus.InSync);
        var planned = SubtitleFinder.IdFor(Path.Combine(season, "Example Show S01E07.mkv"), "eng");
        _store.Put(new SubtitleResult { Id = planned, SubtitlePath = Path.Combine(season, "Example Show S01E07.en.srt"), Status = ResultStatus.NotFound });

        // The old copy (and its subtitle) moved away; the new one filed as "... S01E05 - Pilot [1080p].mkv"
        File.Delete(old);
        var queue = new RemovedVideos();
        var now = DateTimeOffset.UtcNow;
        Assert.True(queue.Added(Path.Combine(season, "Example Show S01E05 - Pilot [1080p].mkv"), now));
        Assert.Null(queue.TakeIfDue(now + TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1)));
        var due = queue.TakeIfDue(now + TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1))!.Value;

        Assert.Equal(1, _processor.DropForRemovedVideos(due.Removed, due.Folders));
        Assert.Null(_store.ForPath(old));
        Assert.NotNull(_store.ForPath(kept));
        Assert.NotNull(_store.Get(planned));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void The_removal_queue_waits_for_a_quiet_delay_and_holds_each_video_once()
    {
        var queue = new RemovedVideos(max: 2);
        var now = DateTimeOffset.UtcNow;
        var item = Guid.NewGuid();
        Assert.True(queue.Removed(item, "/media/Film.mkv", now));
        Assert.True(queue.Removed(item, "/media/Film.mkv", now + TimeSpan.FromSeconds(10)));
        Assert.True(queue.Removed(Guid.NewGuid(), "/media/Other.mkv", now + TimeSpan.FromSeconds(20)));
        Assert.False(queue.Removed(Guid.NewGuid(), "/media/Third.mkv", now));
        Assert.False(queue.Removed(Guid.NewGuid(), null, now));
        Assert.Equal(now + TimeSpan.FromSeconds(80), queue.DueAt(TimeSpan.FromMinutes(1)));

        var due = queue.TakeIfDue(now + TimeSpan.FromSeconds(80), TimeSpan.FromMinutes(1))!.Value;

        Assert.Equal(["/media/Film.mkv", "/media/Other.mkv"], due.Removed.Select(v => v.Path).Order(StringComparer.Ordinal));
        Assert.Null(queue.DueAt(TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void The_list_hides_results_whose_video_or_subtitle_is_gone()
    {
        var there = new HashSet<string>(StringComparer.Ordinal) { "/media/Film.en.srt", "/media/Film.mkv", "/media/Kept.mkv" };
        bool Exists(string p) => there.Contains(p);
        var results = new List<SubtitleResult>
        {
            new() { Id = ResultStore.IdFor("/media/Film.en.srt"), SubtitlePath = "/media/Film.en.srt", Status = ResultStatus.InSync },
            new() { Id = ResultStore.IdFor("/media/Old.en.srt"), SubtitlePath = "/media/Old.en.srt", Status = ResultStatus.Proposed },
            new() { Id = SubtitleGenerator.IdPrefix + "1", SubtitlePath = "/media/Gone.en.srt", VideoPath = "/media/Gone.mkv", Status = ResultStatus.Generated },
            new() { Id = SubtitleGenerator.IdPrefix + "2", SubtitlePath = "/media/Kept.en.srt", VideoPath = "/media/Kept.mkv", Status = ResultStatus.NoSpeech },
            new() { Id = SubtitleFinder.IdFor("/media/Film.mkv", "deu"), SubtitlePath = "/media/Film.de.srt", Status = ResultStatus.NotFound },
            new() { Id = ResultStore.IdFor("/offline/Film.en.srt"), SubtitlePath = "/offline/Film.en.srt", Status = ResultStatus.InSync },
        };

        var shown = ResultQuery.Visible(results, Exists, folder => folder == "/media");

        Assert.Equal(
            [ResultStore.IdFor("/media/Film.en.srt"), SubtitleGenerator.IdPrefix + "2", SubtitleFinder.IdFor("/media/Film.mkv", "deu"), ResultStore.IdFor("/offline/Film.en.srt")],
            shown.Select(r => r.Id));
        var page = ResultQuery.Page(shown, null, null, 0, 15, r => ResultPresenter.Present(r, null, DateTimeOffset.UtcNow, TimeZoneInfo.Utc, false));
        Assert.Equal(4, page.Total);
        Assert.Equal(4, page.Tally.All);
    }

    [Fact]
    public void A_search_result_whose_video_the_library_says_is_gone_is_hidden_and_pruned()
    {
        var video = Path.Combine(_dir, "Film.mkv");
        var item = Guid.NewGuid();
        var id = SubtitleFinder.IdFor(video, "eng");
        _store.Put(new SubtitleResult { Id = id, ItemId = item, SubtitlePath = Path.Combine(_dir, "Film.en.srt"), Status = ResultStatus.NotFound });
        _processor.VideoLookup = r => r.ItemId == item ? video : null;

        Assert.Equal(Staleness.VideoGone, _processor.StalenessOf(_store.Get(id)!));
        Assert.Equal(1, _processor.PruneGone());
        Assert.Null(_store.Get(id));
    }

    private static Policies Policies() => new(ChangePolicy.Automatic, ChangePolicy.Review, new CleanupSettings());

    private void Act(string action, string id)
    {
        switch (action)
        {
            case "Apply": _processor.Apply(id, Policies()); break;
            case "Decline": _processor.Decline(id); break;
            case "Undo": _processor.Undo(id); break;
            case "CheckAgain": _processor.CheckAgain(id); break;
            case "ApplyFinding": _processor.ApplyFinding(id, 0, 1); break;
            case "DeclineFinding": _processor.DeclineFinding(id, 0, 1); break;
            case "ApplyFindings": _processor.ApplyFindings(id); break;
            case "DeclineFindings": _processor.DeclineFindings(id); break;
            case "Edit": _processor.LoadForEditing(id); break;
            case "SaveEdit": _processor.SaveEdited(id, "x", []); break;
            case "Rerun": _processor.RequestRerun(id); break;
            default: _processor.RequestWholeFileCheck(id, _ => null, ["en"]); break;
        }
    }

    // A subtitle with a correction, a suggested wording and a speech-to-text fallback waiting
    private string Waiting(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, Original);
        Put(path, ResultStatus.Proposed);
        return path;
    }

    private void Put(string path, ResultStatus status) => _store.Put(new SubtitleResult
    {
        Id = ResultStore.IdFor(path),
        SubtitlePath = path,
        Name = "Film",
        Status = status,
        Offset = 1,
        Confidence = 0.9,
        Fingerprint = SubtitleFiles.Fingerprint(Encoding.UTF8.GetBytes(Original)),
        Findings = [new LineFinding(1, "Hello there.", "Hi there.", "wrong", "Invented.")],
        SpeechFallback = new SpeechFallbackNote("deepgram", null, "Invented."),
        Time = DateTimeOffset.UtcNow,
    });
}
