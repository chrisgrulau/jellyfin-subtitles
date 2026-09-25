namespace Jellyfin.Plugin.Subtitles.Candidates;

/// <summary>
/// A subtitle offered by a source, described by whatever the source knows about it. Unknown facts are <c>null</c> and
/// simply don't contribute to its score.
/// </summary>
public sealed record SubtitleCandidate
{
    /// <summary>Gets the source that offered it (e.g. <c>Open Subtitles</c>, <c>Embedded</c>).</summary>
    public required string Source { get; init; }

    /// <summary>Gets the source's own id, used to fetch it.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the release name the subtitle was made for (usually the video file name it was synced to).</summary>
    public string ReleaseName { get; init; } = string.Empty;

    /// <summary>Gets the file format (<c>srt</c>, <c>ass</c> …), if known.</summary>
    public string? Format { get; init; }

    /// <summary>Gets the language as an ISO 639-2 code.</summary>
    public string? Language { get; init; }

    /// <summary>Gets the frame rate the subtitle was timed for, if known.</summary>
    public double? FrameRate { get; init; }

    /// <summary>Gets how many times it was downloaded.</summary>
    public int? DownloadCount { get; init; }

    /// <summary>Gets the community rating (0–10).</summary>
    public double? Rating { get; init; }

    /// <summary>Gets a value indicating whether the source matched it to this exact file by content fingerprint.</summary>
    public bool? IsHashMatch { get; init; }

    /// <summary>Gets a value indicating whether it was machine translated.</summary>
    public bool? MachineTranslated { get; init; }

    /// <summary>Gets a value indicating whether it was translated by AI.</summary>
    public bool? AiTranslated { get; init; }

    /// <summary>Gets a value indicating whether it includes hearing-impaired descriptions (SDH).</summary>
    public bool? HearingImpaired { get; init; }

    /// <summary>Gets a value indicating whether it only covers foreign-language parts (forced).</summary>
    public bool? Forced { get; init; }

    /// <summary>Gets the uploader, if known.</summary>
    public string? Uploader { get; init; }
}
