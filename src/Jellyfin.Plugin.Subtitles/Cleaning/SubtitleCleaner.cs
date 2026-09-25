using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Subtitles.Formats;

namespace Jellyfin.Plugin.Subtitles.Cleaning;

/// <summary>
/// What a clean-up step changed.
/// </summary>
public enum CleanChangeKind
{
    /// <summary>An advert or credit line (site promotion, "subtitles by …") was removed.</summary>
    RemovedAdvert = 0,

    /// <summary>A cue with no visible text was removed.</summary>
    RemovedEmpty,

    /// <summary>A cue repeating the previous one while it was still showing was merged into it.</summary>
    MergedDuplicate,

    /// <summary>A cue that ran into the next one was shortened.</summary>
    FixedOverlap,

    /// <summary>A flash cue, too short to read, was lengthened.</summary>
    ExtendedShortCue,

    /// <summary>Hearing-impaired descriptions or speaker labels were removed.</summary>
    StrippedHearingImpaired,
}

/// <summary>
/// One change made by <see cref="SubtitleCleaner"/>.
/// </summary>
/// <param name="Kind">What was done.</param>
/// <param name="At">The start time of the affected cue (before cleaning).</param>
/// <param name="Before">The cue text before.</param>
/// <param name="After">The cue text after (empty if the cue was removed).</param>
public sealed record CleanChange(CleanChangeKind Kind, TimeSpan At, string Before, string After)
{
    /// <summary>Gets when the cue ended before the change (timing changes and merges).</summary>
    public TimeSpan? EndBefore { get; init; }

    /// <summary>Gets when the cue ends after the change (timing changes and merges).</summary>
    public TimeSpan? EndAfter { get; init; }
}

/// <summary>
/// Clean-up options. The defaults are safe for any subtitle; stripping hearing-impaired text is opt-in.
/// </summary>
public sealed record CleanOptions
{
    /// <summary>Gets a value indicating whether advert and credit lines are removed.</summary>
    public bool RemoveAdverts { get; init; } = true;

    /// <summary>Gets a value indicating whether overlapping cues are separated.</summary>
    public bool FixOverlaps { get; init; } = true;

    /// <summary>Gets a value indicating whether flash cues are lengthened (never into the next cue).</summary>
    public bool ExtendShortCues { get; init; } = true;

    /// <summary>Gets a value indicating whether hearing-impaired descriptions (<c>[door slams]</c>) and speaker labels are removed.</summary>
    public bool StripHearingImpaired { get; init; }

