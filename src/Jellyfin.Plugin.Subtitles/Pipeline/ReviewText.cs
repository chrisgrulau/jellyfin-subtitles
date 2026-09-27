using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Subtitles.Audit;
using Jellyfin.Plugin.Subtitles.Discrepancy;

namespace Jellyfin.Plugin.Subtitles.Pipeline;

/// <summary>
/// Keeps a result's "What changed" list and explanation true to what still waits for review: once held-back clean-up or
/// suggested wording is applied or declined, the list and the explanation stop saying it waits. Used when a review
/// decision is saved, and again when a result is shown (so results saved before this was kept up to date read right).
/// </summary>
public static class ReviewText
{
    /// <summary>The prefix of an example that waits for review.</summary>
    public const string WaitingPrefix = "Waiting for review: ";

    /// <summary>The prefix of a suggestion (a finding) that was applied in review.</summary>
    public const string AppliedPrefix = "Applied: ";

    /// <summary>What the explanation says while suggested wording waits.</summary>
    public const string SuggestionWaits = "; Apply uses the suggested wording.";

    /// <summary>What it says once all of it was applied.</summary>
    public const string SuggestionApplied = "; the suggested wording was applied.";

    /// <summary>What it says once all of it was declined.</summary>
    public const string SuggestionDeclined = "; the suggested wording was declined.";

    /// <summary>What it says when some was applied and the rest declined.</summary>
    public const string SuggestionPartly = "; some suggested wording was applied, the rest declined.";

    /// <summary>What it says when nothing waits but none of it was applied (its line changed since).</summary>
    public const string SuggestionNotApplied = "; the suggested wording wasn't applied.";

    private const string DeclinedMarker = "Declined in review";

    /// <summary>
    /// An example line for a change that waits for review.
    /// </summary>
    /// <param name="what">The change.</param>
    /// <returns>The line.</returns>
    public static string Waiting(string what) => WaitingPrefix + what;

    /// <summary>
    /// The result after a review decision: the findings applied with a suggestion are marked applied in the list, the
    /// findings declined are dropped from it, and the list and explanation are brought up to date (see <see cref="Current"/>).
    /// </summary>
    /// <param name="r">The result as saved after the decision (its findings and clean-up waiting already updated).</param>
    /// <param name="decided">The findings decided on.</param>
    /// <param name="applied">Whether they were applied (else declined).</param>
    /// <returns>The result with its list and explanation updated.</returns>
    public static SubtitleResult Settle(SubtitleResult r, IEnumerable<LineFinding> decided, bool applied)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(decided);
        // A finding without a suggestion that was applied along with the rest only flagged its line: it stays as it was
        var lines = decided.Where(f => !applied || f.Suggestion is not null || f.From is not null).SelectMany(Descriptions).ToHashSet(StringComparer.Ordinal);
        var examples = r.Examples
            .Where(e => applied || !lines.Contains(e))
            .Select(e => applied && lines.Contains(e) ? AppliedPrefix + e : e)
            .ToList();
        var (current, explanation) = Current(r with { Examples = examples }, declined: !applied);
        return r with { Examples = current, Explanation = explanation };
    }

    /// <summary>
    /// The list and explanation as they read with what waits now: with no clean-up waiting, its lines lose their
    /// "Waiting for review" (or are dropped when it was declined), and with no suggested wording waiting, the explanation
    /// says whether it was applied or declined.
    /// </summary>
    /// <param name="r">The result.</param>
    /// <param name="declined">Whether the latest decision declined (else it is read from the explanation).</param>
    /// <returns>The list and the explanation.</returns>
    public static (IReadOnlyList<string> Examples, string Explanation) Current(SubtitleResult r, bool declined = false)
    {
        ArgumentNullException.ThrowIfNull(r);
        declined |= r.Explanation.Contains(DeclinedMarker, StringComparison.Ordinal);
        IReadOnlyList<string> examples = r.Examples;
        if (r.Status != ResultStatus.Proposed && r.CleanupPending.Count == 0 && examples.Any(e => e.StartsWith(WaitingPrefix, StringComparison.Ordinal)))
        {
            examples = [.. examples
                .Where(e => !declined || !e.StartsWith(WaitingPrefix, StringComparison.Ordinal))
                .Select(e => e.StartsWith(WaitingPrefix, StringComparison.Ordinal) ? e[WaitingPrefix.Length..] : e)];
        }

        var explanation = r.Explanation;
        var wordingWaits = r.Findings.Any(f => f.Suggestion is not null && !DiscrepancyReview.IsWholeFile(f) && !DiscrepancyReview.IsSection(f));
        if (!wordingWaits && explanation.Contains(SuggestionWaits, StringComparison.Ordinal))
        {
            var reworded = r.Cleaned.GetValueOrDefault(SubtitleProcessor.RewordedKind) > 0;
            var now = (reworded, declined) switch
            {
                (true, false) => SuggestionApplied,
                (true, true) => SuggestionPartly,
                (false, true) => SuggestionDeclined,
                _ => SuggestionNotApplied,
            };
            explanation = explanation.Replace(SuggestionWaits, now, StringComparison.Ordinal);
        }

        return (examples, explanation);
    }

    // How a finding may have been listed (by the wording audit, or by the whole-file check or the fix by section)
    private static IEnumerable<string> Descriptions(LineFinding f)
        => DiscrepancyReview.IsWholeFile(f) || DiscrepancyReview.IsSection(f)
            ? [DiscrepancyReview.Describe(f), WordingAudit.Describe(f)]
            : [WordingAudit.Describe(f)];
}
