using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Subtitles.Formats;

namespace Jellyfin.Plugin.Subtitles.Pipeline;

/// <summary>
/// One line as the editor shows and returns it.
/// </summary>
/// <param name="Source">The line's position in the file when it was loaded, or -1 for a line added in the editor.</param>
/// <param name="Start">When it starts, in seconds.</param>
/// <param name="End">When it ends, in seconds.</param>
/// <param name="Text">Its text, as in the file (markup and line breaks kept).</param>
public sealed record EditorCue(int Source, double Start, double End, string Text);

/// <summary>
/// A subtitle file opened in the editor.
/// </summary>
/// <param name="Id">The result id it belongs to.</param>
/// <param name="Name">The video's name.</param>
/// <param name="File">The subtitle's file name (no folders).</param>
/// <param name="Fingerprint">The file's fingerprint when loaded (a save is refused if it changed since).</param>
/// <param name="Format">The subtitle format.</param>
/// <param name="Cues">The lines.</param>
public sealed record EditorView(string Id, string Name, string File, string Fingerprint, string Format, IReadOnlyList<EditorCue> Cues);

/// <summary>
/// Checks and applies edits made by hand in the editor.
/// </summary>
public static class SubtitleEditing
{
    /// <summary>The most lines a saved file may have.</summary>
    public const int MaxCues = 20000;

    /// <summary>The longest line text accepted.</summary>
    public const int MaxText = 1000;

    /// <summary>The latest time accepted (a day, in seconds).</summary>
    public const double MaxTime = 86400;

    /// <summary>The <see cref="SubtitleResult.Cleaned"/> key counting lines changed by hand.</summary>
    public const string EditedKind = "EditedByHand";

    /// <summary>
    /// The lines of a document as the editor shows them.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <returns>The lines.</returns>
    public static IReadOnlyList<EditorCue> ToEditor(SubtitleDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return [.. document.Cues.Select((c, i) => new EditorCue(i, Math.Round(c.Start.TotalSeconds, 3), Math.Round(c.End.TotalSeconds, 3), c.Text))];
    }

    /// <summary>
    /// Builds the edited document: lines keep their file details (identifiers, styles) through <see cref="EditorCue.Source"/>;
    /// added lines take the first line's style. Lines are sorted by start time.
    /// </summary>
    /// <param name="original">The document as loaded.</param>
    /// <param name="edited">The lines from the editor.</param>
    /// <returns>The document and how many lines were changed, added or removed; or why the edit can't be saved.</returns>
    public static (SubtitleDocument? Document, int Changed, string? Problem) Apply(SubtitleDocument original, IReadOnlyList<EditorCue> edited)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(edited);
        if (edited.Count == 0)
        {
            return (null, 0, "A subtitle needs at least one line.");
        }

        if (edited.Count > MaxCues)
        {
            return (null, 0, "Too many lines.");
        }

        var used = new HashSet<int>();
        var cues = new List<SubtitleCue>(edited.Count);
        var changed = 0;
        foreach (var e in edited)
        {
            var text = (e.Text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
            if (!double.IsFinite(e.Start) || !double.IsFinite(e.End) || e.Start < 0 || e.End <= e.Start || e.End > MaxTime)
            {
                return (null, 0, $"A line has an impossible time ({e.Start:0.###} to {e.End:0.###} s): it must end after it starts.");
            }

            if (text.Length == 0 || text.Length > MaxText)
            {
                return (null, 0, "A line is empty or too long; delete an empty line instead.");
            }

            var start = TimeSpan.FromSeconds(Math.Round(e.Start, 3));
            var end = TimeSpan.FromSeconds(Math.Round(e.End, 3));
            if (e.Source >= 0 && e.Source < original.Cues.Count && used.Add(e.Source))
            {
                var was = original.Cues[e.Source];
                var same = was.Start == start && was.End == end && string.Equals(was.Text, text, StringComparison.Ordinal);
                changed += same ? 0 : 1;
                cues.Add(same ? was : was with { Start = start, End = end, Text = text });
            }
            else
            {
                changed++;
                var style = original.Cues.Count > 0 ? original.Cues[0] : null;
                cues.Add(new SubtitleCue { Start = start, End = end, Text = text, Settings = style?.Settings, AssFields = style?.AssFields });
            }
        }

        changed += original.Cues.Count - used.Count;
        return (original with { Cues = [.. cues.OrderBy(c => c.Start)] }, changed, null);
    }
}