    /// <summary>Gets a value indicating whether a cue repeating the previous one while it is still showing is merged into it.</summary>
    public bool MergeDuplicates { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether timing fixes also apply to ASS typesetting: signs and karaoke (override tags
    /// such as <c>\pos</c>, <c>\move</c>, <c>\k</c>, <c>\t</c>, <c>\fad</c>, <c>\clip</c>), layers above 0 and styles other
    /// than the dialogue style. They are short and overlap on purpose, so this is off by default.
    /// </summary>
    public bool FixTypesetTiming { get; init; }

    /// <summary>
    /// Gets the duration below which a cue counts as a flash, too short to read, and is lengthened. Short interjections
    /// ("Hey!") commonly run 0.5–0.8 s and read fine, so they are left alone.
    /// </summary>
    public TimeSpan FlashThreshold { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Gets how long a flash cue is lengthened to, where there's room (5/6 s, a common broadcast minimum).</summary>
    public TimeSpan MinimumDuration { get; init; } = TimeSpan.FromMilliseconds(833);

    /// <summary>Gets the gap kept between consecutive cues when adjusting them (about one frame).</summary>
    public TimeSpan MinimumGap { get; init; } = TimeSpan.FromMilliseconds(42);

    /// <summary>
    /// Gets the largest overlap treated as sloppy timing and fixed. Bigger overlaps are usually deliberate (two speakers
    /// shown at once) and are left alone.
    /// </summary>
    public TimeSpan MaximumOverlapToFix { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Tidies a subtitle document. Every change is reported, so it can be shown for review and recorded for undo.
/// </summary>
public static partial class SubtitleCleaner
{
    /// <summary>Cues this close to either end of the file are checked for bare web addresses (credits usually sit there).</summary>
    private const int EdgeCues = 3;

    /// <summary>
    /// Cleans a document.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="options">What to do.</param>
    /// <returns>The cleaned document and the changes made, in order.</returns>
    public static (SubtitleDocument Document, IReadOnlyList<CleanChange> Changes) Clean(SubtitleDocument document, CleanOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);

        var changes = new List<CleanChange>();
        var cues = document.Cues.ToList();

        // 1. adverts
        if (options.RemoveAdverts)
        {
            var kept = new List<SubtitleCue>();
            for (var i = 0; i < cues.Count; i++)
            {
                if (IsAdvert(cues[i], i, cues.Count))
                {
                    changes.Add(new CleanChange(CleanChangeKind.RemovedAdvert, cues[i].Start, cues[i].Text, string.Empty));
                }
                else
                {
                    kept.Add(cues[i]);
                }
            }

            cues = kept;
        }

        // 2. hearing-impaired text
        if (options.StripHearingImpaired)
        {
            for (var i = 0; i < cues.Count; i++)
            {
                var stripped = StripHearingImpaired(cues[i].Text);
                if (!string.Equals(stripped, cues[i].Text, StringComparison.Ordinal))
                {
                    changes.Add(new CleanChange(CleanChangeKind.StrippedHearingImpaired, cues[i].Start, cues[i].Text, stripped));
                    cues[i] = cues[i] with { Text = stripped };
                }
            }
        }

        // 3. empty cues
        var nonEmpty = new List<SubtitleCue>();
        foreach (var c in cues)
        {
            if (SubtitleMarkup.ToPlainText(c.Text).Length == 0)
            {
                changes.Add(new CleanChange(CleanChangeKind.RemovedEmpty, c.Start, c.Text, string.Empty));
            }
            else
            {
                nonEmpty.Add(c);
            }
        }

        cues = nonEmpty;

        // 4. duplicates: the same text overlapping the line before (a ripping or merging error). A repeat that follows the
        //    previous line, however closely, is left: chants, echoes and people repeating each other are real dialogue.
        var merged = new List<SubtitleCue>();
        foreach (var c in cues)
        {
            if (options.MergeDuplicates
                && merged.Count > 0
                && string.Equals(SubtitleMarkup.ToPlainText(merged[^1].Text), SubtitleMarkup.ToPlainText(c.Text), StringComparison.Ordinal)
                && c.Start < merged[^1].End)
            {
                var newEnd = c.End > merged[^1].End ? c.End : merged[^1].End;
                changes.Add(new CleanChange(CleanChangeKind.MergedDuplicate, c.Start, c.Text, merged[^1].Text) { EndBefore = merged[^1].End, EndAfter = newEnd });
                merged[^1] = merged[^1] with { End = newEnd };
            }
            else
            {
                merged.Add(c);
            }
        }

        cues = merged;

        // 5. timing: overlaps, then cues too short to read. ASS typesetting is left alone unless asked for.
        var dialogueStyle = DialogueStyle(document, cues);
        bool Fixable(SubtitleCue c) => options.FixTypesetTiming || !IsTypeset(document, c, dialogueStyle);
        for (var i = 0; i < cues.Count - 1; i++)
        {
            var (c, next) = (cues[i], cues[i + 1]);
            var overlap = c.End - next.Start;
            // Identical overlapping lines are a duplicate for the merge (or its review), not sloppy timing
            var duplicate = string.Equals(SubtitleMarkup.ToPlainText(c.Text), SubtitleMarkup.ToPlainText(next.Text), StringComparison.Ordinal);
            if (options.FixOverlaps && !duplicate && overlap > TimeSpan.Zero && overlap <= options.MaximumOverlapToFix && Fixable(c) && Fixable(next))
            {
                var end = next.Start - options.MinimumGap;
                if (end > c.Start)
                {
                    changes.Add(new CleanChange(CleanChangeKind.FixedOverlap, c.Start, c.Text, c.Text) { EndBefore = c.End, EndAfter = end });
                    cues[i] = c with { End = end };
                }
            }
        }

        if (options.ExtendShortCues)
        {
            for (var i = 0; i < cues.Count; i++)
            {
                var c = cues[i];
                if (c.Duration >= options.FlashThreshold || !Fixable(c))
                {
                    continue;
                }

                var wanted = c.Start + options.MinimumDuration;
                var limit = i + 1 < cues.Count ? cues[i + 1].Start - options.MinimumGap : wanted;
                var end = wanted < limit ? wanted : limit;
                if (end > c.End)
                {
                    changes.Add(new CleanChange(CleanChangeKind.ExtendedShortCue, c.Start, c.Text, c.Text) { EndBefore = c.End, EndAfter = end });
                    cues[i] = c with { End = end };
                }
            }
        }

        return (document with { Cues = cues }, changes);
    }

    /// <summary>
    /// Whether an ASS event is typesetting (a sign, karaoke, an effect) rather than ordinary dialogue: it has
    /// positioning, movement, karaoke, transform, fade or clip override tags, sits on a layer above 0, or uses a style
    /// other than the document's dialogue style (the most common one). Always <c>false</c> for SubRip and WebVTT.
    /// </summary>
    /// <param name="document">The document the cue belongs to.</param>
    /// <param name="cue">The cue.</param>
    /// <param name="dialogueStyle">The document's dialogue style (see <see cref="DialogueStyle"/>).</param>
    /// <returns><c>true</c> for typesetting.</returns>
    public static bool IsTypeset(SubtitleDocument document, SubtitleCue cue, string? dialogueStyle)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(cue);
        if (document.Format != SubtitleFormat.Ass)
        {
            return false;
        }

        if (TypesetTag().IsMatch(cue.Text))
        {
            return true;
        }

        if (cue.AssFields is not { } fields)
        {
            return false;
        }

        var layer = Field(document, fields, "Layer");
        var style = Field(document, fields, "Style");
        return (layer is not null && layer.Trim() is not ("0" or ""))
            || (dialogueStyle is not null && style is not null && !string.Equals(style.Trim(), dialogueStyle, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The style most ASS events use, taken to be the dialogue style; <c>null</c> for other formats.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="cues">Its cues.</param>
    /// <returns>The style name, or <c>null</c>.</returns>
    public static string? DialogueStyle(SubtitleDocument document, IEnumerable<SubtitleCue> cues)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(cues);
        return document.Format != SubtitleFormat.Ass ? null
            : cues.Select(c => c.AssFields is { } f ? Field(document, f, "Style")?.Trim() : null)
                .OfType<string>()
                .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault();
    }

    private static string? Field(SubtitleDocument document, IReadOnlyList<string> fields, string name)
    {
        var i = document.AssFormat.ToList().FindIndex(f => f.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i < fields.Count ? fields[i] : null;
    }

    /// <summary>
    /// Whether a cue is an advert or credit rather than dialogue.
    /// </summary>
    /// <param name="cue">The cue.</param>
    /// <param name="index">Its position.</param>
    /// <param name="count">Number of cues.</param>
    /// <returns><c>true</c> for adverts and credits.</returns>
    public static bool IsAdvert(SubtitleCue cue, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(cue);

        var text = SubtitleMarkup.ToPlainText(cue.Text);
        if (SiteAdvert().IsMatch(text))
        {
            return true;
        }

        // Credit phrases and bare web addresses can occur in dialogue too, so they only count near the start or end,
        // where credits sit
        var nearEdge = index < EdgeCues || index >= count - EdgeCues;
        return nearEdge && (CreditLine().IsMatch(text) || WebAddress().IsMatch(text));
    }

    /// <summary>
    /// Removes hearing-impaired descriptions (<c>[door slams]</c>, <c>(laughs)</c>), speaker labels (<c>JOHN:</c>) and
    /// music-only lines, keeping the dialogue.
    /// </summary>
    /// <param name="text">Cue text.</param>
    /// <returns>The text without them (may be empty).</returns>
    public static string StripHearingImpaired(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = MultipleSpaces().Replace(Descriptions().Replace(raw, " "), " ").Trim();
            line = SpeakerLabel().Replace(line, "$1");
            var plain = SubtitleMarkup.ToPlainText(line);
            if (plain.Length == 0 || MusicOnly().IsMatch(plain))
            {
                continue;
            }

            lines.Add(line.Trim());
        }

        return string.Join('\n', lines);
    }

    // Unambiguous: subtitle-site promotions and names never occur in dialogue
    [GeneratedRegex(@"opensubtitles|osdb\.link|advertise your (?:product|brand)|become (?:a )?vip member|please rate (?:this|these) subtitles?|\byify\b|\byts\.[a-z]{2,3}\b|addic7ed|subscene|podnapisi|\bsubdl\b", RegexOptions.IgnoreCase)]
    private static partial Regex SiteAdvert();

    // Credit wording; only trusted near the start or end of the file
    [GeneratedRegex(@"subtitles? (?:by|provided by|created by|ripped by|downloaded from)|sync(?:ed|hronized)? (?:and|&) correct(?:ed|ions)? by|(?:ripped|encoded|translated|transcribed|corrected|resynced|timed) by\b|downloaded from", RegexOptions.IgnoreCase)]
    private static partial Regex CreditLine();

    [GeneratedRegex(@"(?:https?://|www\.)\S+|\b[\w-]+\.(?:com|org|net|to|io|tv|cc|me|info|link)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WebAddress();

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)")]
    private static partial Regex Descriptions();

    // Leading upper-case speaker label, optionally after markup or a dash: "JOHN: Hi" → "Hi", "- MARY: Hi" → "- Hi"
    [GeneratedRegex(@"^((?:<[^>]+>|\{[^}]*\}|-\s*)*)[A-Z][A-Z0-9 .'\-]{0,24}:\s+")]
    private static partial Regex SpeakerLabel();

    // Override tags that make an ASS event a sign or karaoke: position, movement, origin, karaoke, transform, fade, clip,
    // drawing mode
    [GeneratedRegex(@"\{[^}]*\\(?:pos|move|org|k[fo]?\d|K\d|t\(|fade?\(|i?clip|p[1-9])", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TypesetTag();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex MultipleSpaces();

    [GeneratedRegex(@"^[\s♪♫#*~\-]+$")]
    private static partial Regex MusicOnly();
}
