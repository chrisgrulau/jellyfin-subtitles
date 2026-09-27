using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Resilience;
using Jellyfin.Plugin.Subtitles.Audio;
using Jellyfin.Plugin.Subtitles.Discrepancy;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.Generation;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Jellyfin.Plugin.Subtitles.Sync;

namespace Jellyfin.Plugin.Subtitles.Pipeline;

/// <summary>
/// The fix by section: a subtitle whose timing the regular check couldn't settle (unclear, or settled by few agreeing
/// words) is fitted to a full transcript of its video piece by piece (see <see cref="PiecewiseAligner"/>). Subtitles made
/// for a different cut of the video (a scene added or removed, a recap or cold open, ad breaks, a frame-rate change as
/// well) jump part-way, so no single timing fits them. A fit becomes a correction waiting for review, never applied on its
/// own; lines covering a part the video doesn't have are flagged for removal, not moved. Transcripts are the whole-file
/// check's and the generator's (cached), so a fix after either costs nothing more.
/// </summary>
public sealed class SectionFixer
{
    /// <summary>The stage's name in results.</summary>
    public const string Stage = "timing by section";

    /// <summary>The start of the fix's part of an explanation.</summary>
    public const string Marker = " Timing by section:";

    /// <summary>Whether subtitles made for a different cut are fixed on their own at first (the setting's default: off).</summary>
    public const bool OnByDefault = false;

    /// <summary>
    /// A timing settled by speech-to-text with less agreement than this (the share of matched words agreeing, see
    /// <see cref="SubtitleResult.Confidence"/>) may be a different cut: sample snippets can agree while the timing jumps
    /// between them. Higher than the whole-file check's <see cref="WholeFileChecker.LowAgreement"/>, from the calibration.
    /// </summary>
    public const double PartialAgreement = 0.75;

    /// <summary>
    /// When the whole file agrees on one timing at the same frame rate, a shift smaller than this (seconds) isn't proposed:
    /// good subtitles sit within a few tenths of a second of the words (lines appear a moment early), and the regular
    /// check already settled that much.
    /// </summary>
    public const double OneTimingWithin = 0.5;

    /// <summary>How long a fix whose transcript failed waits before it is tried again.</summary>
    public static readonly TimeSpan RetryFailedAfter = TimeSpan.FromDays(3);

    private readonly ResultStore _results;
    private readonly TranscriptCache? _cache;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Initializes a new instance of the <see cref="SectionFixer"/> class.
    /// </summary>
    /// <param name="results">Where results are kept.</param>
    /// <param name="cache">Full transcripts kept for reuse (optional).</param>
    /// <param name="clock">Clock.</param>
    public SectionFixer(ResultStore results, TranscriptCache? cache = null, TimeProvider? clock = null)
    {
        _results = results ?? throw new ArgumentNullException(nameof(results));
        _cache = cache;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Gets whether any file waits for a fix by section someone asked for.</summary>
    public bool HasRequests => _results.All().Any(r => r.SectionFixRequested);

    /// <summary>Gets or sets the fit's settings (the calibrated defaults unless set, for tests).</summary>
    public PiecewiseOptions Options { get; set; } = new();

    /// <summary>
    /// Whether a result's timing is doubtful in the way a different cut makes it: unclear ("Unreliable"), or settled by
    /// speech-to-text with only partial agreement (see <see cref="PartialAgreement"/>). Not subtitles in another language or matched by
    /// meaning, generated or embedded ones, subtitles the search added (chosen among candidates for fitting), text that
    /// didn't decode cleanly, or a correction already declined or undone.
    /// </summary>
    /// <param name="r">The result.</param>
    /// <returns><c>true</c> if a fix by section may help.</returns>
    public static bool IsCandidate(SubtitleResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.Stage == SyncCheck.ByMeaningStage || r.Stage == Stage || r.Id.StartsWith(SubtitleGenerator.IdPrefix, StringComparison.Ordinal) || r.Id.StartsWith("emb-", StringComparison.Ordinal)
            || r.Explanation.Contains("didn't decode cleanly", StringComparison.Ordinal))
        {
            return false;
        }

        return r.Status == ResultStatus.Unreliable
            || (r.Status is ResultStatus.InSync or ResultStatus.Corrected or ResultStatus.Proposed && r.Stage == WholeFileChecker.SpeechStage && r.Confidence < PartialAgreement);
    }

