using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.Pipeline;

namespace Jellyfin.Plugin.Subtitles.Discrepancy;

/// <summary>
/// The whole-file check's findings as they wait for review, and applying them: a differing line takes the suggested
/// wording, a line heard but missing is added, a line with nothing heard is removed. Nothing is applied on its own; each
/// finding is applied or declined in review (or all at once with Apply, which leaves lines with nothing heard alone).
/// A finding is found again in the file by its text and time (as the wording audit's are), so it is never applied to a
/// line that has changed since.
/// </summary>
public static class DiscrepancyReview
{
    /// <summary>What <see cref="LineFinding.From"/> says for the whole-file check.</summary>
    public const string WholeFile = "whole file";

    /// <summary>The <see cref="SubtitleResult.Cleaned"/> key counting lines fixed from the whole-file check.</summary>
    public const string FixedKind = "FixedFromWholeFile";

    /// <summary>
    /// Whether a finding comes from the whole-file check.
    /// </summary>
    /// <param name="f">The finding.</param>
    /// <returns><c>true</c> if so.</returns>
    public static bool IsWholeFile(LineFinding f)
    {
        ArgumentNullException.ThrowIfNull(f);
        return string.Equals(f.From, WholeFile, StringComparison.Ordinal);
    }

    /// <summary>
    /// A difference as a finding waiting for review, with times and text as in the file.
    /// </summary>
    /// <param name="d">The difference.</param>
    /// <param name="file">The subtitle.</param>
    /// <param name="toFile">Maps the audio's clock (seconds) back to the file's times.</param>
    /// <returns>The finding.</returns>
    public static LineFinding ToFinding(LineDiscrepancy d, SubtitleDocument file, Func<double, double> toFile)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(toFile);
        if (d.Cue < 0 || d.Cue >= file.Cues.Count)
        {
            var start = Math.Max(0, Math.Round(toFile(d.AudioStart), 3));
            return new LineFinding(start, string.Empty, d.Suggestion, d.Kind, d.Reason) { From = WholeFile, Heard = d.Heard, End = Math.Max(start + 0.5, Math.Round(toFile(d.AudioEnd), 3)) };
        }

