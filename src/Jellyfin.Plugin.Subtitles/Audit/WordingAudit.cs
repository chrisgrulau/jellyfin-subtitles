using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.Pipeline;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Jellyfin.Plugin.Subtitles.Sync;

namespace Jellyfin.Plugin.Subtitles.Audit;

/// <summary>
/// A subtitle line offered for an audit, on the audio's clock.
/// </summary>
/// <param name="Index">Its position in the list sent.</param>
/// <param name="At">When it is shown, in seconds of audio.</param>
/// <param name="Text">Its text (plain, one line).</param>
public sealed record AuditLine(int Index, double At, string Text);

/// <summary>
/// A line an auditor says differs in meaning from what is said.
/// </summary>
/// <param name="Line">The line's index in the list sent.</param>
/// <param name="Kind">What differs: <c>name</c>, <c>number</c>, <c>negation</c>, <c>missing</c>, <c>wrong</c> or <c>extra</c>.</param>
/// <param name="Suggestion">The line as it should read, or <c>null</c> to only flag it.</param>
/// <param name="Reason">Why, in one sentence.</param>
public sealed record AuditFinding(int Line, string Kind, string? Suggestion, string Reason);

/// <summary>
/// An auditor's answer.
/// </summary>
/// <param name="Findings">The lines that differ in meaning (none when all fit).</param>
/// <param name="Note">Why there is no answer, in words for the results list (empty when there is nothing to say).</param>
/// <param name="By">Who decided (for example the model), if anyone did.</param>
public sealed record AuditAnswer(IReadOnlyList<AuditFinding> Findings, string Note, string? By);

/// <summary>
/// Compares what is said with the subtitle lines shown at the same time and flags lines whose meaning differs (names,
/// numbers, negations, missing or wrong words), not ordinary subtitle shortening (for example the AI plugin).
/// </summary>
public interface ITextAuditor
{
    /// <summary>
    /// Audits the lines against what was heard.
    /// </summary>
    /// <param name="heard">What was heard, on the audio's clock.</param>
    /// <param name="lines">The subtitle lines shown during the same stretches, on the audio's clock.</param>
    /// <param name="language">The subtitle's language (two letters), if known.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The answer; never throws for an unavailable or failed auditor.</returns>
    Task<AuditAnswer> AuditAsync(IReadOnlyList<HeardPhrase> heard, IReadOnlyList<AuditLine> lines, string? language, CancellationToken cancellationToken);
}

/// <summary>
/// Runs an audit of a subtitle whose timing is settled, and applies accepted suggestions.
/// </summary>
public static class WordingAudit
{
    /// <summary>The most findings kept for one subtitle.</summary>
    public const int MaxFindings = 10;

    /// <summary>The longest suggestion accepted.</summary>
    public const int MaxSuggestion = 300;

    /// <summary>How far (seconds) a line may be from where it was found and still be the same line when applying.</summary>
    public const double SameLineWithin = 1.5;

    /// <summary>The kinds of difference accepted.</summary>
    public static readonly IReadOnlySet<string> Kinds = new HashSet<string>(StringComparer.Ordinal) { "name", "number", "negation", "missing", "wrong", "extra" };

    /// <summary>
    /// Audits a subtitle file's lines against the transcribed stretches.
    /// </summary>
    /// <param name="auditor">The auditor.</param>
    /// <param name="file">The subtitle as it is (or will be) in the file.</param>
    /// <param name="toAudio">Maps the file's times to the audio's clock.</param>
    /// <param name="transcripts">The transcribed stretches.</param>
    /// <param name="language">The subtitle's language (two letters), if known.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The findings (times and text as in the file) and a note.</returns>
    public static async Task<(IReadOnlyList<LineFinding> Findings, string Note, string? By)> RunAsync(ITextAuditor auditor, SubtitleDocument file, Func<TimeSpan, TimeSpan> toAudio, IReadOnlyList<(double Start, Transcript Transcript)> transcripts, string? language, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditor);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(toAudio);
        ArgumentNullException.ThrowIfNull(transcripts);
        var heard = MeaningAligner.Phrases(transcripts);
        var (lines, cues) = Lines(file, toAudio, transcripts);
        if (heard.Count == 0 || lines.Count == 0)
        {
            return ([], string.Empty, null);
        }

