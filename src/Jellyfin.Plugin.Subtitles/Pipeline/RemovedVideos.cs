using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Jellyfin.Plugin.Subtitles.Pipeline;

/// <summary>
/// A video Jellyfin removed from its library.
/// </summary>
/// <param name="ItemId">The library item it was.</param>
/// <param name="Path">Its file.</param>
public sealed record RemovedVideo(Guid ItemId, string Path);

/// <summary>
/// Videos removed from the library, and folders a video was added to, waiting to have their results cleaned up: once
/// nothing has been reported for the quiet delay (as with new videos), the results of removed videos whose file is gone
/// are forgotten (their subtitles' too), and stale results in those folders (a copy replaced by one filed under a new
/// name) with them (see <see cref="SubtitleProcessor.DropForRemovedVideos"/>). A video back at the same path keeps its
/// results. Holds at most <see cref="MaxQueued"/> of each; past that, the nightly prune sees to the rest.
/// </summary>
public sealed class RemovedVideos
{
    /// <summary>The most videos (and folders) queued at once.</summary>
    public const int MaxQueued = 5000;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, RemovedVideo> _removed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _folders = new(StringComparer.Ordinal);
    private readonly int _max;
    private DateTimeOffset? _last;

    /// <summary>
    /// Initializes a new instance of the <see cref="RemovedVideos"/> class.
    /// </summary>
    /// <param name="max">The most of each queued at once (for tests).</param>
    public RemovedVideos(int max = MaxQueued)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        _max = max;
    }

    /// <summary>Gets how many videos and folders are queued.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _removed.Count + _folders.Count;
            }
        }
    }

    /// <summary>
    /// Queues a video Jellyfin removed: the quiet delay starts over.
    /// </summary>
    /// <param name="itemId">The library item.</param>
    /// <param name="path">Its file.</param>
    /// <param name="now">The time.</param>
    /// <returns>Whether it is queued.</returns>
    public bool Removed(Guid itemId, string? path, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        lock (_lock)
        {
            if (!_removed.ContainsKey(path) && _removed.Count >= _max)
            {
                return false;
            }

            _removed[path] = new RemovedVideo(itemId, path);
            _last = now;
            return true;
        }
    }

    /// <summary>
    /// Queues the folder of a video Jellyfin added (a replacement for one removed, perhaps): its stale results are
    /// cleaned up with the rest. The quiet delay starts over.
    /// </summary>
    /// <param name="videoPath">The added video's file.</param>
    /// <param name="now">The time.</param>
    /// <returns>Whether it is queued.</returns>
    public bool Added(string? videoPath, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(videoPath) || Path.GetDirectoryName(videoPath) is not { Length: > 0 } folder)
        {
            return false;
        }

        lock (_lock)
        {
            if (!_folders.Contains(folder) && _folders.Count >= _max)
            {
                return false;
            }

            _folders.Add(folder);
            _last = now;
            return true;
        }
    }

    /// <summary>
    /// When the queue is due: the quiet delay after the last report.
    /// </summary>
    /// <param name="delay">The quiet delay.</param>
    /// <returns>The time, or <c>null</c> when nothing is queued.</returns>
    public DateTimeOffset? DueAt(TimeSpan delay)
    {
        lock (_lock)
        {
            return _removed.Count + _folders.Count > 0 && _last is { } last ? last + delay : null;
        }
    }

    /// <summary>
    /// Takes everything queued, if the quiet delay has passed.
    /// </summary>
    /// <param name="now">The time.</param>
    /// <param name="delay">The quiet delay.</param>
    /// <returns>The removed videos and the folders, or <c>null</c> when nothing is due yet.</returns>
    public (IReadOnlyList<RemovedVideo> Removed, IReadOnlyList<string> Folders)? TakeIfDue(DateTimeOffset now, TimeSpan delay)
    {
        lock (_lock)
        {
            if (_removed.Count + _folders.Count == 0 || _last is not { } last || now < last + delay)
            {
                return null;
            }

            var taken = ((IReadOnlyList<RemovedVideo>)[.. _removed.Values], (IReadOnlyList<string>)[.. _folders]);
            _removed.Clear();
            _folders.Clear();
            return taken;
        }
    }

    /// <summary>
    /// Whether a result belongs to a removed video: it is for the same library item, records that video, is the video's
    /// own (an embedded track), or, for an older result that records no item, is a subtitle file named after the video
    /// beside it (<c>Film.en.srt</c> beside <c>Film.mkv</c>).
    /// </summary>
    /// <param name="r">The result.</param>
    /// <param name="video">The removed video.</param>
    /// <returns><c>true</c> if it goes with the video.</returns>
    public static bool Concerns(SubtitleResult r, RemovedVideo video)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(video);
        if ((video.ItemId != Guid.Empty && r.ItemId == video.ItemId)
            || string.Equals(r.VideoPath, video.Path, StringComparison.Ordinal)
            || string.Equals(r.SubtitlePath, video.Path, StringComparison.Ordinal))
        {
            return true;
        }

        return r.ItemId == Guid.Empty
            && string.Equals(Path.GetDirectoryName(r.SubtitlePath), Path.GetDirectoryName(video.Path), StringComparison.Ordinal)
            && Path.GetFileName(r.SubtitlePath).StartsWith(Path.GetFileNameWithoutExtension(video.Path) + ".", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a result's subtitle file (or its video) is in one of the folders.
    /// </summary>
    /// <param name="r">The result.</param>
    /// <param name="folders">The folders.</param>
    /// <returns><c>true</c> if it is.</returns>
    public static bool InFolders(SubtitleResult r, IReadOnlyCollection<string> folders)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(folders);
        return folders.Count > 0
            && ((Path.GetDirectoryName(r.SubtitlePath) is { } folder && folders.Contains(folder))
                || (StaleResults.VideoOf(r) is { } video && Path.GetDirectoryName(video) is { } videoFolder && folders.Contains(videoFolder)));
    }
}
