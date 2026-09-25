namespace Jellyfin.Plugin.Subtitles.Formats;

/// <summary>
/// Text subtitle formats the plugin reads and writes.
/// </summary>
public enum SubtitleFormat
{
    /// <summary>SubRip (<c>.srt</c>).</summary>
    Srt = 0,

    /// <summary>WebVTT (<c>.vtt</c>).</summary>
    WebVtt,

    /// <summary>Advanced SubStation Alpha / SubStation Alpha (<c>.ass</c>, <c>.ssa</c>).</summary>
    Ass,
}
