using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Subtitles.Formats;

/// <summary>
/// A parsed subtitle file: its cues in time order, plus whatever the format needs to write the file back faithfully
/// (WebVTT header blocks, ASS script info, styles and event format).
/// </summary>
public sealed record SubtitleDocument
{
    /// <summary>Gets the format.</summary>
    public required SubtitleFormat Format { get; init; }

    /// <summary>Gets the cues, ordered by start time.</summary>
    public required IReadOnlyList<SubtitleCue> Cues { get; init; }

    /// <summary>
    /// Gets the text before the first cue that must be preserved: the WebVTT header (including <c>STYLE</c> and
    /// <c>REGION</c> blocks), or the ASS sections up to and including the <c>[Events]</c> heading.
    /// </summary>
    public string Header { get; init; } = string.Empty;

    /// <summary>Gets the field names from the ASS <c>[Events]</c> <c>Format:</c> line.</summary>
    public IReadOnlyList<string> AssFormat { get; init; } = [];

    /// <summary>Gets ASS event lines that aren't dialogue (e.g. <c>Comment:</c>), kept verbatim.</summary>
    public IReadOnlyList<string> AssOtherEvents { get; init; } = [];

    /// <summary>Gets any ASS sections that follow <c>[Events]</c> (e.g. <c>[Fonts]</c>), kept verbatim.</summary>
    public string AssTrailer { get; init; } = string.Empty;

    /// <summary>
    /// Returns a copy with every cue's start and end mapped through <paramref name="map"/> (used by synchronisation).
    /// Times are clamped at zero, and a cue never ends before it starts.
    /// </summary>
    /// <param name="map">Maps an original time to the corrected time.</param>
    /// <returns>The retimed document.</returns>
    public SubtitleDocument Retime(Func<TimeSpan, TimeSpan> map)
    {
        ArgumentNullException.ThrowIfNull(map);

        static TimeSpan Clamp(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;
        var cues = Cues.Select(c =>
        {
            var start = Clamp(map(c.Start));
            var end = Clamp(map(c.End));
            return c with { Start = start, End = end < start ? start : end };
        });
        return this with { Cues = [.. cues.OrderBy(c => c.Start)] };
    }
}
