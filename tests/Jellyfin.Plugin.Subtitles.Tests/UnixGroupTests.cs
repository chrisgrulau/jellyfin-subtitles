using System;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.Subtitles.Pipeline;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// SUB-24: a rewritten subtitle keeps its group on Linux
public sealed class UnixGroupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "subs-group-" + Guid.NewGuid().ToString("N"));

    public UnixGroupTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_new_file_has_the_process_group_and_can_be_given_it_again()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var path = Path.Combine(_dir, "a.srt");
        File.WriteAllText(path, "x");
        var own = UnixGroup.EffectiveGroup();
        Assert.NotNull(own);

        // New files get the process's group (the temporary folder isn't set-group-id)
        Assert.Equal(own, UnixGroup.GroupOf(path, out var problem));
        Assert.Null(problem);

        Assert.True(UnixGroup.SetGroup(path, own.Value, out problem), problem);
        Assert.Equal(own, UnixGroup.GroupOf(path, out _));
    }

    [Fact]
    public void A_missing_file_is_reported_not_thrown()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var missing = Path.Combine(_dir, "missing.srt");
        Assert.Null(UnixGroup.GroupOf(missing, out var problem));
        Assert.Contains("errno", problem, StringComparison.Ordinal);
        Assert.False(UnixGroup.SetGroup(missing, 0, out problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void A_rewrite_and_its_undo_keep_the_original_group()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // Needs a group other than the process's own that this user belongs to (as a library shared with other tools is)
        var own = UnixGroup.EffectiveGroup();
        var other = SupplementaryGroups().FirstOrDefault(g => g != own);
        var path = Path.Combine(_dir, "b.srt");
        File.WriteAllText(path, "original");
        if (other == 0 || !UnixGroup.SetGroup(path, other, out _))
        {
            return;
        }

        var files = new SubtitleFiles(Path.Combine(_dir, "originals"));
        var (backup, written) = files.Replace(path, SubtitleFiles.Fingerprint(File.ReadAllBytes(path)), Encoding.UTF8.GetBytes("changed"));
        Assert.Equal(other, UnixGroup.GroupOf(path, out _));

        files.Restore(path, backup, written);
        Assert.Equal("original", File.ReadAllText(path));
        Assert.Equal(other, UnixGroup.GroupOf(path, out _));
    }

    [Fact]
    public void A_group_that_cannot_be_kept_never_fails_the_rewrite()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A file already in the user's own group is rewritten without a chown; a group the user isn't in is refused
        // (EPERM) and reported, which the rewrite only logs
        var path = Path.Combine(_dir, "c.srt");
        File.WriteAllText(path, "original");
        var files = new SubtitleFiles(Path.Combine(_dir, "originals"));
        files.Replace(path, SubtitleFiles.Fingerprint(File.ReadAllBytes(path)), Encoding.UTF8.GetBytes("changed"));
        Assert.Equal("changed", File.ReadAllText(path));
        Assert.Equal(UnixGroup.EffectiveGroup(), UnixGroup.GroupOf(path, out _));

        if (UnixGroup.EffectiveGroup() != 0)
        {
            Assert.False(UnixGroup.SetGroup(path, 0, out var problem));
            Assert.Contains("errno", problem, StringComparison.Ordinal);
        }
    }

    // The process's supplementary groups, from /proc (no process is started)
    private static uint[] SupplementaryGroups()
    {
        var line = File.ReadLines("/proc/self/status").FirstOrDefault(l => l.StartsWith("Groups:", StringComparison.Ordinal));
        return line is null ? [] : line["Groups:".Length..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(uint.Parse).ToArray();
    }
}