    /// <summary>
    /// Why a subtitle can't be fixed by section, by the same rules the nightly run uses: those of the whole-file check (a
    /// subtitle file beside its video, not generated or matched by meaning, in a wanted language that is the audio's), and
    /// a timing the regular check left unclear or settled with few agreeing words.
    /// </summary>
    /// <param name="r">Its result.</param>
    /// <param name="job">The subtitle as the library lists it, or <c>null</c> if the library doesn't list it.</param>
    /// <param name="wanted">The wanted languages, in order.</param>
    /// <returns>The reason, in plain words, or <c>null</c> when it can be tried.</returns>
    public static string? Ineligible(SubtitleResult r, SubtitleJob? job, IReadOnlyList<string> wanted)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (WholeFileChecker.Ineligible(r, job, wanted) is { } why)
        {
            return why.Replace("checked against a full transcript", "fixed by section", StringComparison.Ordinal);
        }

        return IsCandidate(r) ? null : "Only a subtitle whose timing the check left unclear, or settled with few matching words, is fixed by section.";
    }

    /// <summary>
    /// The result a subtitle file's fix belongs to (see <see cref="WholeFileChecker.ResultFor"/>).
    /// </summary>
    /// <param name="subtitlePath">The subtitle file.</param>
    /// <returns>The result, or <c>null</c>.</returns>
    public SubtitleResult? ResultFor(string subtitlePath)
    {
        ArgumentNullException.ThrowIfNull(subtitlePath);
        return _results.Get(ResultStore.IdFor(subtitlePath))
            ?? (_results.ForPath(subtitlePath) is { } r && r.Id.StartsWith("find-", StringComparison.Ordinal) ? r : null);
    }

    /// <summary>
    /// The files to fix tonight: those asked for first, then (with <paramref name="automatic"/>) candidates not tried yet
    /// (see <see cref="IsCandidate"/>), in the audio's language, the longest waiting first; at most <paramref name="max"/>.
    /// </summary>
    /// <param name="jobs">The subtitle files beside the library's videos.</param>
    /// <param name="wantedFor">The wanted languages for a subtitle's video, in order.</param>
    /// <param name="automatic">Whether candidates are fixed without being asked for.</param>
    /// <param name="max">The most to take.</param>
    /// <returns>The files, in order.</returns>
    public IReadOnlyList<SubtitleJob> Choose(IEnumerable<SubtitleJob> jobs, Func<SubtitleJob, IReadOnlyList<string>> wantedFor, bool automatic, int max)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(wantedFor);
        var take = Math.Clamp(max, 0, WholeFileChecker.MaxPerNight);
        if (take == 0)
        {
            return [];
        }

        var now = _clock.GetUtcNow();
        var chosen = new List<(SubtitleJob Job, bool Asked, DateTimeOffset Time)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var job in jobs)
        {
            if (SubtitleGenerator.IsGenerated(job.SubtitlePath) || !seen.Add(job.SubtitlePath) || ResultFor(job.SubtitlePath) is not { } r)
            {
                continue;
            }

            if (r.SectionFixRequested)
            {
                chosen.Add((job, true, r.Time));
            }
            else if (automatic && IsCandidate(r) && WholeFileChecker.SameLanguage(job, wantedFor(job))
                && (r.SectionFix is null || (r.SectionFix.Failed && now - r.SectionFix.Time >= RetryFailedAfter)))
            {
                chosen.Add((job, false, r.Time));
            }
        }

        return [.. chosen.OrderByDescending(c => c.Asked).ThenBy(c => c.Time).ThenBy(c => c.Job.SubtitlePath, StringComparer.Ordinal).Take(take).Select(c => c.Job)];
    }

    /// <summary>
    /// Clears the queue flag of files asked for that the run can't fix (the settings, the library or the result changed
    /// since), noting why in their results.
    /// </summary>
    /// <param name="jobs">The subtitle files beside the library's videos.</param>
    /// <param name="wantedFor">The wanted languages for a subtitle's video, in order.</param>
    /// <returns>How many were cleared.</returns>
    public int ClearUnreachable(IEnumerable<SubtitleJob> jobs, Func<SubtitleJob, IReadOnlyList<string>> wantedFor)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(wantedFor);
        var byPath = new Dictionary<string, SubtitleJob>(StringComparer.Ordinal);
        foreach (var j in jobs)
        {
            byPath.TryAdd(j.SubtitlePath, j);
        }

        var cleared = 0;
        foreach (var r in _results.All().Where(r => r.SectionFixRequested).ToList())
        {
            var job = byPath.GetValueOrDefault(r.SubtitlePath);
            if (Ineligible(r, job, job is null ? [] : wantedFor(job)) is { } why)
            {
                Note(r, string.Empty, "not tried: " + why, false);
                cleared++;
            }
        }

        return cleared;
    }

    /// <summary>
    /// Fits one subtitle file to a full transcript of its video, section by section, and records what was found: a
    /// correction waiting for review, or why none is proposed. The transcript comes from the cache, or is made with
    /// <paramref name="speech"/> and cached; without a service (<c>null</c>) only a cached transcript is used.
    /// </summary>
    /// <param name="job">The subtitle.</param>
    /// <param name="audio">The video's audio (only read when there is no cached transcript), or <c>null</c>.</param>
    /// <param name="speech">The "Full transcript" speech-to-text service, or <c>null</c> to use a cached transcript only.</param>
    /// <param name="setup">The service and model (see <see cref="SubtitleGenerator.SetupOf"/>).</param>
    /// <param name="wanted">The wanted languages for the subtitle's video, in order.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated result, or <c>null</c> when there was nothing to do (or no cached transcript without a service).</returns>
    /// <exception cref="SpeechToTextException">The service refused because of a limit or its sign-in.</exception>
    public async Task<SubtitleResult?> FixAsync(SubtitleJob job, IAudioSource? audio, ISpeechToText? speech, string setup, IReadOnlyList<string> wanted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(wanted);
        if (SubtitleGenerator.IsGenerated(job.SubtitlePath) || ResultFor(job.SubtitlePath) is not { } r)
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(job.SubtitlePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(SubtitleFiles.Fingerprint(bytes), r.Fingerprint, StringComparison.Ordinal))
        {
            // Changed since it was checked: the nightly check sees it first
            return null;
        }

        if (Ineligible(r, job, wanted) is { } why)
        {
            return Note(r, setup, "not tried: " + why, false);
        }

        var document = SubtitleReader.Read(bytes, job.SubtitlePath);
        if (document is null || document.Cues.Count == 0)
        {
            return Note(r, setup, "not tried: it isn't a readable text subtitle.", false);
        }

        if (document.TextSuspect)
        {
            return Note(r, setup, "not tried: its text didn't decode cleanly.", false);
        }

        var language = Languages.ToTwoLetter(job.Language);
        var key = TranscriptCache.KeyForFile(job.VideoPath, job.AudioStream, language, setup);
        var full = _cache?.Get(key);
        var reused = full is not null;
        if (full is null)
        {
            if (speech is null || audio is null)
            {
                return null;
            }

            var limit = SubtitleGenerator.TimeLimit(job.Duration);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(limit);
            try
            {
                full = await FullTranscriber.TranscribeAsync(audio, job.Duration, speech, language, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Note(r, setup, string.Create(CultureInfo.InvariantCulture, $"not tried: transcribing took longer than {limit.TotalMinutes:0} minutes and was stopped. Tried again in 3 days."), true);
            }
            catch (SpeechToTextException ex) when (ex.Failure is not (FailureClass.ProviderLimit or FailureClass.Authentication))
            {
                return Note(r, setup, "not tried: " + ex.Message + " Tried again in 3 days.", true);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
            {
                return Note(r, setup, "not tried: the audio couldn't be read: " + ex.Message + " Tried again in 3 days.", true);
            }

            _cache?.Put(key, full);
        }

        var service = SubtitleGenerator.ServiceName(full.Provider) + (string.IsNullOrEmpty(full.Model) || full.Model == "default" ? string.Empty : " (" + full.Model + ")") + (reused ? ", transcript reused" : string.Empty);
        var fit = PiecewiseAligner.Fit(document, full.Words, job.Duration > TimeSpan.Zero ? job.Duration.TotalSeconds : null, Options);
        return Record(r, document, fit, setup, service);
    }

    /// <summary>
    /// Records a fit on a result: a correction waiting for review (with the lines not in the video flagged), or a note
    /// saying why none is proposed. Nothing is written to the subtitle file.
    /// </summary>
    /// <param name="r">The result.</param>
    /// <param name="document">The subtitle as it is now.</param>
    /// <param name="fit">The fit.</param>
    /// <param name="setup">The transcript's service and model.</param>
    /// <param name="service">The service, for people.</param>
    /// <returns>The updated result.</returns>
    public SubtitleResult Record(SubtitleResult r, SubtitleDocument document, PiecewiseFit fit, string setup, string service)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(fit);
        var by = " (full transcript by " + service + ")";
        if (fit.Status == PiecewiseStatus.Rejected)
        {
            return Note(r, setup, "no fix proposed" + by + ": " + fit.Explanation, false);
        }

        var one = fit.Sections[0];
        var inSync = fit.Status == PiecewiseStatus.OneTiming && Math.Abs(fit.Scale - 1) < 1e-9 && Math.Abs(one.Offset) < OneTimingWithin && fit.NotInVideo.Count == 0;
        if (inSync)
        {
            return Note(r, setup, "no fix needed" + by + ": " + fit.Explanation, false);
        }

        var flagged = fit.NotInVideo.Select(i => new LineFinding(document.Cues[i].Start.TotalSeconds, document.Cues[i].Text, null, DiscrepancyReview.NotInVideo, "Nothing like it is heard where it would be after the jump: it covers a part this cut of the video doesn't have. Remove it, or decline to keep it.") { From = DiscrepancyReview.Sections }).ToList();
        var summary = (fit.Status == PiecewiseStatus.Sections ? fit.Explanation : "one timing fits the whole file: " + fit.Explanation) + by + " — waiting for review.";
        var sections = fit.Status == PiecewiseStatus.Sections ? fit.Sections : null;
        return Save(r with
        {
            Status = ResultStatus.Proposed,
            Scale = fit.Scale,
            Offset = one.Offset,
            Sections = sections,
            Stage = Stage,
            Confidence = fit.Anchors == 0 ? 0 : Math.Round(fit.Agreeing / (double)fit.Anchors, 3),
            Findings = [.. r.Findings.Where(f => !DiscrepancyReview.IsSection(f)), .. flagged],
            SectionFix = new SectionFixCheck { Time = _clock.GetUtcNow(), Setup = setup, Summary = summary },
            SectionFixRequested = false,
            Time = _clock.GetUtcNow(),
            Examples = [.. flagged.Select(DiscrepancyReview.Describe).Concat(r.Examples).Take(SubtitleProcessor.MaxExamples)],
            Explanation = Without(r.Explanation) + Marker + " " + summary,
        });
    }

    /// <summary>
    /// Fixes the chosen files in turn. No new file is started once <paramref name="budget"/> has passed since
    /// <paramref name="began"/>; a service limit or sign-in failure stops the run; anything else is recorded for that file.
    /// </summary>
    /// <param name="jobs">The files (see <see cref="Choose"/>).</param>
    /// <param name="audioFor">Each video's audio.</param>
    /// <param name="speech">The "Full transcript" speech-to-text service.</param>
    /// <param name="setup">The service and model.</param>
    /// <param name="wantedFor">The wanted languages for a subtitle's video, in order.</param>
    /// <param name="budget">The night's time budget, or <c>null</c> for none.</param>
    /// <param name="began">When the night's run began (the budget counts from there), or <c>null</c> for now.</param>
    /// <param name="recorded">Told about each result recorded.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the run did.</returns>
    public async Task<SectionRun> RunAsync(IReadOnlyList<SubtitleJob> jobs, Func<SubtitleJob, IAudioSource> audioFor, ISpeechToText speech, string setup, Func<SubtitleJob, IReadOnlyList<string>> wantedFor, TimeSpan? budget, DateTimeOffset? began, Action<SubtitleJob, SubtitleResult>? recorded, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(audioFor);
        ArgumentNullException.ThrowIfNull(wantedFor);
        var start = began ?? _clock.GetUtcNow();
        int tried = 0, proposed = 0, failed = 0;
        for (var i = 0; i < jobs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (budget is { } b && _clock.GetUtcNow() - start >= b)
            {
                return new SectionRun(tried, proposed, failed, jobs.Count - i, b, null);
            }

            var job = jobs[i];
            SubtitleResult? result;
            try
            {
                result = await FixAsync(job, audioFor(job), speech, setup, wantedFor(job), cancellationToken).ConfigureAwait(false);
            }
            catch (SpeechToTextException ex)
            {
                return new SectionRun(tried, proposed, failed, jobs.Count - i, null, ex.Message);
            }
#pragma warning disable CA1031 // One odd file mustn't stop the nightly run: it is recorded and tried again later
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                result = ResultFor(job.SubtitlePath) is { } r ? Note(r, setup, "not tried: " + ex.GetType().Name + ": " + ex.Message, true) : null;
            }

            if (result is not null)
            {
                if (result.SectionFix?.Failed == true)
                {
                    failed++;
                }
                else
                {
                    tried++;
                    proposed += result.Status == ResultStatus.Proposed && result.Stage == Stage ? 1 : 0;
                }

                recorded?.Invoke(job, result);
            }
        }

        return new SectionRun(tried, proposed, failed, 0, null, null);
    }

    /// <summary>
    /// Writes results still waiting to be saved (at the end of a run).
    /// </summary>
    public void FlushResults() => _results.Flush();

    private static string Without(string explanation)
    {
        var at = explanation.IndexOf(Marker, StringComparison.Ordinal);
        return at >= 0 ? explanation[..at] : explanation;
    }

    private SubtitleResult Note(SubtitleResult r, string setup, string summary, bool failed)
        => Save(r with
        {
            SectionFix = new SectionFixCheck { Time = _clock.GetUtcNow(), Setup = setup, Summary = summary, Failed = failed },
            SectionFixRequested = false,
            Explanation = Without(r.Explanation) + Marker + " " + summary,
        });

    private SubtitleResult Save(SubtitleResult result)
    {
        _results.Put(result);
        return result;
    }
}