        var answer = await auditor.AuditAsync(heard, lines, language, cancellationToken).ConfigureAwait(false);
        return (Findings(answer, lines, cues, file), answer.Note, answer.By);
    }

    /// <summary>
    /// The lines shown during the transcribed stretches (their start on the audio's clock within a stretch).
    /// </summary>
    /// <param name="file">The subtitle.</param>
    /// <param name="toAudio">Maps the file's times to the audio's clock.</param>
    /// <param name="transcripts">The transcribed stretches.</param>
    /// <returns>The lines offered and, for each, its cue's position in the file.</returns>
    public static (IReadOnlyList<AuditLine> Lines, IReadOnlyList<int> Cues) Lines(SubtitleDocument file, Func<TimeSpan, TimeSpan> toAudio, IReadOnlyList<(double Start, Transcript Transcript)> transcripts)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(toAudio);
        ArgumentNullException.ThrowIfNull(transcripts);
        var length = TranscriptSynchroniser.SnippetLength.TotalSeconds;
        var lines = new List<AuditLine>();
        var cues = new List<int>();
        for (var i = 0; i < file.Cues.Count; i++)
        {
            var at = toAudio(file.Cues[i].Start).TotalSeconds;
            if (!transcripts.Any(t => at >= t.Start && at < t.Start + length))
            {
                continue;
            }

            var text = SubtitleMarkup.ToPlainText(file.Cues[i].Text).ReplaceLineEndings(" ").Trim();
            if (text.Length > 0)
            {
                lines.Add(new AuditLine(lines.Count, Math.Round(at, 1), text.Length > 200 ? text[..200] + "…" : text));
                cues.Add(i);
            }
        }

        return (lines, cues);
    }

    /// <summary>
    /// Keeps findings for offered lines only, of a known kind, one per line, at most <see cref="MaxFindings"/>; a
    /// suggestion that is empty, too long or the same as the line only flags it.
    /// </summary>
    /// <param name="answer">The auditor's answer.</param>
    /// <param name="lines">The lines offered.</param>
    /// <param name="cues">Each offered line's cue position in the file.</param>
    /// <param name="file">The subtitle.</param>
    /// <returns>The findings, with times and text as in the file.</returns>
    public static IReadOnlyList<LineFinding> Findings(AuditAnswer answer, IReadOnlyList<AuditLine> lines, IReadOnlyList<int> cues, SubtitleDocument file)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(cues);
        ArgumentNullException.ThrowIfNull(file);
        var seen = new HashSet<int>();
        var findings = new List<LineFinding>();
        foreach (var f in answer.Findings)
        {
            if (f.Line < 0 || f.Line >= lines.Count || !Kinds.Contains(f.Kind) || !seen.Add(f.Line))
            {
                continue;
            }

            var cue = file.Cues[cues[f.Line]];
            var suggestion = f.Suggestion?.ReplaceLineEndings(" ").Trim();
            if (string.IsNullOrEmpty(suggestion) || suggestion.Length > MaxSuggestion || string.Equals(suggestion, lines[f.Line].Text, StringComparison.Ordinal))
            {
                suggestion = null;
            }

            findings.Add(new LineFinding(cue.Start.TotalSeconds, cue.Text, suggestion, f.Kind, f.Reason));
            if (findings.Count == MaxFindings)
            {
                break;
            }
        }

        return findings;
    }

    /// <summary>
    /// Applies suggestions to lines that are still as they were found (same text, about the same time).
    /// </summary>
    /// <param name="file">The subtitle as it is in the file now.</param>
    /// <param name="findings">The findings.</param>
    /// <returns>The subtitle and how many lines were changed.</returns>
    public static (SubtitleDocument Document, int Changed) Apply(SubtitleDocument file, IEnumerable<LineFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(findings);
        var cues = file.Cues.ToList();
        var changed = 0;
        foreach (var f in findings.Where(f => f.Suggestion is not null))
        {
            var i = cues.FindIndex(c => string.Equals(c.Text, f.Current, StringComparison.Ordinal) && Math.Abs(c.Start.TotalSeconds - f.Time) <= SameLineWithin);
            if (i >= 0)
            {
                cues[i] = cues[i] with { Text = f.Suggestion! };
                changed++;
            }
        }

        return (file with { Cues = cues }, changed);
    }

    /// <summary>
    /// Describes a finding for the results list.
    /// </summary>
    /// <param name="f">The finding.</param>
    /// <returns>One line of text.</returns>
    public static string Describe(LineFinding f)
    {
        ArgumentNullException.ThrowIfNull(f);
        var at = TimeSpan.FromSeconds(Math.Max(0, f.Time)).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);
        var current = SubtitleMarkup.ToPlainText(f.Current).ReplaceLineEndings(" ").Trim();
        return f.Suggestion is null
            ? $"{at} ({f.Kind}): “{current}” — {f.Reason}"
            : $"{at} ({f.Kind}): “{current}” → “{f.Suggestion}” — {f.Reason}";
    }
}
