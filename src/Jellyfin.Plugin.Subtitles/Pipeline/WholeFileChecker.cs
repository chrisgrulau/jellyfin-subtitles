using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Resilience;
using Jellyfin.Plugin.Subtitles.Audio;
using Jellyfin.Plugin.Subtitles.Audit;
using Jellyfin.Plugin.Subtitles.Discrepancy;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.Generation;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Jellyfin.Plugin.Subtitles.Sync;

namespace Jellyfin.Plugin.Subtitles.Pipeline;

/// <summary>
/// What a night's whole-file checks use.
/// </summary>
/// <param name="Wanted">The wanted languages, in order (an untagged audio track is taken to be in the first).</param>
/// <param name="Tuned">Whether confidence thresholds are tuned automatically (see <see cref="ConfidenceCalibration"/>).</param>
/// <param name="Auditor">The AI plugin, to confirm lines whose wording differs (optional; within the run's AI checks).</param>
/// <param name="Options">The comparison's options, or <c>null</c> for the defaults.</param>
public sealed record WholeFileSettings(IReadOnlyList<string> Wanted, bool Tuned, ITextAuditor? Auditor = null, DiscrepancyOptions? Options = null);

/// <summary>
/// The whole-file check: a doubtful subtitle is compared with a full transcript of its video (the "Full transcript"
/// speech-to-text, as for generated subtitles), and the lines that differ in ways that matter wait for review (see
/// <see cref="DiscrepancyFinder"/>); nothing is changed on its own. Only doubtful subtitles are checked (an unclear
/// timing, a timing settled by few agreeing words, or wording the audit flagged) and those picked in the results,
/// a few a night; generated subtitles and subtitles in another language than the audio's are left alone. Transcripts
/// are cached, so a second check, or generating from the same video, costs nothing.
/// </summary>
public sealed class WholeFileChecker
{
    /// <summary>The most files checked in one night that can be set.</summary>
    public const int MaxPerNight = 200;

    /// <summary>A timing settled by speech-to-text with less confidence than this (few agreeing words) is doubtful.</summary>
    public const double LowAgreement = 0.5;

    /// <summary>The most findings kept for one file (the counts cover them all).</summary>
    public const int MaxFindings = 50;

    /// <summary>The most differing lines the AI plugin is asked to confirm for one file.</summary>
    public const int MaxConfirmed = 30;

    /// <summary>The start of the whole-file check's part of an explanation.</summary>
    public const string Marker = " Whole file checked";

    /// <summary>The speech-to-text stage's name in results.</summary>
    public const string SpeechStage = "speech-to-text";

    /// <summary>How long a check whose transcript failed waits before it is tried again.</summary>
    public static readonly TimeSpan RetryFailedAfter = TimeSpan.FromDays(3);

    private readonly ResultStore _results;
    private readonly TranscriptCache? _cache;
    private readonly ConfidenceCalibration? _calibration;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Initializes a new instance of the <see cref="WholeFileChecker"/> class.
    /// </summary>
    /// <param name="results">Where results are kept.</param>
    /// <param name="cache">Full transcripts kept for reuse (optional).</param>
    /// <param name="calibration">Confidence thresholds per service (optional; the documented floors otherwise).</param>
    /// <param name="clock">Clock.</param>
    public WholeFileChecker(ResultStore results, TranscriptCache? cache = null, ConfidenceCalibration? calibration = null, TimeProvider? clock = null)
    {
        _results = results ?? throw new ArgumentNullException(nameof(results));
        _cache = cache;
        _calibration = calibration;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Gets whether any file waits for a whole-file check someone asked for.</summary>
    public bool HasRequests => _results.All().Any(r => r.WholeFileRequested);

    /// <summary>
    /// Whether a result is doubtful enough for a whole-file check: its timing is unclear, was settled by speech-to-text
    /// with few agreeing words, or the wording audit flagged lines. Subtitles matched by meaning (translations) never are.
    /// </summary>
    /// <param name="r">The result.</param>
    /// <returns><c>true</c> if doubtful.</returns>
    public static bool IsDoubtful(SubtitleResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.Stage == SyncCheck.ByMeaningStage || r.Id.StartsWith(SubtitleGenerator.IdPrefix, StringComparison.Ordinal) || r.Id.StartsWith("emb-", StringComparison.Ordinal))
        {
            return false;
        }

        return r.Status == ResultStatus.Unreliable
            || r.Findings.Any(f => f.From is null)
            || (r.Status is ResultStatus.InSync or ResultStatus.Corrected or ResultStatus.Proposed or ResultStatus.Added && r.Stage == SpeechStage && r.Confidence < LowAgreement);
    }

