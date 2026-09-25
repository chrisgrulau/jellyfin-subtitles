using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Subtitles.Formats;

namespace Jellyfin.Plugin.Subtitles.Candidates;

/// <summary>
/// Findings from looking inside a downloaded subtitle, before any audio is involved.
/// </summary>
/// <param name="Adjustment">Score adjustment to add to the pre-download score.</param>
/// <param name="Rejected">Whether the subtitle is unusable (wrong language, far too short or long).</param>
/// <param name="Reasons">Human-readable reasons.</param>
public sealed record ContentAssessment(double Adjustment, bool Rejected, IReadOnlyList<string> Reasons);

/// <summary>
/// Cheap checks on a downloaded subtitle: does it cover the video's running time, is it plausibly dense, and is it in the
/// language it claims to be.
/// </summary>
public static class ContentChecks
{
    /// <summary>
    /// Assesses a downloaded subtitle.
    /// </summary>
    /// <param name="video">The video.</param>
    /// <param name="document">The parsed subtitle.</param>
    /// <param name="language">The language wanted (ISO 639-2).</param>
    /// <returns>The assessment.</returns>
    public static ContentAssessment Assess(VideoFacts video, SubtitleDocument document, string language)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(document);

        var adjustment = 0.0;
        var rejected = false;
        var reasons = new List<string>();
        if (document.Cues.Count == 0)
        {
            return new ContentAssessment(0, true, ["no readable cues"]);
        }

        var lastEnd = document.Cues.Max(c => c.End);
        if (video.Duration is { TotalSeconds: > 60 } duration)
        {
            // Subtitles usually end within the last few minutes (credits aren't subtitled). A last cue far past the end
            // means a longer cut or a different frame rate; far before it means a shorter cut or a partial file.
            var coverage = lastEnd / duration;
            var at = string.Create(CultureInfo.InvariantCulture, $"last cue at {coverage:P0} of the running time");
            if (coverage > 1.08)
            {
                (adjustment, rejected) = (adjustment - 0.4, coverage > 1.25);
                reasons.Add("runs past the end: " + at);
            }
            else if (coverage < 0.6)
            {
                (adjustment, rejected) = (adjustment - 0.4, coverage < 0.35);
                reasons.Add("stops well before the end: " + at);
            }
            else if (coverage >= 0.8)
            {
                adjustment += 0.05;
                reasons.Add("covers the running time: " + at);
            }

            var perMinute = document.Cues.Count / duration.TotalMinutes;
            if (perMinute < 1)
            {
                adjustment -= 0.2;
                reasons.Add(string.Create(CultureInfo.InvariantCulture, $"sparse: {perMinute:0.0} cues per minute (forced-only or partial?)"));
            }
        }

        var text = string.Join('\n', document.Cues.Select(c => SubtitleMarkup.ToPlainText(c.Text)));
        if (LanguageGuesser.Guess(text) is { } guess)
        {
            if (string.Equals(guess.Language, language, StringComparison.Ordinal))
            {
                adjustment += 0.05;
                reasons.Add("text is in the expected language");
            }
            else if (LanguageGuessable(language))
            {
                rejected = true;
                reasons.Add("text looks like " + guess.Language + ", not " + language);
            }
        }

        return new ContentAssessment(adjustment, rejected, reasons);
    }

    // Only reject on a language mismatch when the wanted language is one the guesser knows; otherwise it could be
    // confusing a closely related language for one it knows
    private static bool LanguageGuessable(string language) => language is "eng" or "fre" or "spa" or "ger" or "ita" or "por" or "dut";
}
