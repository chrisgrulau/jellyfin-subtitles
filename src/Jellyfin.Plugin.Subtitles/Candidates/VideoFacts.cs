using System;

namespace Jellyfin.Plugin.Subtitles.Candidates;

/// <summary>
/// What is known about the video a subtitle is wanted for.
/// </summary>
public sealed record VideoFacts
{
    /// <summary>Gets the video's file name (without folder).</summary>
    public required string FileName { get; init; }

    /// <summary>Gets the name of the folder holding the video (often the original release name for films).</summary>
    public string FolderName { get; init; } = string.Empty;

    /// <summary>Gets the running time, if known.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>Gets the video frame rate, if known.</summary>
    public double? FrameRate { get; init; }

    /// <summary>Gets the season number, for episodes.</summary>
    public int? Season { get; init; }

    /// <summary>Gets the episode number, for episodes.</summary>
    public int? Episode { get; init; }

    /// <summary>Gets the last episode number of a multi-episode file.</summary>
    public int? EndingEpisode { get; init; }

    /// <summary>Gets the edition, if the library names one (e.g. <c>Director's Cut</c>).</summary>
    public string? Edition { get; init; }
}
