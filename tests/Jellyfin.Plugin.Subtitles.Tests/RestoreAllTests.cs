using System;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.Subtitles.Pipeline;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// FEAT-06: "Restore all originals", before uninstalling. Invented dialogue throughout.
public sealed class RestoreAllTests : IDisposable
{
    private const string Original = "1\r\n00:00:01,000 --> 00:00:02,000\r\nHello there.\r\n\r\n";
    private const string Fixed = "1\r\n00:00:02,000 --> 00:00:03,000\r\nHello there.\r\n\r\n";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "subs-restore-" + Guid.NewGuid().ToString("N"));
    private readonly ResultStore _store;
    private readonly SubtitleFiles _files;
    private readonly SubtitleProcessor _processor;

    public RestoreAllTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new ResultStore(Path.Combine(_dir, "results.json"));
        _files = new SubtitleFiles(Path.Combine(_dir, "originals"));
        _processor = new SubtitleProcessor(_store, _files);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Changed_files_get_their_original_back_and_added_ones_are_removed()
    {
        var corrected = Corrected("a.en.srt");
        var added = Added("b.en.srt", ResultStatus.Added);
        var generated = Added("c.en.generated.srt", ResultStatus.Generated);
        var untouched = Path.Combine(_dir, "d.en.srt");
        File.WriteAllText(untouched, Original);
        _store.Put(new SubtitleResult { Id = ResultStore.IdFor(untouched), SubtitlePath = untouched, Status = ResultStatus.InSync, Fingerprint = SubtitleFiles.Fingerprint(Encoding.UTF8.GetBytes(Original)) });

        Assert.Equal(new RestorePreview(1, 2), _processor.PreviewRestoreAll());
        var batch = _processor.RestoreAll(null);

        Assert.Equal(1, batch.Restored);
        Assert.Equal(2, batch.Removed);
        Assert.Empty(batch.Skipped);
        Assert.Null(batch.Next);
        Assert.Equal(3, batch.Touched.Count);
        Assert.Equal(Original, File.ReadAllText(corrected));
        Assert.False(File.Exists(added));
        Assert.False(File.Exists(generated));
        Assert.Equal(Original, File.ReadAllText(untouched));
        Assert.All(_store.All().Where(r => r.SubtitlePath != untouched), r => Assert.Equal(ResultStatus.Undone, r.Status));

        // Nothing is left to do, and the plugin doesn't change them again on its own
        Assert.Equal(new RestorePreview(0, 0), _processor.PreviewRestoreAll());
        Assert.False(_processor.NeedsCheck(corrected, SubtitleFiles.Fingerprint(File.ReadAllBytes(corrected))));
    }

    [Fact]
    public void Files_edited_since_are_left_alone_and_reported()
    {
        var corrected = Corrected("a.en.srt");
        var added = Added("b.en.srt", ResultStatus.Added);
        File.AppendAllText(corrected, "\r\n2\r\n00:00:05,000 --> 00:00:06,000\r\nMine.\r\n");
        File.AppendAllText(added, "\r\n");
        var edited = File.ReadAllText(corrected);

        var batch = _processor.RestoreAll(null);

        Assert.Equal(0, batch.Restored);
        Assert.Equal(0, batch.Removed);
        Assert.Equal(2, batch.Skipped.Count);
        Assert.All(batch.Skipped, s => Assert.Contains("changed after", s.Reason, StringComparison.Ordinal));
        Assert.Contains(batch.Skipped, s => s.File == "a.en.srt" && s.Name == "Invented Film");
        Assert.Equal(edited, File.ReadAllText(corrected));
        Assert.True(File.Exists(added));
        Assert.Empty(batch.Touched);
    }

    [Fact]
    public void A_changed_file_that_is_gone_is_not_brought_back_and_an_added_one_that_is_gone_is_just_recorded()
    {
        var corrected = Corrected("a.en.srt");
        var added = Added("b.en.srt", ResultStatus.Added);
        File.Delete(corrected);
        File.Delete(added);

        var batch = _processor.RestoreAll(null);

        Assert.False(File.Exists(corrected));
        Assert.Single(batch.Skipped);
        Assert.Contains("no longer there", batch.Skipped[0].Reason, StringComparison.Ordinal);
        Assert.Equal(1, batch.AlreadyGone);
        Assert.Equal(ResultStatus.Undone, _store.ForPath(added)!.Status);
    }

    [Fact]
    public void An_original_no_longer_kept_is_reported()
    {
        var corrected = Corrected("a.en.srt");
        foreach (var f in Directory.GetFiles(Path.Combine(_dir, "originals")))
        {
            File.Delete(f);
        }

        var batch = _processor.RestoreAll(null);

        Assert.Single(batch.Skipped);
        Assert.Contains("no longer kept", batch.Skipped[0].Reason, StringComparison.Ordinal);
        Assert.Equal(Fixed, File.ReadAllText(corrected));
    }

    [Fact]
    public void An_added_subtitle_the_plugin_then_corrected_is_restored_first_then_removed()
    {
        // Added as found, then corrected by the nightly check (two results for one file)
        var added = Added("b.en.srt", ResultStatus.Added);
        var fingerprint = SubtitleFiles.Fingerprint(File.ReadAllBytes(added));
        var (backup, written) = _files.Replace(added, fingerprint, Encoding.UTF8.GetBytes(Fixed));
        _store.Put(new SubtitleResult { Id = ResultStore.IdFor(added), SubtitlePath = added, Name = "Invented Film", Status = ResultStatus.Corrected, Changed = true, Backup = backup, Fingerprint = written });

        var batch = _processor.RestoreAll(null);

        Assert.Equal(1, batch.Restored);
        Assert.Equal(1, batch.Removed);
        Assert.Empty(batch.Skipped);
        Assert.False(File.Exists(added));
    }

    [Fact]
    public void Large_restores_go_in_batches_that_carry_on_from_a_cursor_past_what_was_skipped()
    {
        var files = Enumerable.Range(0, 5).Select(i => Corrected("f" + i + ".en.srt")).ToList();
        File.AppendAllText(files[1], "\r\n");

        var first = _processor.RestoreAll(null, max: 2);
        Assert.NotNull(first.Next);
        Assert.Equal(3, first.Left);
        var second = _processor.RestoreAll(first.Next, max: 2);
        var third = _processor.RestoreAll(second.Next, max: 2);

        Assert.Null(third.Next);
        Assert.Equal(0, third.Left);
        Assert.Equal(4, first.Restored + second.Restored + third.Restored);
        Assert.Single(first.Skipped.Concat(second.Skipped).Concat(third.Skipped));
        Assert.Equal(1, _processor.PreviewRestoreAll().ToRestore);
    }

    // A subtitle file whose timing the plugin corrected (the original kept)
    private string Corrected(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, Original);
        var (backup, written) = _files.Replace(path, SubtitleFiles.Fingerprint(File.ReadAllBytes(path)), Encoding.UTF8.GetBytes(Fixed));
        _store.Put(new SubtitleResult { Id = ResultStore.IdFor(path), SubtitlePath = path, Name = "Invented Film", Status = ResultStatus.Corrected, Changed = true, Backup = backup, Fingerprint = written });
        return path;
    }

    // A subtitle the plugin added (found or generated)
    private string Added(string name, ResultStatus status)
    {
        var path = Path.Combine(_dir, name);
        var written = SubtitleFiles.Create(path, Encoding.UTF8.GetBytes(Original));
        var prefix = status == ResultStatus.Generated ? SubtitleGenerator.IdPrefix : "find-";
        _store.Put(new SubtitleResult { Id = prefix + ResultStore.IdFor(path), SubtitlePath = path, Name = "Invented Film", Status = status, Changed = true, Fingerprint = written });
        return path;
    }
}
