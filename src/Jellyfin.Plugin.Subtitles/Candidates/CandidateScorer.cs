using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.Subtitles.Candidates;

/// <summary>
/// Whether hearing-impaired (SDH) subtitles are wanted.
/// </summary>
public enum HearingImpairedPreference
{
    /// <summary>No preference.</summary>
    Neutral = 0,

    /// <summary>Prefer subtitles with sound descriptions.</summary>
    Prefer,

    /// <summary>Prefer subtitles without them.</summary>
    Avoid,
}

/// <summary>
/// A candidate with its score and the reasons behind it, best first when ranked.
/// </summary>
/// <param name="Candidate">The candidate.</param>
/// <param name="Score">Score in [0, 1]; about 0.5 when nothing is known.</param>
/// <param name="Rejected">Whether it must not be used at all (wrong episode, forced-only …).</param>
/// <param name="Reasons">Human-readable reasons, each with its effect on the score.</param>
public sealed record ScoredCandidate(SubtitleCandidate Candidate, double Score, bool Rejected, IReadOnlyList<string> Reasons);

/// <summary>
/// Scores subtitle candidates before anything is downloaded, from what the source says about them and how their release
/// name compares with the video's. The weights favour evidence that the subtitle was timed for the same version of the
/// video; the audio check that follows is what finally decides.
/// </summary>
public static class CandidateScorer
{
    /// <summary>Score when nothing is known either way.</summary>
    public const double Neutral = 0.5;

    /// <summary>
    /// Scores one candidate.
    /// </summary>
    /// <param name="video">The video.</param>
    /// <param name="candidate">The candidate.</param>
    /// <param name="hearingImpaired">Hearing-impaired preference.</param>
    /// <returns>The scored candidate.</returns>
    public static ScoredCandidate Score(VideoFacts video, SubtitleCandidate candidate, HearingImpairedPreference hearingImpaired = HearingImpairedPreference.Neutral)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(candidate);

        var score = Neutral;
        var reasons = new List<string>();
        var rejected = false;
        void Add(double delta, string why)
        {
            score += delta;
            reasons.Add(string.Create(CultureInfo.InvariantCulture, $"{(delta >= 0 ? "+" : string.Empty)}{delta:0.00} {why}"));
        }

        void Reject(string why)
        {
            rejected = true;
            reasons.Add("rejected: " + why);
        }

        var theirs = ReleaseTags.Parse(candidate.ReleaseName);
        var ours = ReleaseTags.Parse(video.FileName);
        var folder = video.FolderName.Length > 0 ? ReleaseTags.Parse(video.FolderName) : new ReleaseTags();

        // Identity: the same file, or the wrong episode
        var exactFile = candidate.IsHashMatch == true;
        if (exactFile)
        {
            Add(0.40, "matched to this exact file by fingerprint");
        }

        if (video.Episode is { } ep && theirs.Episode is { } theirEp && (video.Season != theirs.Season || !Covers(theirs, ep, video.EndingEpisode)))
        {
            Reject(string.Create(CultureInfo.InvariantCulture, $"made for S{theirs.Season:00}E{theirEp:00}, not this episode"));
        }

        if (candidate.Forced == true)
        {
            Reject("forced subtitles only cover foreign-language parts");
        }