        var cue = file.Cues[d.Cue];
        return new LineFinding(cue.Start.TotalSeconds, cue.Text, d.Suggestion, d.Kind, d.Reason) { From = WholeFile, Heard = d.Heard };
    }

    /// <summary>
    /// Where a finding's line is in the file now: the same text, starting within <see cref="Audit.WordingAudit.SameLineWithin"/>.
    /// </summary>
    /// <param name="file">The subtitle as it is now.</param>
    /// <param name="f">The finding.</param>
    /// <returns>The line's position, or -1 if it has changed or gone.</returns>
    public static int Locate(SubtitleDocument file, LineFinding f)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(f);
        for (var i = 0; i < file.Cues.Count; i++)
        {
            if (string.Equals(file.Cues[i].Text, f.Current, StringComparison.Ordinal) && Math.Abs(file.Cues[i].Start.TotalSeconds - f.Time) <= Audit.WordingAudit.SameLineWithin)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Whether a finding still applies to the file as it is now (after an edit by hand, for example): its line is still
    /// there as it was, or, for a missing line, no line is shown during it yet.
    /// </summary>
    /// <param name="file">The subtitle as it is now.</param>
    /// <param name="f">The finding.</param>
    /// <returns><c>true</c> if it still applies.</returns>
    public static bool StillOpen(SubtitleDocument file, LineFinding f)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(f);
        if (f.Kind == DiscrepancyFinder.MissingLine)
        {
            var end = f.End ?? f.Time + 1;
            return !file.Cues.Any(c => c.Start.TotalSeconds < end && c.End.TotalSeconds > f.Time);
        }

        return Locate(file, f) >= 0;
    }

    /// <summary>
    /// Applies one finding: the suggested wording, the missing line added (styled like the first line), or the line
    /// removed.
    /// </summary>
    /// <param name="file">The subtitle as it is now.</param>
    /// <param name="f">The finding.</param>
    /// <returns>The subtitle, or why the finding can't be applied.</returns>
    public static (SubtitleDocument? Document, string? Problem) ApplyOne(SubtitleDocument file, LineFinding f)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(f);
        var cues = file.Cues.ToList();
        if (f.Kind == DiscrepancyFinder.MissingLine)
        {
            var text = f.Suggestion?.Trim();
            if (string.IsNullOrEmpty(text) || text.Length > SubtitleEditing.MaxText)
            {
                return (null, "There's no line to add; open the editor instead.");
            }

            if (!StillOpen(file, f))
            {
                return (null, "A line is already shown at that time; open the editor instead.");
            }

            var start = TimeSpan.FromSeconds(Math.Round(Math.Max(0, f.Time), 3));
            var end = TimeSpan.FromSeconds(Math.Round(Math.Max(f.Time + 0.5, f.End ?? f.Time + 1), 3));
            var style = cues.Count > 0 ? cues[0] : null;
            cues.Add(new SubtitleCue { Start = start, End = end, Text = text, Settings = style?.Settings, AssFields = style?.AssFields });
            return (file with { Cues = [.. cues.OrderBy(c => c.Start)] }, null);
        }

        var i = Locate(file, f);
        if (i < 0)
        {
            return (null, "That line has changed since it was checked; open the editor instead.");
        }

        if (f.Kind == DiscrepancyFinder.Extra)
        {
            if (cues.Count == 1)
            {
                return (null, "It's the subtitle's only line; open the editor instead.");
            }

            cues.RemoveAt(i);
            return (file with { Cues = cues }, null);
        }

        var suggestion = f.Suggestion?.Trim();
        if (string.IsNullOrEmpty(suggestion) || suggestion.Length > SubtitleEditing.MaxText)
        {
            return (null, "There's no suggested wording for this line; open the editor instead.");
        }

        cues[i] = cues[i] with { Text = suggestion };
        return (file with { Cues = cues }, null);
    }

    /// <summary>
    /// Applies the whole-file findings that carry a suggestion (lines with nothing heard are only removed one at a time),
    /// skipping any whose line has changed since.
    /// </summary>
    /// <param name="file">The subtitle as it is now.</param>
    /// <param name="findings">The findings.</param>
    /// <returns>The subtitle and how many findings were applied.</returns>
    public static (SubtitleDocument Document, int Changed) ApplyAll(SubtitleDocument file, IEnumerable<LineFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(findings);
        var changed = 0;
        foreach (var f in findings.Where(f => IsWholeFile(f) && f.Kind != DiscrepancyFinder.Extra && f.Suggestion is not null))
        {
            if (ApplyOne(file, f) is ({ } done, null))
            {
                file = done;
                changed++;
            }
        }

        return (file, changed);
    }

    /// <summary>
    /// Describes a whole-file finding for the results list.
    /// </summary>
    /// <param name="f">The finding.</param>
    /// <returns>One line of text.</returns>
    public static string Describe(LineFinding f)
    {
        ArgumentNullException.ThrowIfNull(f);
        var at = TimeSpan.FromSeconds(Math.Max(0, f.Time)).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);
        var current = SubtitleMarkup.ToPlainText(f.Current).ReplaceLineEndings(" ").Trim();
        var heard = (f.Heard ?? string.Empty).ReplaceLineEndings(" ");
        return f.Kind switch
        {
            DiscrepancyFinder.MissingLine => $"{at} (missing line): heard “{heard}” — {f.Reason}",
            DiscrepancyFinder.Extra => $"{at} (nothing heard): “{current}” — {f.Reason}",
            _ => $"{at} ({f.Kind}): “{current}”, heard “{heard}” — {f.Reason}",
        };
    }

    /// <summary>
    /// Names a kind for people ("missing line", "nothing heard" …), in the plural when there are several.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <param name="count">How many.</param>
    /// <returns>The words.</returns>
    public static string KindName(string kind, int count)
    {
        var one = kind switch
        {
            DiscrepancyFinder.MissingLine => "missing line",
            DiscrepancyFinder.Extra => "line with nothing heard",
            DiscrepancyFinder.Name => "name",
            DiscrepancyFinder.Number => "number",
            DiscrepancyFinder.Negation => "negation",
            DiscrepancyFinder.Words => "line with words missing",
            _ => kind,
        };
        if (count == 1)
        {
            return "1 " + one;
        }

        var many = one switch
        {
            "line with nothing heard" => "lines with nothing heard",
            "line with words missing" => "lines with words missing",
            _ => one + "s",
        };
        return count.ToString(CultureInfo.InvariantCulture) + " " + many;
    }
}
