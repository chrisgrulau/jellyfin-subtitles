using System;
using System.IO;

namespace Jellyfin.Plugin.Subtitles.Pipeline;

/// <summary>
/// Why a result no longer stands for anything on disk.
/// </summary>
public enum Staleness
{
    /// <summary>Its files are there (or it legitimately has none yet).</summary>
    None,

    /// <summary>Its subtitle file is gone (replaced or removed), where its folder is still there.</summary>
    SubtitleGone,

    /// <summary>Its video is gone (replaced or removed), where its folder is still there.</summary>
    VideoGone,

    /// <summary>A file it needs is missing and so is its folder: an offline share, perhaps; nothing is cleared.</summary>
    Unreachable,
}

/// <summary>
/// Thrown when an action is asked of a result whose files are gone: the result has been cleared. It is an
/// <see cref="InvalidOperationException"/>, so every action answers it with 409 and its message.
/// </summary>
public sealed class StaleResultException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StaleResultException"/> class.
    /// </summary>
    public StaleResultException()
        : base(StaleResults.SubtitleGoneMessage)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StaleResultException"/> class.
    /// </summary>
    /// <param name="message">The reason.</param>
    public StaleResultException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StaleResultException"/> class.
    /// </summary>
    /// <param name="message">The reason.</param>
    /// <param name="innerException">What was thrown.</param>
    public StaleResultException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// When a result is stale: its video, or its subtitle file, was replaced or removed (another tool filing a new copy
/// under a new name, say), so it can no longer be acted on and is cleared. One rule for the results list (stale rows are
/// hidden), each action (409, and the result is cleared), bulk jobs (skipped and cleared) and
/// <see cref="ResultStore.Prune"/>.
/// <para>
/// Some results legitimately have no subtitle file (see <see cref="StandsForVideo"/>): they stand for their video and go
/// only with it. A file whose folder is missing too is never taken as gone (an offline share keeps its results).
/// </para>
/// </summary>
public static class StaleResults
{
    /// <summary>What an action on a result whose subtitle file is gone answers.</summary>
    public const string SubtitleGoneMessage = "This subtitle file no longer exists (replaced or removed) — the result has been cleared.";

    /// <summary>What an action on a result whose video is gone answers.</summary>
    public const string VideoGoneMessage = "This video no longer exists (replaced or removed) — the result has been cleared.";

    /// <summary>What an action answers when a file it needs, and its folder, can't be found (nothing is cleared).</summary>
    public const string UnreachableMessage = "The subtitle's folder can't be reached right now (an offline share?), so nothing was done; try again later.";

    /// <summary>What an action that needs the subtitle file answers for a result that has none yet (nothing is cleared).</summary>
    public const string NoFileMessage = "There's no subtitle file for this (yet), so nothing was done.";

    /// <summary>What a bulk job records for an item it skipped because its files are gone.</summary>
    public const string BulkSkipReason = "File no longer exists (replaced or removed); the result was cleared.";

    /// <summary>
    /// Whether a result stands for its video rather than for a subtitle file, so it legitimately has no file (its
    /// subtitle path is only where one would go) and is kept for as long as the video is:
    /// <list type="bullet">
    /// <item>any <see cref="ResultStatus.NotFound"/> result (nothing fitting was found);</item>
    /// <item>a search (<c>find-</c>) that didn't add a file: deferred for want of speech-to-text, failed, can't write
    /// there, or undone (the added file was removed, and it isn't searched for again on its own); only
    /// <see cref="ResultStatus.Added"/> has a file;</item>
    /// <item>every result of generating a subtitle (<c>gen-</c>: failed, no speech, can't write, generated, replaced or
    /// undone; a generated file someone deleted isn't generated again).</item>
    /// </list>
    /// An embedded track's result (<c>emb-</c>) records the video as its path, so it goes with the video anyway.
    /// </summary>
    /// <param name="r">The result.</param>
    /// <returns><c>true</c> if it has no subtitle file of its own to lose.</returns>
    public static bool StandsForVideo(SubtitleResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return r.Status == ResultStatus.NotFound
            || (r.Id.StartsWith(SubtitleFinder.IdPrefix, StringComparison.Ordinal) && r.Status != ResultStatus.Added)
            || r.Id.StartsWith(SubtitleGenerator.IdPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// The result's video, where it is recorded: <see cref="SubtitleResult.VideoPath"/>, or the subtitle path of an
    /// embedded track's result (the video stands in for the track). Older results of subtitle files don't record it.
    /// </summary>
    /// <param name="r">The result.</param>
    /// <returns>The video's path, or <c>null</c>.</returns>
    public static string? VideoOf(SubtitleResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return !string.IsNullOrEmpty(r.VideoPath) ? r.VideoPath : r.Id.StartsWith("emb-", StringComparison.Ordinal) ? r.SubtitlePath : null;
    }

    /// <summary>
    /// Whether a result is stale: its video is gone, or its subtitle file is gone and it isn't one that stands for its
    /// video (<see cref="StandsForVideo"/>). A missing file whose folder is missing too is
    /// <see cref="Staleness.Unreachable"/>, never gone.
    /// </summary>
    /// <param name="r">The result.</param>
    /// <param name="fileExists">Whether a file exists.</param>
    /// <param name="folderExists">Whether a folder exists.</param>
    /// <returns>Why it is stale, or <see cref="Staleness.None"/>.</returns>
    /// <param name="videoOf">Where to find the video of a result that doesn't record it (the library), if anywhere.</param>
    public static Staleness Check(SubtitleResult r, Func<string, bool> fileExists, Func<string, bool> folderExists, Func<SubtitleResult, string?>? videoOf = null)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(folderExists);
        var unreachable = false;
        if ((VideoOf(r) ?? videoOf?.Invoke(r)) is { Length: > 0 } video && !fileExists(video))
        {
            if (FolderThere(video, folderExists))
            {
                return Staleness.VideoGone;
            }

            unreachable = true;
        }

        if (!StandsForVideo(r) && !string.IsNullOrEmpty(r.SubtitlePath) && !fileExists(r.SubtitlePath))
        {
            if (FolderThere(r.SubtitlePath, folderExists))
            {
                return Staleness.SubtitleGone;
            }

            unreachable = true;
        }

        return unreachable ? Staleness.Unreachable : Staleness.None;
    }

    /// <summary>
    /// Whether a result is stale and is to be cleared (and hidden from the list).
    /// </summary>
    /// <param name="r">The result.</param>
    /// <param name="fileExists">Whether a file exists.</param>
    /// <param name="folderExists">Whether a folder exists.</param>
    /// <returns><c>true</c> if its video or its subtitle file is gone.</returns>
    /// <param name="videoOf">Where to find the video of a result that doesn't record it, if anywhere.</param>
    public static bool IsGone(SubtitleResult r, Func<string, bool> fileExists, Func<string, bool> folderExists, Func<SubtitleResult, string?>? videoOf = null)
        => Check(r, fileExists, folderExists, videoOf) is Staleness.SubtitleGone or Staleness.VideoGone;

    /// <summary>
    /// What an action answers for a result that is stale.
    /// </summary>
    /// <param name="why">Why it is stale.</param>
    /// <returns>The message.</returns>
    public static string MessageFor(Staleness why) => why switch
    {
        Staleness.VideoGone => VideoGoneMessage,
        Staleness.Unreachable => UnreachableMessage,
        _ => SubtitleGoneMessage,
    };

    private static bool FolderThere(string path, Func<string, bool> folderExists)
        => Path.GetDirectoryName(path) is { Length: > 0 } folder && folderExists(folder);
}