        // Same version of the video? Group, source, service, edition. Skipped for a fingerprint match: that subtitle
        // was synced to this exact file, so release-name differences (renamed uploads, other labelling) don't matter
        if (!exactFile)
        {
            var group = ours.Group ?? folder.Group;
            if (group is not null && theirs.Group is not null)
            {
                if (string.Equals(group, theirs.Group, StringComparison.Ordinal))
                {
                    Add(0.15, "same release group");
                }
            }

            var source = ours.Source != ReleaseSource.Unknown ? ours.Source : folder.Source;
            if (source != ReleaseSource.Unknown && theirs.Source != ReleaseSource.Unknown)
            {
                if (source == theirs.Source)
                {
                    Add(0.10, "same kind of source (" + source + ")");
                }
                else
                {
                    Add(-0.08, "different kind of source (" + theirs.Source + " vs " + source + ")");
                }
            }

            var service = ours.Service ?? folder.Service;
            if (service is not null && theirs.Service is not null && string.Equals(service, theirs.Service, StringComparison.Ordinal))
            {
                Add(0.05, "same streaming service");
            }

            var edition = video.Edition ?? ours.Edition ?? folder.Edition;
            if (!string.Equals(edition, theirs.Edition, StringComparison.OrdinalIgnoreCase))
            {
                // A different cut adds or removes whole scenes: timing drifts in steps no simple sync can fully fix
                if (edition is not null || theirs.Edition is not null)
                {
                    Add(-0.30, "made for a different cut (" + (theirs.Edition ?? "standard") + " vs " + (edition ?? "standard") + ")");
                }
            }
            else if (edition is not null)
            {
                Add(0.05, "same edition (" + edition + ")");
            }
        }

        // Frame rate: 25 vs 23.976 fps (PAL speed-up) makes subtitles drift
        if (video.FrameRate is { } vf && candidate.FrameRate is { } cf && vf > 0 && cf > 0 && Math.Abs(vf - cf) / vf > 0.01)
        {
            Add(-0.15, string.Create(CultureInfo.InvariantCulture, $"timed for {cf:0.###} fps, video is {vf:0.###} fps"));
        }

        // Quality signals
        if (candidate.MachineTranslated == true)
        {
            Add(-0.25, "machine translated");
        }

        if (candidate.AiTranslated == true)
        {
            Add(-0.20, "AI translated");
        }

        if (candidate.HearingImpaired is { } hi && hearingImpaired != HearingImpairedPreference.Neutral)
        {
            var wanted = hearingImpaired == HearingImpairedPreference.Prefer;
            Add(hi == wanted ? 0.05 : -0.05, hi ? "has sound descriptions" : "no sound descriptions");
        }

        if (candidate.DownloadCount is > 0 and var downloads)
        {
            // Popularity is a weak signal: up to +0.05 at about 100,000 downloads
            Add(Math.Min(0.05, Math.Log10(downloads) / 100), string.Create(CultureInfo.InvariantCulture, $"{downloads:N0} downloads"));
        }

        if (candidate.Rating is > 0 and var rating)
        {
            Add((Math.Clamp(rating, 0, 10) - 5) / 100, string.Create(CultureInfo.InvariantCulture, $"rated {rating:0.#}/10"));
        }

        if (candidate.Format is { Length: > 0 } format && !IsSupportedFormat(format))
        {
            Reject(format + " subtitles aren't supported yet");
        }

        return new ScoredCandidate(candidate, Math.Clamp(score, 0, 1), rejected, reasons);
    }

    /// <summary>
    /// Scores and ranks candidates: usable ones first, best score first.
    /// </summary>
    /// <param name="video">The video.</param>
    /// <param name="candidates">The candidates.</param>
    /// <param name="hearingImpaired">Hearing-impaired preference.</param>
    /// <returns>The ranked list.</returns>
    public static IReadOnlyList<ScoredCandidate> Rank(VideoFacts video, IEnumerable<SubtitleCandidate> candidates, HearingImpairedPreference hearingImpaired = HearingImpairedPreference.Neutral)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return [.. candidates.Select(c => Score(video, c, hearingImpaired))
            .OrderBy(s => s.Rejected)
            .ThenByDescending(s => s.Score)
            .ThenByDescending(s => s.Candidate.DownloadCount ?? 0)];
    }

    private static bool Covers(ReleaseTags tags, int episode, int? endingEpisode)
    {
        var first = tags.Episode!.Value;
        var last = tags.EndingEpisode ?? first;
        return first <= episode && (endingEpisode ?? episode) <= last;
    }

    private static bool IsSupportedFormat(string format)
        => format.Trim('.').ToUpperInvariant() is "SRT" or "SUBRIP" or "VTT" or "WEBVTT" or "ASS" or "SSA";
}