    /// <summary>
    /// Whether the audio is in the subtitle's language (only then can what is heard be compared with it): the audio
    /// track's tag, or, for a track without one, the first wanted language.
    /// </summary>
    /// <param name="job">The subtitle.</param>
    /// <param name="wanted">The wanted languages, in order.</param>
    /// <returns><c>true</c> if the same.</returns>
    public static bool SameLanguage(SubtitleJob job, IReadOnlyList<string> wanted)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(wanted);
        if (Languages.ToTwoLetter(job.Language) is not { } language)
        {
            return false;
        }

        var audio = string.IsNullOrWhiteSpace(job.AudioLanguage) || job.AudioLanguage.Trim() is "und" or "unk" or "mis" or "zxx" ? null : job.AudioLanguage;
        return audio is not null
            ? string.Equals(Languages.ToTwoLetter(audio), language, StringComparison.Ordinal)
            : wanted.Count > 0 && string.Equals(Languages.ToTwoLetter(wanted[0]), language, StringComparison.Ordinal);
    }

    /// <summary>
    /// The result a subtitle file's whole-file check belongs to: the file's own, or the search's for a subtitle it added.
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
    /// The files to check tonight: those asked for first, then (with <paramref name="automatic"/>) doubtful ones not
    /// checked whole yet, in the audio's language, the longest waiting first; at most <paramref name="max"/>. Generated
    /// subtitles never are.
    /// </summary>
    /// <param name="jobs">The subtitle files beside the library's videos.</param>
    /// <param name="wanted">The wanted languages, in order.</param>
    /// <param name="automatic">Whether doubtful subtitles are checked without being asked for.</param>
    /// <param name="max">The most to take (0 to <see cref="MaxPerNight"/>).</param>
    /// <returns>The files, in order.</returns>
    public IReadOnlyList<SubtitleJob> Choose(IEnumerable<SubtitleJob> jobs, IReadOnlyList<string> wanted, bool automatic, int max)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(wanted);
        var take = Math.Clamp(max, 0, MaxPerNight);
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

            if (r.WholeFileRequested)
            {
                chosen.Add((job, true, r.Time));
            }
            else if (automatic && IsDoubtful(r) && SameLanguage(job, wanted)
                && (r.WholeFile is null || (r.WholeFile.Failed && now - r.WholeFile.Time >= RetryFailedAfter)))
            {
                chosen.Add((job, false, r.Time));
            }
        }

        return [.. chosen.OrderByDescending(c => c.Asked).ThenBy(c => c.Time).ThenBy(c => c.Job.SubtitlePath, StringComparer.Ordinal).Take(take).Select(c => c.Job)];
    }

    /// <summary>
    /// Compares one subtitle file with a full transcript of its video (from the cache, or transcribed and then cached)
    /// and records the lines that differ, waiting for review. A file that changed since its result, or that isn't in the
    /// audio's language, isn't compared.
    /// </summary>
    /// <param name="job">The subtitle.</param>
    /// <param name="audio">The video's audio.</param>
    /// <param name="speech">The "Full transcript" speech-to-text service.</param>
    /// <param name="setup">The service and model (see <see cref="SubtitleGenerator.SetupOf"/>).</param>
    /// <param name="settings">The night's settings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated result, or <c>null</c> when there was nothing to check.</returns>
    /// <exception cref="SpeechToTextException">The service refused because of a limit or its sign-in.</exception>
    public async Task<SubtitleResult?> CheckAsync(SubtitleJob job, IAudioSource audio, ISpeechToText speech, string setup, WholeFileSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(speech);
        ArgumentNullException.ThrowIfNull(settings);
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

        if (!SameLanguage(job, settings.Wanted))
        {
            return Note(r, setup, "not compared: the audio is in another language than the subtitle's, and only speech in the subtitle's own language can be compared with it.", false);
        }

        var document = SubtitleReader.Read(bytes, job.SubtitlePath);
        if (document is null || document.Cues.Count == 0)
        {
            return Note(r, setup, "not compared: it isn't a readable text subtitle.", false);
        }

        if (document.TextSuspect)
        {
            return Note(r, setup, "not compared: its text didn't decode cleanly.", false);
        }

        var language = Languages.ToTwoLetter(job.Language);
        var key = TranscriptCache.KeyForFile(job.VideoPath, job.AudioStream, language, setup);
        var full = _cache?.Get(key);
        var reused = full is not null;
        if (full is null)
        {
            var limit = SubtitleGenerator.TimeLimit(job.Duration);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(limit);
            try
            {
                full = await FullTranscriber.TranscribeAsync(audio, job.Duration, speech, language, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Note(r, setup, string.Create(CultureInfo.InvariantCulture, $"not compared: transcribing took longer than {limit.TotalMinutes:0} minutes and was stopped. Tried again in 3 days."), true);
            }
            catch (SpeechToTextException ex) when (ex.Failure is not (FailureClass.ProviderLimit or FailureClass.Authentication))
            {
                return Note(r, setup, "not compared: " + ex.Message + " Tried again in 3 days.", true);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
            {
                return Note(r, setup, "not compared: the audio couldn't be read: " + ex.Message + " Tried again in 3 days.", true);
            }

            _cache?.Put(key, full);
        }

        var floor = ConfidenceCalibration.FloorFor(full.Provider);
        var threshold = _calibration?.ThresholdFor(full.Provider, full.Model, settings.Tuned) ?? floor;
        var (toAudio, toFile) = Clocks(r);
        var options = (settings.Options ?? new DiscrepancyOptions()) with { MinConfidence = threshold, Language = language };
        var report = DiscrepancyFinder.Find(document, full.Words, toAudio, options);
        var service = SubtitleGenerator.ServiceName(full.Provider) + (string.IsNullOrEmpty(full.Model) || full.Model == "default" ? string.Empty : " (" + full.Model + ")") + (reused ? ", transcript reused" : string.Empty);
        if (report.Problem is not null)
        {
            return Note(r, setup, $"against a full transcript by {service}: {report.Problem}", false, report.Counts);
        }

        var found = report.Findings;
        var confirmed = string.Empty;
        if (settings.Auditor is not null)
        {
            (found, confirmed) = await ConfirmAsync(settings.Auditor, found, document, language, cancellationToken).ConfigureAwait(false);
        }

        var counts = found.GroupBy(f => f.Kind).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var findings = found.Take(MaxFindings).Select(d => DiscrepancyReview.ToFinding(d, document, toFile)).ToList();
        var left = report.Suppressed > 0
            ? string.Create(CultureInfo.InvariantCulture, $" {report.Suppressed} other difference{(report.Suppressed == 1 ? " was" : "s were")} left out: heard with a confidence under {ConfidenceCalibration.Describe(threshold, floor)}.")
            : string.Empty;
        var summary = findings.Count == 0
            ? $"against a full transcript by {service}: no lines differ in ways that matter.{left}{confirmed}"
            : $"against a full transcript by {service}: {string.Join(", ", Kinds(counts))} — waiting for review{(found.Count > MaxFindings ? string.Create(CultureInfo.InvariantCulture, $" (the first {MaxFindings} are listed)") : string.Empty)}. Apply or decline each line, or Apply for every suggested fix.{left}{confirmed}";
        var earlier = r.Findings.Where(DiscrepancyReview.IsWholeFile).Select(DiscrepancyReview.Describe).ToHashSet(StringComparer.Ordinal);
        return Save(r with
        {
            Findings = [.. r.Findings.Where(f => !DiscrepancyReview.IsWholeFile(f)), .. findings],
            WholeFile = new WholeFileCheck { Time = _clock.GetUtcNow(), Setup = setup, Counts = counts, Summary = summary },
            WholeFileRequested = false,
            Time = _clock.GetUtcNow(),
            Examples = [.. findings.Select(DiscrepancyReview.Describe).Concat(r.Examples.Where(e => !earlier.Contains(e))).Take(SubtitleProcessor.MaxExamples)],
            Explanation = Without(r.Explanation) + Marker + " " + summary,
        });
    }

    /// <summary>
    /// Checks the chosen files in turn. No new file is started once <paramref name="budget"/> has passed since
    /// <paramref name="began"/>; a service limit or sign-in failure stops the run; anything else is recorded for that file.
    /// </summary>
    /// <param name="jobs">The files (see <see cref="Choose"/>).</param>
    /// <param name="audioFor">Each video's audio.</param>
    /// <param name="speech">The "Full transcript" speech-to-text service.</param>
    /// <param name="setup">The service and model.</param>
    /// <param name="settings">The night's settings.</param>
    /// <param name="budget">The night's time budget, or <c>null</c> for none.</param>
    /// <param name="began">When the night's run began (the budget counts from there), or <c>null</c> for now.</param>
    /// <param name="recorded">Told about each result recorded.</param>
    /// <param name="progress">Progress, 0 to 100.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the run did.</returns>
    public async Task<WholeFileRun> RunAsync(IReadOnlyList<SubtitleJob> jobs, Func<SubtitleJob, IAudioSource> audioFor, ISpeechToText speech, string setup, WholeFileSettings settings, TimeSpan? budget, DateTimeOffset? began, Action<SubtitleJob, SubtitleResult>? recorded, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(audioFor);
        var start = began ?? _clock.GetUtcNow();
        int checkedCount = 0, flagged = 0, failed = 0;
        for (var i = 0; i < jobs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (budget is { } b && _clock.GetUtcNow() - start >= b)
            {
                return new WholeFileRun(checkedCount, flagged, failed, jobs.Count - i, b, null);
            }

            var job = jobs[i];
            SubtitleResult? result;
            try
            {
                result = await CheckAsync(job, audioFor(job), speech, setup, settings, cancellationToken).ConfigureAwait(false);
            }
            catch (SpeechToTextException ex)
            {
                return new WholeFileRun(checkedCount, flagged, failed, jobs.Count - i, null, ex.Message);
            }
#pragma warning disable CA1031 // One odd file mustn't stop the nightly run: it is recorded and tried again later
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                result = ResultFor(job.SubtitlePath) is { } r ? Note(r, setup, "not compared: " + ex.GetType().Name + ": " + ex.Message, true) : null;
            }

            if (result is not null)
            {
                if (result.WholeFile?.Failed == true)
                {
                    failed++;
                }
                else
                {
                    checkedCount++;
                    flagged += result.Findings.Any(DiscrepancyReview.IsWholeFile) ? 1 : 0;
                }

                recorded?.Invoke(job, result);
            }

            progress?.Report(100.0 * (i + 1) / jobs.Count);
        }

        return new WholeFileRun(checkedCount, flagged, failed, 0, null, null);
    }

    /// <summary>
    /// Writes results still waiting to be saved (at the end of a run).
    /// </summary>
    public void FlushResults() => _results.Flush();

    /// <summary>
    /// The clocks of a result's file and its audio: a correction waiting for review hasn't been applied, so the file's
    /// times are moved by it; otherwise they are the same.
    /// </summary>
    /// <param name="r">The result.</param>
    /// <returns>File to audio, and audio to file (seconds).</returns>
    public static (Func<double, double> ToAudio, Func<double, double> ToFile) Clocks(SubtitleResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.Status != ResultStatus.Proposed || r.Scale <= 0)
        {
            return (t => t, t => t);
        }

        double scale = r.Scale, offset = r.Offset;
        return (t => (scale * t) + offset, a => (a - offset) / scale);
    }

    private static IEnumerable<string> Kinds(Dictionary<string, int> counts)
        => DiscrepancyFinder.Kinds.Where(counts.ContainsKey).Select(k => DiscrepancyReview.KindName(k, counts[k]));

    // The line matcher's cousin: the AI plugin is asked whether each line flagged for its wording really differs in
    // meaning from what was heard; the lines it doesn't confirm are dropped. Missing lines and lines with nothing heard
    // aren't a matter of wording and are kept. No answer (not installed, allowance used up) keeps them all.
    private static async Task<(IReadOnlyList<LineDiscrepancy> Kept, string Note)> ConfirmAsync(ITextAuditor auditor, IReadOnlyList<LineDiscrepancy> found, SubtitleDocument file, string? language, CancellationToken cancellationToken)
    {
        var asked = found.Where(f => f.Cue >= 0 && f.Kind != DiscrepancyFinder.Extra).Take(MaxConfirmed).ToList();
        if (asked.Count == 0)
        {
            return (found, string.Empty);
        }

        var heard = asked.Select((d, i) => new HeardPhrase(i, Math.Round(d.AudioStart, 2), d.Heard.ReplaceLineEndings(" "))).ToList();
        var lines = asked.Select((d, i) => new AuditLine(i, Math.Round(d.AudioStart, 1), SubtitleMarkup.ToPlainText(file.Cues[d.Cue].Text).ReplaceLineEndings(" ").Trim())).ToList();
        var answer = await auditor.AuditAsync(heard, lines, language, cancellationToken).ConfigureAwait(false);
        if (answer.By is null)
        {
            return (found, " Not confirmed by the AI plugin" + (answer.Note.Length > 0 ? " (" + answer.Note.TrimEnd('.') + ")." : "."));
        }

        var yes = answer.Findings.Select(f => f.Line).Where(l => l >= 0 && l < asked.Count).ToHashSet();
        var dropped = asked.Where((_, i) => !yes.Contains(i)).ToHashSet();
        return ([.. found.Where(f => !dropped.Contains(f))], string.Create(CultureInfo.InvariantCulture, $" The AI plugin ({answer.By}) confirmed {yes.Count} of {asked.Count} lines whose wording differs; the others were left out."));
    }

    private static string Without(string explanation)
    {
        var at = explanation.IndexOf(Marker, StringComparison.Ordinal);
        return at >= 0 ? explanation[..at] : explanation;
    }

    private SubtitleResult Note(SubtitleResult r, string setup, string summary, bool failed, IReadOnlyDictionary<string, int>? counts = null)
        => Save(r with
        {
            Findings = [.. r.Findings.Where(f => !DiscrepancyReview.IsWholeFile(f))],
            WholeFile = new WholeFileCheck { Time = _clock.GetUtcNow(), Setup = setup, Counts = counts ?? new Dictionary<string, int>(), Summary = summary, Failed = failed },
            WholeFileRequested = false,
            Explanation = Without(r.Explanation) + Marker + " " + summary,
        });

    private SubtitleResult Save(SubtitleResult result)
    {
        _results.Put(result);
        return result;
    }
}

/// <summary>
/// What one night's whole-file checks did.
/// </summary>
/// <param name="Checked">Files compared (or found not comparable).</param>
/// <param name="Flagged">Files with lines waiting for review.</param>
/// <param name="Failed">Files whose transcript failed.</param>
/// <param name="Left">Files chosen but not started (left for the next night).</param>
/// <param name="OutOfTime">The time budget, when the run stopped because it was used up.</param>
/// <param name="StoppedBy">Why the service stopped the run (a limit or sign-in), if it did.</param>
public sealed record WholeFileRun(int Checked, int Flagged, int Failed, int Left, TimeSpan? OutOfTime, string? StoppedBy)
{
    /// <summary>
    /// The run's summary line, for example "checked 3 subtitle files whole; 1 has lines waiting for review; 0 failed".
    /// </summary>
    /// <returns>The line.</returns>
    public string Summary()
    {
        var line = string.Create(CultureInfo.InvariantCulture, $"checked {Checked} subtitle file{(Checked == 1 ? string.Empty : "s")} whole; {Flagged} {(Flagged == 1 ? "has" : "have")} lines waiting for review; {Failed} failed");
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