/// <summary>
/// What one night's fixes by section did.
/// </summary>
/// <param name="Tried">Files fitted (or found not fixable).</param>
/// <param name="Proposed">Files with a correction waiting for review.</param>
/// <param name="Failed">Files whose transcript failed.</param>
/// <param name="Left">Files chosen but not started (left for the next night).</param>
/// <param name="OutOfTime">The time budget, when the run stopped because it was used up.</param>
/// <param name="StoppedBy">Why the service stopped the run (a limit or sign-in), if it did.</param>
public sealed record SectionRun(int Tried, int Proposed, int Failed, int Left, TimeSpan? OutOfTime, string? StoppedBy)
{
    /// <summary>
    /// The run's summary line, for example "tried 3 subtitles made for a different cut; 1 correction waiting for review; 0 failed".
    /// </summary>
    /// <returns>The line.</returns>
    public string Summary()
    {
        var line = string.Create(CultureInfo.InvariantCulture, $"tried fixing the timing of {Tried} subtitle file{(Tried == 1 ? string.Empty : "s")} by section; {Proposed} {(Proposed == 1 ? "correction waits" : "corrections wait")} for review; {Failed} failed");
        if (OutOfTime is { } budget)
        {
            line += string.Create(CultureInfo.InvariantCulture, $"; stopped after {budget.TotalHours:0.#} h; {Left} left for tomorrow");
        }
        else if (StoppedBy is not null)
        {
            line += string.Create(CultureInfo.InvariantCulture, $"; stopped by the service; {Left} left for tomorrow");
        }

        return line;
    }
}
