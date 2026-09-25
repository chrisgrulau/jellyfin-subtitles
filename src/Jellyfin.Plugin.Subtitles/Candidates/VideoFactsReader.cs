using System;
using System.IO;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.Subtitles.Candidates;

/// <summary>
/// Collects <see cref="VideoFacts"/> from a Jellyfin video item.
/// </summary>
public static class VideoFactsReader
{
    /// <summary>
    /// Reads the facts about a video.
    /// </summary>
    /// <param name="video">The video item.</param>
    /// <returns>The facts.</returns>
    public static VideoFacts Read(Video video)
    {
        ArgumentNullException.ThrowIfNull(video);

        var path = video.Path ?? string.Empty;
        var stream = video.GetDefaultVideoStream();
        var episode = video as Episode;
        return new VideoFacts
        {
            FileName = Path.GetFileName(path),
            FolderName = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty),
            Duration = video.RunTimeTicks is > 0 and var ticks ? TimeSpan.FromTicks(ticks) : null,
            FrameRate = stream?.RealFrameRate ?? stream?.AverageFrameRate,
            Season = episode?.ParentIndexNumber,
            Episode = episode?.IndexNumber,
            EndingEpisode = episode?.IndexNumberEnd,
        };
    }
}
