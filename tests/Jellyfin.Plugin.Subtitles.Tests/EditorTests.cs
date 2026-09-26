using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.Pipeline;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// Editing subtitle lines by hand. Invented text throughout.
public sealed class EditorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "subs-editor-" + Guid.NewGuid().ToString("N"));

    public EditorTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static SubtitleDocument Three() => new()
    {
        Format = SubtitleFormat.Srt,
        Cues =
        [
            new SubtitleCue { Start = TimeSpan.FromSeconds(1), End = TimeSpan.FromSeconds(2), Text = "One" },
            new SubtitleCue { Start = TimeSpan.FromSeconds(3), End = TimeSpan.FromSeconds(4), Text = "Two" },
            new SubtitleCue { Start = TimeSpan.FromSeconds(5), End = TimeSpan.FromSeconds(6), Text = "Three" },
        ],
    };

    [Fact]
    public void Unchanged_lines_count_as_nothing_and_edits_additions_and_deletions_count()
    {
        var doc = Three();
        var lines = SubtitleEditing.ToEditor(doc);

        Assert.Equal(0, SubtitleEditing.Apply(doc, lines).Changed);

        var edited = new[] { lines[0] with { Text = "Uno" }, lines[2], new EditorCue(-1, 4.5, 4.9, "Added") };
        var (result, changed, problem) = SubtitleEditing.Apply(doc, edited);

        Assert.Null(problem);
        Assert.Equal(3, changed);
        Assert.Equal(["Uno", "Added", "Three"], result!.Cues.Select(c => c.Text));
    }

    [Theory]
    [InlineData(2, 1, "x")]
    [InlineData(-1, 1, "x")]
    [InlineData(1, 2, "")]
    public void Impossible_times_and_empty_lines_are_refused(double start, double end, string text)
    {
        var (document, _, problem) = SubtitleEditing.Apply(Three(), [new EditorCue(0, start, end, text)]);
        Assert.Null(document);
        Assert.NotNull(problem);
    }

    [Fact]
    public void Ass_lines_keep_their_fields_and_new_lines_take_the_first_style()
    {
        var fields = new[] { "0", "Top", "Speaker" };
        var doc = Three() with { Format = SubtitleFormat.Ass, Cues = [.. Three().Cues.Select(c => c with { AssFields = fields })] };
        var lines = SubtitleEditing.ToEditor(doc);

        var (result, _, _) = SubtitleEditing.Apply(doc, [lines[0] with { Text = "Changed" }, new EditorCue(-1, 9, 10, "New")]);

        Assert.All(result!.Cues, c => Assert.Equal(fields, c.AssFields));
    }

    [Fact]
    public void Saving_keeps_the_original_for_undo_and_refuses_a_file_changed_since()
    {
        var path = Path.Combine(_dir, "Film.en.srt");
        var original = SubtitleWriter.ToBytes(Three());
        File.WriteAllBytes(path, original);
        var store = new ResultStore(Path.Combine(_dir, "results.json"));
        var processor = new SubtitleProcessor(store, new SubtitleFiles(Path.Combine(_dir, "originals")));
        store.Put(new SubtitleResult { Id = ResultStore.IdFor(path), SubtitlePath = path, Name = "Invented Film", Status = ResultStatus.InSync, Fingerprint = SubtitleFiles.Fingerprint(original), Version = SubtitleProcessor.CurrentVersion });

        var view = processor.LoadForEditing(ResultStore.IdFor(path))!;
        Assert.Equal(3, view.Cues.Count);
        Assert.Equal("Film.en.srt", view.File);

        var saved = processor.SaveEdited(view.Id, view.Fingerprint, [view.Cues[0] with { Text = "Uno" }, view.Cues[1], view.Cues[2]]);
        Assert.True(saved.Changed);
        Assert.Equal(1, saved.Cleaned[SubtitleEditing.EditedKind]);
        Assert.Equal("Uno", SubtitleReader.Read(File.ReadAllBytes(path), path)!.Cues[0].Text);

        Assert.Throws<InvalidOperationException>(() => processor.SaveEdited(view.Id, view.Fingerprint, view.Cues));

        processor.Undo(view.Id);
        Assert.Equal(original, File.ReadAllBytes(path));
    }
}
