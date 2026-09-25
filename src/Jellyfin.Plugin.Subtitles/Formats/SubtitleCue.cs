using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Subtitles.Formats;

/// <summary>
/// One subtitle line (cue): when it shows and what it says. <see cref="Text"/> keeps the format's own markup (italics,
/// positioning, ASS override tags) with lines separated by <c>\n</c>; use <see cref="SubtitleMarkup.ToPlainText"/> for
/// matching and comparison.
/// </summary>
public sealed record SubtitleCue
{
    /// <summary>Gets when the cue appears.</summary>
    public required TimeSpan Start { get; init; }

    /// <summary>Gets when the cue disappears.</summary>
    public required TimeSpan End { get; init; }

    /// <summary>Gets the text, with the format's markup, lines separated by <c>\n</c>.</summary>
    public required string Text { get; init; }

    /// <summary>Gets the WebVTT cue identifier, if any.</summary>
    public string? Identifier { get; init; }

    /// <summary>Gets the WebVTT cue settings (position, alignment …), if any.</summary>
    public string? Settings { get; init; }

    /// <summary>
    /// Gets every field of an ASS/SSA event line in the order of the document's <c>Format:</c> line, so a retimed file
    /// is written back with styles, layers, margins and effects untouched.
    /// </summary>
    public IReadOnlyList<string>? AssFields { get; init; }

    /// <summary>Gets how long the cue shows.</summary>
    public TimeSpan Duration => End - Start;
}
