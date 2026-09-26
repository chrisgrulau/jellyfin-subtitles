using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.Audio;
using Jellyfin.Plugin.Subtitles.Candidates;
using Jellyfin.Plugin.Subtitles.Cleaning;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Jellyfin.Plugin.Subtitles.Sync;

namespace Jellyfin.Plugin.Subtitles.Pipeline;

/// <summary>
/// A video missing a subtitle in a language.
/// </summary>
/// <param name="ItemId">The library item.</param>
/// <param name="Name">The item's name, for display.</param>
/// <param name="VideoPath">The video file.</param>
/// <param name="Video">What's known about the video (for scoring candidates).</param>
/// <param name="Language">The language wanted (three-letter code).</param>
/// <param name="Duration">The video's length.</param>
/// <param name="AudioStream">Which audio stream to listen to.</param>
public sealed record FindJob(Guid ItemId, string Name, string VideoPath, VideoFacts Video, string Language, TimeSpan Duration, int AudioStream);

/// <summary>
/// The day's download allowance is used up; searching stops until tomorrow.
/// </summary>
public sealed class DownloadLimitReachedException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="DownloadLimitReachedException"/> class.</summary>
    public DownloadLimitReachedException()
        : base("Today's subtitle download limit is reached; searching continues tomorrow.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="DownloadLimitReachedException"/> class.</summary>
    /// <param name="message">Message.</param>
    public DownloadLimitReachedException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="DownloadLimitReachedException"/> class.</summary>
    /// <param name="message">Message.</param>
    /// <param name="innerException">Cause.</param>
    public DownloadLimitReachedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Finds a subtitle for a video that has none in a language: candidates from the sources are ranked, the best few are
/// downloaded one at a time and checked against the audio (the same two stages as existing subtitles), and the first
/// that clearly fits is saved beside the video, with its timing corrected and the automatic clean-up applied. It never
/// replaces an existing file, and undo deletes what it added.
/// </summary>
public sealed class SubtitleFinder
{
    /// <summary>How many candidates are downloaded and checked at most per video.</summary>
    public const int MaxTries = 3;

    /// <summary>How long to wait before searching again for a video nothing fitted.</summary>
    public static readonly TimeSpan SearchAgainAfter = TimeSpan.FromDays(30);

    /// <summary>A fit this confident ends the search without trying the other candidates (saves downloads).</summary>
    public const double GoodEnough = 0.6;

    private readonly ResultStore _results;
    private readonly DownloadLedger _ledger;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleFinder"/> class.
    /// </summary>
    /// <param name="results">Where results are kept.</param>
    /// <param name="ledger">The daily download count.</param>
    /// <param name="clock">Clock.</param>
    public SubtitleFinder(ResultStore results, DownloadLedger ledger, TimeProvider? clock = null)
    {
        _results = results ?? throw new ArgumentNullException(nameof(results));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// The result id for a video and language.
    /// </summary>
    /// <param name="videoPath">The video.</param>
    /// <param name="language">The language.</param>
    /// <returns>The id.</returns>
    public static string IdFor(string videoPath, string language) => "find-" + ResultStore.IdFor(videoPath + "|" + language);

    /// <summary>
    /// The file name for an added subtitle: the video's name, the two-letter language (Jellyfin's convention), a
    /// hearing-impaired flag if the subtitle has sound descriptions, and the format's extension.
    /// </summary>
    /// <param name="videoPath">The video.</param>
    /// <param name="language">The language (three-letter).</param>
    /// <param name="hearingImpaired">Whether it has sound descriptions.</param>
    /// <param name="format">The subtitle format.</param>
    /// <returns>The path.</returns>
    public static string PathFor(string videoPath, string language, bool hearingImpaired, SubtitleFormat format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        var ext = format switch { SubtitleFormat.WebVtt => ".vtt", SubtitleFormat.Ass => ".ass", _ => ".srt" };
        var stem = Path.Combine(Path.GetDirectoryName(videoPath)!, Path.GetFileNameWithoutExtension(videoPath));
        return stem + "." + (Languages.ToTwoLetter(language) ?? language) + (hearingImpaired ? ".sdh" : string.Empty) + ext;
    }

    /// <summary>
    /// Whether to search for a video: never searched, or nothing fitted long enough ago. An added subtitle, or one someone
    /// removed with undo, is never searched for again on its own.
    /// </summary>
    /// <param name="job">The video and language.</param>
    /// <returns><c>true</c> to search.</returns>
    public bool NeedsSearch(FindJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return _results.Get(IdFor(job.VideoPath, job.Language)) is not { } r
            || (r.Status is ResultStatus.NotFound or ResultStatus.Failed && _clock.GetUtcNow() - r.Time >= SearchAgainAfter);
    }

    /// <summary>
    /// Searches, checks and (if something fits) adds a subtitle.
    /// </summary>
    /// <param name="job">The video and language.</param>
    /// <param name="source">Where candidates come from.</param>
    /// <param name="audio">The video's audio.</param>
    /// <param name="speech">Speech-to-text, if available (free services only for automatic runs).</param>
    /// <param name="policies">The settings (clean-up that is automatic is applied to the new file).</param>
    /// <param name="downloadsPerDay">The day's download allowance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result.</returns>
    /// <exception cref="DownloadLimitReachedException">No downloads left today (nothing is recorded, so it is tried again).</exception>
    public async Task<SubtitleResult> FindAsync(FindJob job, ICandidateSource source, IAudioSource audio, ISpeechToText? speech, Policies policies, int downloadsPerDay, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(policies);

        var result = new SubtitleResult
        {
            Id = IdFor(job.VideoPath, job.Language),
            ItemId = job.ItemId,
            Name = job.Name,
            SubtitlePath = PathFor(job.VideoPath, job.Language, false, SubtitleFormat.Srt),
            Time = _clock.GetUtcNow(),
            Version = SubtitleProcessor.CurrentVersion,
        };

        var candidates = await source.SearchAsync(job.ItemId, job.Language, cancellationToken).ConfigureAwait(false);
        var preference = policies.Cleanup.RemoveSoundDescriptions ? HearingImpairedPreference.Avoid : HearingImpairedPreference.Neutral;
        var ranked = CandidateScorer.Rank(job.Video, candidates, preference).Where(c => !c.Rejected).Take(MaxTries).ToList();
        if (ranked.Count == 0)
        {
            return Save(result with
            {
                Status = ResultStatus.NotFound,
                Explanation = candidates.Count == 0 ? "No subtitles offered for this video." : string.Create(CultureInfo.InvariantCulture, $"{candidates.Count} offered, none suitable (wrong episode, forced-only or an unsupported format)."),
            });
        }

        var notes = new List<string>();
        (ScoredCandidate Candidate, SubtitleDocument Document, SyncOutcome Outcome, double Fit)? best = null;
        foreach (var candidate in ranked)
        {
            if (!_ledger.TryTake(downloadsPerDay))
            {
                if (best is null)
                {
                    throw new DownloadLimitReachedException();
                }

                break;
            }

            var fetched = await source.FetchAsync(candidate.Candidate, cancellationToken).ConfigureAwait(false);
            var document = fetched is null ? null : SubtitleReader.Read(fetched.Content.Span, "download." + fetched.Format.Trim('.'));
            if (document is null || document.Cues.Count == 0)
            {
                notes.Add(candidate.Candidate.ReleaseName + ": couldn't be downloaded or read");
                continue;
            }

            var content = ContentChecks.Assess(job.Video, document, job.Language);
            if (content.Rejected)
            {
                notes.Add(candidate.Candidate.ReleaseName + ": " + string.Join(", ", content.Reasons));
                continue;
            }

            var outcome = await new SyncCheck(audio, speech, refine: speech is not null, matcher: policies.Matcher)
                .RunAsync(document, job.Duration, Languages.ToTwoLetter(job.Language), cancellationToken).ConfigureAwait(false);
            if (outcome.WrongLanguageSuspected || outcome.Model.Status == SyncStatus.Unreliable)
            {
                notes.Add(candidate.Candidate.ReleaseName + ": doesn't line up with the audio");
                continue;
            }

            var fit = outcome.Model.Confidence + candidate.Score + content.Adjustment;
            if (best is null || fit > best.Value.Fit)
            {
                best = (candidate, document, outcome, fit);
            }

            if (outcome.Model.Confidence >= GoodEnough)
            {
                break;
            }
        }

        if (best is not { } b)
        {
            return Save(result with
            {
                Status = ResultStatus.NotFound,
                Explanation = "No candidate fitted the audio: " + string.Join("; ", notes) + ".",
            });
        }

        // A new file: the timing correction and the automatic clean-up are applied (nothing existing is changed)
        var model = b.Outcome.Model;
        var timed = model.Status == SyncStatus.Corrected ? b.Document.Retime(model.Map) : b.Document;
        var (cleaned, applied) = SubtitleCleaner.Clean(timed, SubtitleProcessor.AutomaticOptions(policies));
        var path = PathFor(job.VideoPath, job.Language, b.Candidate.Candidate.HearingImpaired == true, cleaned.Format);
        if (File.Exists(path))
        {
            return Save(result with { Status = ResultStatus.NotFound, SubtitlePath = path, Explanation = "A subtitle file with that name already exists; nothing was added." });
        }

        var fingerprint = SubtitleFiles.Create(path, SubtitleWriter.ToBytes(cleaned));
        var timing = model.Status == SyncStatus.Corrected
            ? string.Create(CultureInfo.InvariantCulture, $"timing corrected by {model.Offset:+0.00;-0.00} s") + (Math.Abs(model.Scale - 1) > 1e-9 ? string.Create(CultureInfo.InvariantCulture, $" and a frame-rate change (×{model.Scale:0.00000})") : string.Empty)
            : "already in sync";
        return Save(result with
        {
            Status = ResultStatus.Added,
            SubtitlePath = path,
            Fingerprint = fingerprint,
            Changed = true,
            Scale = model.Scale,
            Offset = model.Status == SyncStatus.Corrected ? model.Offset : 0,
            Stage = b.Outcome.Stage,
            Confidence = model.Confidence,
            Origin = string.Create(CultureInfo.InvariantCulture, $"{b.Candidate.Candidate.Source}: {b.Candidate.Candidate.ReleaseName} (score {b.Candidate.Score:0.00})"),
            Cleaned = applied.GroupBy(c => c.Kind.ToString()).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            Explanation = "Added: " + timing + ". " + model.Explanation + (notes.Count > 0 ? " Skipped first: " + string.Join("; ", notes) + "." : string.Empty),
        });
    }

    private SubtitleResult Save(SubtitleResult result)
    {
        _results.Put(result);
        return result;
    }
}
