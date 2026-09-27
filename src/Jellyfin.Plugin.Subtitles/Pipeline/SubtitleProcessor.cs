using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.Audio;
using Jellyfin.Plugin.Subtitles.Audit;
using Jellyfin.Plugin.Subtitles.Cleaning;
using Jellyfin.Plugin.Subtitles.Discrepancy;
using Jellyfin.Plugin.Subtitles.Configuration;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Jellyfin.Plugin.Subtitles.Sync;

namespace Jellyfin.Plugin.Subtitles.Pipeline;

/// <summary>
/// One subtitle file to check.
/// </summary>
/// <param name="ItemId">The library item.</param>
/// <param name="Name">The item's name, for display.</param>
/// <param name="VideoPath">The video file.</param>
/// <param name="SubtitlePath">The subtitle file beside it.</param>
/// <param name="Language">The subtitle's language (three-letter code), if known.</param>
/// <param name="Duration">The video's length.</param>
/// <param name="AudioStream">Which audio stream to listen to (counting audio streams only).</param>
/// <param name="AudioLanguage">That audio stream's language tag, if known.</param>
public sealed record SubtitleJob(Guid ItemId, string Name, string VideoPath, string SubtitlePath, string? Language, TimeSpan Duration, int AudioStream, string? AudioLanguage = null);

/// <summary>
/// The settings that decide what is applied and what waits for review.
/// </summary>
/// <param name="Timing">Timing corrections (shift, frame rate, overlaps, brief lines).</param>
/// <param name="Text">Wording changes (merged repeats, removed sound descriptions).</param>
/// <param name="Cleanup">Clean-up settings.</param>
/// <param name="Matcher">Pairs heard phrases with subtitle lines by meaning when exact words can't settle the timing (optional).</param>
/// <param name="Auditor">Audits the wording of subtitles whose timing is settled (optional).</param>
public sealed record Policies(ChangePolicy Timing, ChangePolicy Text, CleanupSettings Cleanup, Sync.ILineMatcher? Matcher = null, Audit.ITextAuditor? Auditor = null);

/// <summary>
/// Checks one subtitle and applies (or proposes) a timing correction according to the timing policy; applies or undoes
/// a proposal on request. Every change keeps the original (see <see cref="SubtitleFiles"/>).
/// </summary>
public sealed class SubtitleProcessor
{
    private readonly ResultStore _results;
    private readonly SubtitleFiles _files;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleProcessor"/> class.
    /// </summary>
    /// <param name="results">Where results are kept.</param>
    /// <param name="files">Safe file changes.</param>
    /// <param name="clock">Clock.</param>
    public SubtitleProcessor(ResultStore results, SubtitleFiles files, TimeProvider? clock = null)
    {
        _results = results ?? throw new ArgumentNullException(nameof(results));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Gets confidence thresholds learned from subtitles known to be good (optional): a subtitle found in sync by
    /// speech-to-text with nothing flagged adds its matched words (see <see cref="ConfidenceCalibration"/>).
    /// </summary>
    public ConfidenceCalibration? Calibration { get; init; }

    /// <summary>Gets whether a file exists (for tests; <see cref="File.Exists(string)"/> by default).</summary>
    public Func<string, bool> FileExists { get; init; } = File.Exists;

    /// <summary>Gets whether a folder exists (for tests; <see cref="Directory.Exists(string)"/> by default).</summary>
    public Func<string, bool> FolderExists { get; init; } = Directory.Exists;

    /// <summary>
    /// Gets or sets where to find the video of a result that stands for its video but doesn't record it (a search that
    /// found nothing, from before videos were recorded): Jellyfin's library, by the result's item. <c>null</c> when unknown.
    /// </summary>
    public Func<SubtitleResult, string?>? VideoLookup { get; set; }

    /// <summary>
    /// Whether a result is stale: its video or its subtitle file was replaced or removed (see <see cref="StaleResults"/>).
    /// </summary>
    /// <param name="r">The result.</param>
    /// <param name="fileExists">Whether a file exists (default <see cref="FileExists"/>; the results list passes a cached one).</param>
    /// <param name="folderExists">Whether a folder exists (default <see cref="FolderExists"/>).</param>
    /// <returns>Why it is stale, or <see cref="Staleness.None"/>.</returns>
    public Staleness StalenessOf(SubtitleResult r, Func<string, bool>? fileExists = null, Func<string, bool>? folderExists = null)
        => StaleResults.Check(r, fileExists ?? FileExists, folderExists ?? FolderExists, LookUpVideo);

    /// <summary>
    /// Clears a result if it is stale (its video, or its subtitle file, is gone): bulk jobs and the editor's audio clip use
    /// it before acting.
    /// </summary>
    /// <param name="id">Result id.</param>
    /// <param name="video">The video as the library has it now, if known (checked too).</param>
    /// <returns>Why it was cleared, or <c>null</c> when it wasn't (not stale, or no such result).</returns>
    public string? ClearIfStale(string id, string? video = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (_results.FindForRequest(id) is not { } r)
        {
            return null;
        }

        var why = Why(r, video);
        if (why is Staleness.SubtitleGone or Staleness.VideoGone)
        {
            _results.Remove(r.Id);
            return StaleResults.MessageFor(why);
        }

        return null;
    }

    /// <summary>
    /// Forgets the results of videos removed from the library whose file is gone (see <see cref="RemovedVideos"/>), and
    /// stale results in the folders of videos just added (a replacement filed under a new name).
    /// </summary>
    /// <param name="removed">Videos removed from the library.</param>
    /// <param name="folders">Folders a video was added to.</param>
    /// <returns>How many results were forgotten.</returns>
    public int DropForRemovedVideos(IReadOnlyCollection<RemovedVideo> removed, IReadOnlyCollection<string> folders)
    {
        ArgumentNullException.ThrowIfNull(removed);
        ArgumentNullException.ThrowIfNull(folders);
        var gone = removed.Where(v => !FileExists(v.Path)).ToList();
        if (gone.Count == 0 && folders.Count == 0)
        {
            return 0;
        }

        var inFolders = new HashSet<string>(folders, StringComparer.Ordinal);
        return _results.RemoveWhere(r => gone.Any(v => RemovedVideos.Concerns(r, v))
            || (RemovedVideos.InFolders(r, inFolders) && StaleResults.IsGone(r, FileExists, FolderExists)));
    }

    // What an action's result is: its files as the rule has them, and the video the library has now, if given
    private Staleness Why(SubtitleResult r, string? video)
    {
        var why = StalenessOf(r);
        if (why == Staleness.None && !string.IsNullOrEmpty(video) && !FileExists(video))
        {
            why = Path.GetDirectoryName(video) is { Length: > 0 } folder && FolderExists(folder) ? Staleness.VideoGone : Staleness.Unreachable;
        }

        return why;
    }

    private string? LookUpVideo(SubtitleResult r)
    {
        if (VideoLookup is not { } lookUp || !StaleResults.StandsForVideo(r))
        {
            return null;
        }

        try
        {
            return lookUp(r);
        }
#pragma warning disable CA1031 // The library not answering means only that the video isn't known
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    // Before an action: a stale result is cleared and the action refused with the reason (409); a file the action needs
    // that can't be reached, or that a result standing for its video doesn't have, refuses it without clearing anything
    private void Require(SubtitleResult r, bool subtitle = false, string? video = null)
    {
        var why = Why(r, video);
        if (why is Staleness.SubtitleGone or Staleness.VideoGone)
        {
            _results.Remove(r.Id);
            throw new StaleResultException(StaleResults.MessageFor(why));
        }

        if (why == Staleness.Unreachable && (subtitle || video is not null))
        {
            throw new InvalidOperationException(StaleResults.UnreachableMessage);
        }

        if (subtitle && !FileExists(r.SubtitlePath))
        {
            throw new InvalidOperationException(StaleResults.NoFileMessage);
        }
    }

    // A file that vanished between the check and the read or write goes the same way as one gone before: never an
    // unhandled exception
    private T Guard<T>(SubtitleResult r, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException
            || (ex is InvalidOperationException { InnerException: FileNotFoundException or DirectoryNotFoundException } && ex is not StaleResultException))
        {
            Require(r, subtitle: true);
            throw new InvalidOperationException(StaleResults.NoFileMessage, ex);
        }
    }

    private SubtitleResult Find(string id) => _results.FindForRequest(id) ?? throw new InvalidOperationException("No such result.");

    /// <summary>
    /// The latest results, newest first.
    /// </summary>
    /// <param name="limit">How many.</param>
    /// <returns>The results.</returns>
    public IReadOnlyList<SubtitleResult> Recent(int limit)
    {
        // Everything waiting for review comes first, however old, then the most recent (SUB-20)
        var all = _results.All();
        var waiting = all.Where(r => r.PendingReview).ToList();
        return [.. waiting, .. all.Where(r => !r.PendingReview).Take(Math.Clamp(limit, 1, 5000))];
    }

    /// <summary>
    /// Every result, in the order the page lists them: everything waiting for review first, however old, then the most
    /// recent.
    /// </summary>
    /// <returns>The results.</returns>
    public IReadOnlyList<SubtitleResult> Ordered()
    {
        var all = _results.All();
        return [.. all.Where(r => r.PendingReview), .. all.Where(r => !r.PendingReview)];
    }

    /// <summary>
    /// Writes results still waiting to be saved (at the end of a run).
    /// </summary>
    public void FlushResults() => _results.Flush();

    /// <summary>
    /// Drops results for subtitle files that were deleted (see <see cref="ResultStore.Prune"/>).
    /// </summary>
    /// <returns>How many were dropped.</returns>
    public int PruneGone() => _results.Prune(FileExists, FolderExists, LookUpVideo);

    /// <summary>The pipeline version: raised when a new stage is added, so files are checked once more.</summary>
    public const int CurrentVersion = 3;

    /// <summary>How many change examples a result keeps.</summary>
    public const int MaxExamples = 12;

    /// <summary>How long a failed check waits before it is tried again (unless the file changes).</summary>
    public static readonly TimeSpan RetryFailedAfter = TimeSpan.FromDays(3);

    /// <summary>How long a subtitle in a folder that couldn't be written waits before it is tried again.</summary>
    public static readonly TimeSpan RetryCantWriteAfter = TimeSpan.FromDays(30);

    /// <summary>The <see cref="SubtitleResult.Cleaned"/> key counting lines reworded from an audit.</summary>
    public const string RewordedKind = "RewordedFromAudit";

    /// <summary>
    /// Whether a subtitle needs checking: it hasn't been, it changed since, the check failed, or an older pipeline version
    /// checked it. A correction someone undid is never redone on its own.
    /// </summary>
    /// <param name="subtitlePath">The subtitle file.</param>
    /// <param name="fingerprint">Its current content fingerprint.</param>
    /// <returns><c>true</c> if it should be checked.</returns>
    public bool NeedsCheck(string subtitlePath, string fingerprint) => NeedsCheck(subtitlePath, fingerprint, null);

    /// <summary>
    /// Whether a subtitle file needs checking; an unclear result is checked again when the speech-to-text service in
    /// use has changed since (for example once it has been set up), so hard cases get another chance (SUB-19).
    /// </summary>
    /// <param name="subtitlePath">The subtitle file.</param>
    /// <param name="fingerprint">Its fingerprint now.</param>
    /// <param name="speechSetup">The speech-to-text service in use now (<c>null</c> to ignore).</param>
    /// <returns>Whether to check it.</returns>
    public bool NeedsCheck(string subtitlePath, string fingerprint, string? speechSetup)
    {
        // Asked to run again with the chosen speech-to-text service, or couldn't be checked for want of speech-to-text
        if (_results.Get(ResultStore.IdFor(subtitlePath)) is { } asked && (asked.RerunWith is not null || asked.Status == ResultStatus.Deferred))
        {
            return true;
        }

        if (speechSetup is not null && _results.Get(ResultStore.IdFor(subtitlePath)) is { Status: ResultStatus.Unreliable or ResultStatus.WrongLanguage } unclear
            && string.Equals(unclear.Fingerprint, fingerprint, StringComparison.Ordinal)
            && !string.Equals(unclear.SpeechSetup, speechSetup, StringComparison.Ordinal))
        {
            return true;
        }

        var r = _results.Get(ResultStore.IdFor(subtitlePath));
        if (r is null && _results.ForPath(subtitlePath) is { Status: ResultStatus.Added or ResultStatus.Undone } added)
        {
            // Added by the finder (already checked and cleaned as it was added)
            r = added;
        }

        if (r is null || !string.Equals(r.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return true;
        }

        // Failures and folders that can't be written are tried again only after a while (not every night)
        var age = _clock.GetUtcNow() - r.Time;
        return r.Status != ResultStatus.Undone
            && ((r.Status == ResultStatus.Failed && age >= RetryFailedAfter)
                || (r.Status == ResultStatus.CantWrite && age >= RetryCantWriteAfter)
                || (r.Status is not (ResultStatus.Failed or ResultStatus.CantWrite) && r.Version < CurrentVersion));
    }

    /// <summary>
    /// Checks a subtitle, applies what the policies allow in one write (timing, then clean-up), holds the rest for review,
    /// and records the result.
    /// </summary>
    /// <param name="job">The subtitle.</param>
    /// <param name="audio">The video's audio.</param>
    /// <param name="speech">Speech-to-text, if available (free services only for automatic runs).</param>
    /// <param name="policies">The timing, wording and clean-up settings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result.</returns>
    public async Task<SubtitleResult> ProcessAsync(SubtitleJob job, IAudioSource audio, ISpeechToText? speech, Policies policies, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(policies);
        var info = new FileInfo(job.SubtitlePath);
        if (info.Length > SubtitleReader.MaxBytes)
        {
            return Save(new SubtitleResult
            {
                Id = ResultStore.IdFor(job.SubtitlePath),
                ItemId = job.ItemId,
                Name = job.Name,
                SubtitlePath = job.SubtitlePath,
                Time = _clock.GetUtcNow(),
                Fingerprint = SubtitleFiles.TooLargeFingerprint(info),
                Version = CurrentVersion,
                Status = ResultStatus.TooLarge,
                Explanation = "Too large to be a subtitle file; it isn't read again unless it changes.",
            });
        }

        var bytes = await File.ReadAllBytesAsync(job.SubtitlePath, cancellationToken).ConfigureAwait(false);
        var fingerprint = SubtitleFiles.Fingerprint(bytes);
        var previous = _results.Get(ResultStore.IdFor(job.SubtitlePath));

        // Checking only makes sense if a correction could be written: don't spend audio work (or quota) on it otherwise
        if (Path.GetDirectoryName(job.SubtitlePath) is { } folder && !SubtitleFiles.CanWrite(folder, job.SubtitlePath))
        {
            return Save(new SubtitleResult
            {
                Id = ResultStore.IdFor(job.SubtitlePath),
                ItemId = job.ItemId,
                Name = job.Name,
                SubtitlePath = job.SubtitlePath,
                Time = _clock.GetUtcNow(),
                Fingerprint = fingerprint,
                Version = CurrentVersion,
                Status = ResultStatus.CantWrite,
                Explanation = "Jellyfin's account can't write this subtitle or its folder (a read-only mount, or folder permissions?), so it isn't checked. Tried again in 30 days, or when the file changes.",
            });
        }
        var result = new SubtitleResult
        {
            Id = ResultStore.IdFor(job.SubtitlePath),
            ItemId = job.ItemId,
            Name = job.Name,
            SubtitlePath = job.SubtitlePath,
            Time = _clock.GetUtcNow(),
            Fingerprint = fingerprint,
            Version = CurrentVersion,

            // A file still exactly as this plugin left it stays undoable to the original from before its first change. A
            // file replaced from outside (another tool, a person) starts afresh: its content is the new original.
            Backup = StillOurs(previous, fingerprint) ? previous!.Backup : null,
            Changed = StillOurs(previous, fingerprint),
        };

        var document = SubtitleReader.Read(bytes, job.SubtitlePath);
        if (document is null || document.Cues.Count == 0)
        {
            return Save(result with { Status = ResultStatus.Failed, Explanation = "Not a readable text subtitle." });
        }

        // Text that doesn't decode cleanly (a legacy code page guessed wrong): nothing is changed on its own
        if (document.TextSuspect)
        {
            policies = policies with { Timing = ChangePolicy.Review, Text = ChangePolicy.Review, Auditor = null };
        }

        var outcome = await new SyncCheck(audio, speech, refine: speech is not null, matcher: policies.Matcher)
            .RunAsync(document, job.Duration, Languages.ToTwoLetter(job.Language), cancellationToken).ConfigureAwait(false);
        var model = outcome.Model;
        if (outcome.Deferred)
        {
            // No verdict without speech-to-text: nothing is changed, and the next run tries again
            return Save(result with
            {
                Status = ResultStatus.Deferred,
                Stage = outcome.Stage,
                SpeechSetup = speech?.Id ?? string.Empty,
                SpeechFallback = outcome.SpeechFallback,
                Explanation = outcome.Note ?? "Couldn't check yet: speech-to-text unavailable.",
            });
        }

        var explanation = outcome.Note is null ? model.Explanation : model.Explanation + " " + outcome.Note;
        if (outcome.SpeechFallback is { To: not null } stoodIn)
        {
            explanation += " " + stoodIn.Reason;
        }

        // A timing decided from lines the AI matched by meaning always waits for review (SUB-28), until there is field data
        var status = outcome.WrongLanguageSuspected ? ResultStatus.WrongLanguage : model.Status switch
        {
            SyncStatus.InSync => ResultStatus.InSync,
            SyncStatus.Unreliable => ResultStatus.Unreliable,
            _ => policies.Timing == ChangePolicy.Automatic && outcome.Stage != SyncCheck.ByMeaningStage ? ResultStatus.Corrected : ResultStatus.Proposed,
        };
        result = result with
        {
            // A stand-in's transcripts: its setup is recorded, so an unclear result is checked again with the chosen service
            SpeechSetup = outcome.SpeechFallback?.To ?? speech?.Id ?? string.Empty,
            SpeechFallback = outcome.SpeechFallback,
            Status = status,
            Scale = status is ResultStatus.Corrected or ResultStatus.Proposed ? model.Scale : 1,
            Offset = status is ResultStatus.Corrected or ResultStatus.Proposed ? model.Offset : 0,
            Stage = outcome.Stage,
            Confidence = model.Confidence,
            Explanation = document.TextSuspect
                ? explanation + $" The text didn't decode cleanly as {document.SourceEncoding}, so any change waits for review (the file is written back in that encoding)."
                : explanation,
        };

        // Timing first (when applied), then clean-up: what the policies allow now, the rest held for review
        // A subtitle that doesn't match the speech (another language, version, or a notes track) gets only the harmless
        // clean-up (adverts, empty lines): its timing isn't touched and nothing is suggested
        var timed = status == ResultStatus.Corrected ? document.Retime(model.Map) : document;
        var fitting = status != ResultStatus.WrongLanguage;
        var automatic = document.TextSuspect ? WithoutTextChanges(AutomaticOptions(policies)) : AutomaticOptions(policies);
        var full = CleanupPolicy.Options(policies.Cleanup);
        if (!fitting)
        {
            automatic = automatic with { FixOverlaps = false, ExtendShortCues = false, MergeDuplicates = false, StripHearingImpaired = false };
            full = automatic;
        }

        var (cleaned, applied) = SubtitleCleaner.Clean(timed, automatic);
        var (_, all) = SubtitleCleaner.Clean(timed, full);
        var pending = all.Where(c => CleanupPolicy.For(c.Kind, policies.Cleanup, policies.Text, policies.Timing) == ChangePolicy.Review).ToList();
        result = result with
        {
            Cleaned = Count(applied),
            CleanupPending = Count(pending),
            Examples = [.. outcome.Pairs.Concat(applied.Select(c => Describe(c, false))).Concat(pending.Select(c => Describe(c, true))).Take(MaxExamples)],
        };

        // With the timing settled by speech-to-text (and the words matching, so not a translation), the wording can be
        // audited: lines whose meaning differs are flagged, and suggested wording waits for review
        var writes = status == ResultStatus.Corrected || applied.Count > 0;
        if (policies.Auditor is not null && outcome.Transcripts.Count > 0 && status is ResultStatus.InSync or ResultStatus.Corrected or ResultStatus.Proposed
            && outcome.Stage != SyncCheck.ByMeaningStage)
        {
            var file = writes ? cleaned : document;
            Func<TimeSpan, TimeSpan> toAudio = status == ResultStatus.Proposed ? model.Map : t => t;
            var (findings, note, by) = await WordingAudit.RunAsync(policies.Auditor, file, toAudio, outcome.Transcripts, Languages.ToTwoLetter(job.Language), cancellationToken).ConfigureAwait(false);
            result = result with
            {
                Audited = by is not null,
                Findings = findings,
                Examples = [.. findings.Select(WordingAudit.Describe).Concat(result.Examples).Take(MaxExamples)],
                Explanation = result.Explanation + (findings.Count > 0
                    ? $" Wording audited ({by}): {findings.Count} line(s) differ in meaning from what is said{(findings.Any(f => f.Suggestion is not null) ? "; Apply uses the suggested wording" : string.Empty)}."
                    : note.Length > 0 ? " " + note : by is not null ? $" Wording audited ({by}): no differences in meaning." : string.Empty),
            };
        }

        if (Calibration is not null && outcome.Stage == WholeFileChecker.SpeechStage && status is ResultStatus.InSync or ResultStatus.Corrected
            && result.Findings.Count == 0 && !document.TextSuspect && outcome.Transcripts.Count > 0)
        {
            Learn(document, status == ResultStatus.Corrected ? model.Map : t => t, outcome.Transcripts, Languages.ToTwoLetter(job.Language));
        }

        if (!writes)
        {
            return Save(result);
        }

        var (backup, written) = _files.Replace(job.SubtitlePath, fingerprint, SubtitleWriter.ToBytes(cleaned));
        return Save(result with { Backup = result.Backup ?? backup, Fingerprint = written, Changed = true });
    }

    /// <summary>
    /// A result by id.
    /// </summary>
    /// <param name="id">Result id.</param>
    /// <returns>The result, or <c>null</c>.</returns>
    public SubtitleResult? Get(string id) => _results.FindForRequest(id);

    /// <summary>
    /// Opens a subtitle this plugin has a result for, for editing by hand.
    /// </summary>
    /// <param name="id">Result id.</param>
    /// <returns>The lines, or <c>null</c> if there is no such result or the file isn't a text subtitle.</returns>
    /// <exception cref="StaleResultException">The subtitle file (or the video) is gone; the result was cleared.</exception>
    /// <exception cref="InvalidOperationException">The file can't be reached, or there is none (yet).</exception>
    public EditorView? LoadForEditing(string id)
    {
        if (_results.FindForRequest(id) is not { } r)
        {
            return null;
        }

        Require(r, subtitle: true);
        return Guard(r, () =>
        {
            var bytes = File.ReadAllBytes(r.SubtitlePath);
            if (bytes.Length > SubtitleReader.MaxBytes || SubtitleReader.Read(bytes, r.SubtitlePath) is not { } document)
            {
                return null;
            }

            return new EditorView(r.Id, r.Name, Path.GetFileName(r.SubtitlePath), SubtitleFiles.Fingerprint(bytes), document.Format.ToString(), SubtitleEditing.ToEditor(document));
        });
    }

    /// <summary>
    /// Saves lines edited by hand. The file must still be as it was when opened; the first original is kept, so Undo
    /// brings it back.
    /// </summary>
    /// <param name="id">Result id.</param>
    /// <param name="fingerprint">The fingerprint the editor loaded.</param>
    /// <param name="cues">The lines.</param>
    /// <returns>The updated result.</returns>
    /// <exception cref="InvalidOperationException">No such result, the file changed since, or the edit isn't valid.</exception>
    public SubtitleResult SaveEdited(string id, string fingerprint, IReadOnlyList<EditorCue> cues)
    {
        var r = Find(id);
        Require(r, subtitle: true);
        return Guard(r, () => SaveEditedNow(r, fingerprint, cues));
    }

    private SubtitleResult SaveEditedNow(SubtitleResult r, string fingerprint, IReadOnlyList<EditorCue> cues)
    {
        if (r.Id.StartsWith(SubtitleGenerator.IdPrefix, StringComparison.Ordinal))
        {
            // Undo removes a generated subtitle only while it is as generated; edits would make it impossible to undo
            throw new InvalidOperationException("A generated subtitle can't be edited here.");
        }

        var bytes = File.ReadAllBytes(r.SubtitlePath);
        var current = SubtitleFiles.Fingerprint(bytes);
        if (!string.Equals(current, fingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The subtitle file changed since it was opened; open it again.");
        }

        var document = SubtitleReader.Read(bytes, r.SubtitlePath) ?? throw new InvalidOperationException("The subtitle can no longer be read.");
        var (edited, changed, problem) = SubtitleEditing.Apply(document, cues);
        if (edited is null)
        {
            throw new InvalidOperationException(problem);
        }

        if (changed == 0)
        {
            return r;
        }

        try
        {
            var (backup, written) = _files.Replace(r.SubtitlePath, current, SubtitleWriter.ToBytes(edited));
            var counts = new Dictionary<string, int>(r.Cleaned, StringComparer.Ordinal);
            counts[SubtitleEditing.EditedKind] = counts.GetValueOrDefault(SubtitleEditing.EditedKind) + changed;
            return Save(r with
            {
                Backup = r.Changed ? r.Backup ?? backup : backup,
                Fingerprint = written,
                Changed = true,
                Cleaned = counts,
                Findings = [.. r.Findings.Where(f => !DiscrepancyReview.IsWholeFile(f) || DiscrepancyReview.StillOpen(edited, f))],
                Time = _clock.GetUtcNow(),
            });
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    /// <summary>
    /// When a subtitle's result was last recorded (for ordering the nightly audit).
    /// </summary>
    /// <param name="subtitlePath">The subtitle file.</param>
    /// <returns>The time, or the earliest time when there is no result.</returns>
    public DateTimeOffset LastChecked(string subtitlePath) => _results.Get(ResultStore.IdFor(subtitlePath))?.Time ?? DateTimeOffset.MinValue;

    /// <summary>
    /// Whether a subtitle checked earlier can have its wording audited now: in sync or corrected by this plugin's check
    /// (not a translation matched by meaning), not audited yet, nothing waiting, and the file unchanged since.
    /// </summary>
    /// <param name="subtitlePath">The subtitle file.</param>
    /// <param name="fingerprint">The file's fingerprint now, or <c>null</c> to check only the stored result.</param>
    /// <returns>Whether it can be audited.</returns>
    public bool NeedsAudit(string subtitlePath, string? fingerprint = null)
    {
        var r = _results.Get(ResultStore.IdFor(subtitlePath));
        return r is { Status: ResultStatus.InSync or ResultStatus.Corrected, Audited: false, PendingReview: false }
            && r.Stage != SyncCheck.ByMeaningStage
            && r.Version == CurrentVersion
            && (fingerprint is null || string.Equals(r.Fingerprint, fingerprint, StringComparison.Ordinal));
    }

    /// <summary>
    /// Audits the wording of a subtitle checked earlier (see <see cref="NeedsAudit"/>): a few stretches are transcribed
    /// again and compared with the lines, which are already on the audio's clock. If the transcript no longer finds the
    /// subtitle in sync, it isn't audited (and isn't tried again until the file changes).
    /// </summary>
    /// <param name="job">The subtitle.</param>
    /// <param name="audio">The video's audio.</param>
    /// <param name="speech">Speech-to-text.</param>
    /// <param name="auditor">The auditor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated result, or <c>null</c> if it wasn't audited (not eligible, or the auditor gave no answer).</returns>
    public async Task<SubtitleResult?> AuditAsync(SubtitleJob job, IAudioSource audio, ISpeechToText speech, ITextAuditor auditor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(auditor);
        var bytes = await File.ReadAllBytesAsync(job.SubtitlePath, cancellationToken).ConfigureAwait(false);
        var fingerprint = SubtitleFiles.Fingerprint(bytes);
        if (!NeedsAudit(job.SubtitlePath, fingerprint) || _results.Get(ResultStore.IdFor(job.SubtitlePath)) is not { } r
            || SubtitleReader.Read(bytes, job.SubtitlePath) is not { Cues.Count: > 0 } document)
        {
            return null;
        }

        var language = Languages.ToTwoLetter(job.Language);
        var (model, transcripts) = await new TranscriptSynchroniser(audio, speech).SolveAsync(document, job.Duration, language, cancellationToken).ConfigureAwait(false);
        if (model.Status != SyncStatus.InSync)
        {
            return Save(r with { Audited = true, Explanation = r.Explanation + " Wording not audited: a fresh transcript didn't find it in sync (" + model.Explanation + ")." });
        }

        var (findings, note, by) = await WordingAudit.RunAsync(auditor, document, t => t, transcripts, language, cancellationToken).ConfigureAwait(false);
        if (by is null)
        {
            return null;
        }

        return Save(r with
        {
            Audited = true,
            Findings = findings,
            Time = _clock.GetUtcNow(),
            Examples = [.. findings.Select(WordingAudit.Describe).Concat(r.Examples).Take(MaxExamples)],
            Explanation = r.Explanation + (findings.Count > 0
                ? $" Wording audited ({by}): {findings.Count} line(s) differ in meaning from what is said{(findings.Any(f => f.Suggestion is not null) ? "; Apply uses the suggested wording" : string.Empty)}."
                : $" Wording audited ({by}): no differences in meaning."),
        });
    }

    /// <summary>
    /// Applies everything waiting for review: the timing correction and the held-back clean-up.
    /// </summary>
    /// <param name="id">Result id.</param>
    /// <param name="policies">The clean-up settings (held-back kinds are applied as if automatic).</param>
    /// <returns>The updated result.</returns>
    /// <exception cref="InvalidOperationException">Nothing is waiting, or the file changed since.</exception>
    public SubtitleResult Apply(string id, Policies policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        var r = Find(id);
        Require(r, subtitle: true);
        return Guard(r, () => ApplyNow(r, policies));
    }

    private SubtitleResult ApplyNow(SubtitleResult r, Policies policies)
    {
        if (!r.PendingReview)
        {
            throw new InvalidOperationException("Nothing is waiting for review for this subtitle.");
        }

        if (r.Status != ResultStatus.Proposed && r.CleanupPending.Count == 0 && !r.Findings.Any(f => f.Suggestion is not null))
        {
            throw new InvalidOperationException("Only lines with nothing heard are left: remove or decline them one at a time.");
        }

        var bytes = File.ReadAllBytes(r.SubtitlePath);
        var document = SubtitleReader.Read(bytes, r.SubtitlePath) ?? throw new InvalidOperationException("The subtitle can no longer be read.");

        // Suggested wording first (the lines are found by their text and time as the file has them now), then timing
        var reworded = 0;
        var fixedLines = 0;
        if (r.Findings.Count > 0)
        {
            (document, reworded) = WordingAudit.Apply(document, r.Findings.Where(f => !DiscrepancyReview.IsWholeFile(f)));
            (document, fixedLines) = DiscrepancyReview.ApplyAll(document, r.Findings);
        }

        var notInVideo = 0;
        if (r.Status == ResultStatus.Proposed && r.Sections is { Count: > 0 } sections)
        {
            // A subtitle made for a different cut: the lines still flagged as not in the video go, then each section moves
            // by its own timing
            (document, notInVideo) = RemoveNotInVideo(document, r.Findings);
            document = PiecewiseFit.Retime(document, r.Scale, sections);
        }
        else if (r.Status == ResultStatus.Proposed)
        {
            (document, notInVideo) = RemoveNotInVideo(document, r.Findings);
            document = document.Retime(new SyncModel(SyncStatus.Corrected, r.Scale, r.Offset, r.Confidence, [], r.Explanation).Map);
        }

        var (cleaned, changes) = SubtitleCleaner.Clean(document, CleanupPolicy.Options(policies.Cleanup));
        try
        {
            var (backup, written) = _files.Replace(r.SubtitlePath, r.Fingerprint, SubtitleWriter.ToBytes(cleaned));
            var cleanedCounts = new Dictionary<string, int>(r.Cleaned, StringComparer.Ordinal);
            foreach (var (kind, n) in Count(changes))
            {
                cleanedCounts[kind] = cleanedCounts.GetValueOrDefault(kind) + n;
            }

            if (reworded > 0)
            {
                cleanedCounts[RewordedKind] = cleanedCounts.GetValueOrDefault(RewordedKind) + reworded;
            }

            if (fixedLines > 0)
            {
                cleanedCounts[DiscrepancyReview.FixedKind] = cleanedCounts.GetValueOrDefault(DiscrepancyReview.FixedKind) + fixedLines;
            }

            if (notInVideo > 0)
            {
                cleanedCounts[DiscrepancyReview.RemovedNotInVideoKind] = cleanedCounts.GetValueOrDefault(DiscrepancyReview.RemovedNotInVideoKind) + notInVideo;
            }

            // Lines with nothing heard are only removed one at a time, so they stay for review
            return Save(r with
            {
                Status = r.Status == ResultStatus.Proposed ? ResultStatus.Corrected : r.Status,
                Backup = r.Changed ? r.Backup ?? backup : backup,
                Fingerprint = written,
                Changed = true,
                Cleaned = cleanedCounts,
                CleanupPending = new Dictionary<string, int>(),
                Findings = [.. r.Findings.Where(f => DiscrepancyReview.IsWholeFile(f) && f.Kind == DiscrepancyFinder.Extra && DiscrepancyReview.StillOpen(cleaned, f))],
                Time = _clock.GetUtcNow(),
            });
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    /// <summary>
    /// Applies one finding (a suggested wording, a missing line added, or a line with nothing heard removed); the others
    /// keep waiting. The first original is kept, so Undo brings it back.
    /// </summary>
    /// <param name="id">Result id.</param>
    /// <param name="index">The finding's position in the result's list.</param>
    /// <param name="time">The finding's time as the page shows it (to be sure it is the same finding).</param>
    /// <returns>The updated result.</returns>
    /// <exception cref="InvalidOperationException">No such finding, or its line changed since.</exception>
    public SubtitleResult ApplyFinding(string id, int index, double time)
    {
        var r = Find(id);
        Require(r, subtitle: true);
        return Guard(r, () => ApplyFindingNow(r, index, time));
    }

    private SubtitleResult ApplyFindingNow(SubtitleResult r, int index, double time)
    {
        var f = FindingAt(r, index, time);
        var bytes = File.ReadAllBytes(r.SubtitlePath);
        var document = SubtitleReader.Read(bytes, r.SubtitlePath) ?? throw new InvalidOperationException("The subtitle can no longer be read.");
        SubtitleDocument changed;
        string kind;
        if (DiscrepancyReview.IsWholeFile(f) || DiscrepancyReview.IsSection(f))
        {
            var (done, problem) = DiscrepancyReview.ApplyOne(document, f);
            changed = done ?? throw new InvalidOperationException(problem);
            kind = DiscrepancyReview.IsSection(f) ? DiscrepancyReview.RemovedNotInVideoKind : DiscrepancyReview.FixedKind;
        }
        else
        {
            var (done, n) = f.Suggestion is null ? (document, 0) : WordingAudit.Apply(document, [f]);
            changed = n > 0 ? done : throw new InvalidOperationException(f.Suggestion is null ? "There's no suggested wording for this line; open the editor instead." : "That line has changed since it was checked; open the editor instead.");
            kind = RewordedKind;
        }

        try
        {
            var (backup, written) = _files.Replace(r.SubtitlePath, r.Fingerprint, SubtitleWriter.ToBytes(changed));
            var counts = new Dictionary<string, int>(r.Cleaned, StringComparer.Ordinal);
            counts[kind] = counts.GetValueOrDefault(kind) + 1;
            return Save(r with
            {
                Backup = r.Changed ? r.Backup ?? backup : backup,
                Fingerprint = written,
                Changed = true,
                Cleaned = counts,
                Findings = [.. r.Findings.Where((_, i) => i != index)],
                Time = _clock.GetUtcNow(),
            });
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    /// <summary>
    /// Declines one finding: nothing is changed, and it no longer waits for review.
    /// </summary>
    /// <param name="id">Result id.</param>
    /// <param name="index">The finding's position in the result's list.</param>
    /// <param name="time">The finding's time as the page shows it.</param>
    /// <returns>The updated result.</returns>
    /// <exception cref="InvalidOperationException">No such finding.</exception>
    public SubtitleResult DeclineFinding(string id, int index, double time)
    {
        var r = Find(id);
        Require(r);
        FindingAt(r, index, time);
        return Save(r with { Findings = [.. r.Findings.Where((_, i) => i != index)], Time = _clock.GetUtcNow() });
    }

    /// <summary>
    /// Applies every line finding that carries a suggestion (suggested wording, a missing line added), as
    /// <see cref="ApplyFinding"/> would one by one, in one write; lines with nothing heard are only removed one at a time,
    /// so they, and any finding whose line has changed since, keep waiting. A timing correction or clean-up waiting for
    /// review is left as it is. The first original is kept, so Undo brings it back.
    /// </summary>
    /// <param name="id">Result id.</param>
    /// <returns>The updated result and how many findings were applied.</returns>
    /// <exception cref="InvalidOperationException">No suggestion to apply, none could be applied, or the file changed since.</exception>
    public (SubtitleResult Result, int Applied) ApplyFindings(string id)
    {
        var r = Find(id);
        Require(r, subtitle: true);
        return Guard(r, () => ApplyFindingsNow(r));
    }

    private (SubtitleResult Result, int Applied) ApplyFindingsNow(SubtitleResult r)
    {
        if (!r.Findings.Any(HasSuggestion))
        {
            throw new InvalidOperationException("No line to review here has a suggestion to apply.");
        }

        var bytes = File.ReadAllBytes(r.SubtitlePath);
        var document = SubtitleReader.Read(bytes, r.SubtitlePath) ?? throw new InvalidOperationException("The subtitle can no longer be read.");
        var applied = new HashSet<int>();
        int reworded = 0, fixedLines = 0;
        for (var i = 0; i < r.Findings.Count; i++)
        {
            var f = r.Findings[i];
            if (!HasSuggestion(f))
            {
                continue;
            }

            if (DiscrepancyReview.IsWholeFile(f))
            {
                if (DiscrepancyReview.ApplyOne(document, f) is ({ } done, null))
                {
                    document = done;
                    applied.Add(i);
                    fixedLines++;
                }
            }
            else
            {
                var (done, n) = WordingAudit.Apply(document, [f]);
                if (n > 0)
                {
                    document = done;
                    applied.Add(i);
                    reworded++;
                }
            }
        }

        if (applied.Count == 0)
        {
            throw new InvalidOperationException("None of the suggestions could be applied: their lines have changed since they were checked. Open the editor instead.");
        }

        try
        {
            var (backup, written) = _files.Replace(r.SubtitlePath, r.Fingerprint, SubtitleWriter.ToBytes(document));
            var counts = new Dictionary<string, int>(r.Cleaned, StringComparer.Ordinal);
            if (reworded > 0)
            {
                counts[RewordedKind] = counts.GetValueOrDefault(RewordedKind) + reworded;
            }

            if (fixedLines > 0)
            {
                counts[DiscrepancyReview.FixedKind] = counts.GetValueOrDefault(DiscrepancyReview.FixedKind) + fixedLines;
            }

            return (Save(r with
            {
                Backup = r.Changed ? r.Backup ?? backup : backup,
                Fingerprint = written,
                Changed = true,
                Cleaned = counts,
                Findings = [.. r.Findings.Where((_, i) => !applied.Contains(i))],
                Time = _clock.GetUtcNow(),
            }), applied.Count);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    /// <summary>
    /// Declines every line finding: nothing is changed, and none of them waits for review any more (a timing correction or
    /// clean-up waiting for review is left as it is).
    /// </summary>
    /// <param name="id">Result id.</param>
    /// <returns>The updated result.</returns>
    /// <exception cref="InvalidOperationException">No such result, or no findings.</exception>
    public SubtitleResult DeclineFindings(string id)
    {
        var r = Find(id);
        Require(r);
        if (r.Findings.Count == 0)
        {
            throw new InvalidOperationException("There are no lines to review for this subtitle.");
        }

        return Save(r with { Findings = [], Time = _clock.GetUtcNow() });
    }

    // A finding "Apply all suggestions" applies: one with a suggestion that isn't a line with nothing heard
    private static bool HasSuggestion(LineFinding f)
        => f.Suggestion is not null && !(DiscrepancyReview.IsWholeFile(f) && f.Kind == DiscrepancyFinder.Extra);

    /// <summary>
    /// Asks for a subtitle file to be compared whole with a full transcript of its video on the next run of the full
    /// transcripts task (the request returns at once). A file the run can't check is refused with the reason, by the
    /// run's own rules (see <see cref="WholeFileChecker.Ineligible"/>), so nothing waits in the queue for ever.
    /// </summary>
    /// <param name="id">Result id.</param>
    /// <param name="jobFor">The subtitle as the library lists it (video, language, audio track), or <c>null</c>.</param>
    /// <param name="wanted">The wanted languages, in order.</param>
    /// <returns>The updated result, or why it can't be checked.</returns>
    /// <exception cref="InvalidOperationException">No such result.</exception>
    public (SubtitleResult? Result, string? Refused) RequestWholeFileCheck(string id, Func<SubtitleResult, SubtitleJob?> jobFor, IReadOnlyList<string> wanted)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        return RequestWholeFileCheck(id, jobFor, _ => wanted);
    }

    /// <summary>
    /// Asks for a subtitle file to be compared whole with a full transcript, with the languages wanted for its video's
    /// library (see <see cref="RequestWholeFileCheck(string, Func{SubtitleResult, SubtitleJob?}, IReadOnlyList{string})"/>).
    /// </summary>
    /// <param name="id">Result id.</param>
    /// <param name="jobFor">The subtitle as the library lists it (video, language, audio track), or <c>null</c>.</param>
    /// <param name="wantedFor">The wanted languages for a subtitle's video, in order.</param>
    /// <returns>The updated result, or why it can't be checked.</returns>
    /// <exception cref="InvalidOperationException">No such result.</exception>
    public (SubtitleResult? Result, string? Refused) RequestWholeFileCheck(string id, Func<SubtitleResult, SubtitleJob?> jobFor, Func<SubtitleJob, IReadOnlyList<string>> wantedFor)
    {
        ArgumentNullException.ThrowIfNull(jobFor);
        ArgumentNullException.ThrowIfNull(wantedFor);
        var r = Find(id);
        Require(r, subtitle: true);
        var job = jobFor(r);
        if (job is not null)
        {
            // The check reads the video: one replaced or removed clears the result too
            Require(r, subtitle: true, video: job.VideoPath);
        }

        if (WholeFileChecker.Ineligible(r, job, job is null ? [] : wantedFor(job)) is { } why)
        {
            return (null, why);
        }

        return (r.WholeFileRequested ? r : Save(r with { WholeFileRequested = true }), null);
    }

    /// <summary>
    /// Asks for a subtitle's timing to be fixed by section against a full transcript (see <see cref="SectionFixer"/>) on
    /// the next run of the full transcripts task. A file the run can't fix is refused with the reason, by the run's own
    /// rules (see <see cref="SectionFixer.Ineligible"/>).
    /// </summary>
    /// <param name="id">Result id.</param>
    /// <param name="jobFor">The subtitle as the library lists it (video, language, audio track), or <c>null</c>.</param>
    /// <param name="wantedFor">The wanted languages for a subtitle's video, in order.</param>
    /// <returns>The updated result and the subtitle as the library lists it, or why it can't be fixed.</returns>
    /// <exception cref="InvalidOperationException">No such result.</exception>
    public (SubtitleResult? Result, SubtitleJob? Job, string? Refused) RequestSectionFix(string id, Func<SubtitleResult, SubtitleJob?> jobFor, Func<SubtitleJob, IReadOnlyList<string>> wantedFor)
    {
        ArgumentNullException.ThrowIfNull(jobFor);
        ArgumentNullException.ThrowIfNull(wantedFor);
        var r = Find(id);
        Require(r, subtitle: true);
        var job = jobFor(r);
        if (job is not null)
        {
            Require(r, subtitle: true, video: job.VideoPath);
        }

        if (SectionFixer.Ineligible(r, job, job is null ? [] : wantedFor(job)) is { } why)
        {
            return (null, job, why);
        }

        return (r.SectionFixRequested ? r : Save(r with { SectionFixRequested = true }), job, null);
    }

    // The lines a fix by section flagged as not in the video, as long as they are still flagged (not declined) and as
    // they were
    private static (SubtitleDocument Document, int Removed) RemoveNotInVideo(SubtitleDocument document, IReadOnlyList<LineFinding> findings)
    {
        var removed = 0;
        foreach (var f in findings.Where(f => DiscrepancyReview.IsSection(f) && f.Kind == DiscrepancyReview.NotInVideo))
        {
            if (document.Cues.Count > 1 && DiscrepancyReview.ApplyOne(document, f) is ({ } done, null))
            {
                document = done;
                removed++;
            }
        }

        return (document, removed);
    }

    /// <summary>
    /// Undoes this plugin's changes to a file: the first original comes back, if the file is still as this plugin left it.
    /// </summary>
    /// <param name="id">Result id.</param>
    /// <returns>The updated result.</returns>
    /// <exception cref="InvalidOperationException">Nothing to undo, or the file changed since.</exception>
    public SubtitleResult Undo(string id)
    {
        var r = Find(id);

        // Putting an original back needs the file as this plugin left it; removing an added or generated one doesn't
        Require(r, subtitle: r.Changed && !IsRemoval(r));
        return Guard(r, () => Undo(r));
    }

    /// <summary>The most files one call of <see cref="RestoreAll"/> handles.</summary>
    public const int RestoreBatch = 200;

    /// <summary>
    /// What <see cref="RestoreAll"/> would do: how many changed files would get their original back, and how many added or
    /// generated subtitles would be removed.
    /// </summary>
    /// <returns>The counts.</returns>
    public RestorePreview PreviewRestoreAll()
    {
        var all = Restorable().ToList();
        return new RestorePreview(all.Count(r => !IsRemoval(r)), all.Count(IsRemoval));
    }

    /// <summary>
    /// Undoes everything this plugin did, for use before uninstalling, a batch at a time: every file it changed gets its
    /// original back, then every subtitle it added or generated is removed, each exactly as <b>Undo</b> would, so a file
    /// changed since (by someone else, or by hand) is left alone and reported, as is one whose original is no longer
    /// kept. A changed file that is gone isn't brought back; an added one that is gone is simply recorded as undone.
    /// Restored files aren't changed again by later runs (as after Undo). Files are taken in a fixed order (originals
    /// first, then removals, each by result id), so a caller passes the returned cursor to carry on.
    /// </summary>
    /// <param name="after">The cursor from the previous batch, or <c>null</c> to start.</param>
    /// <param name="max">The most files in this batch (1 to <see cref="RestoreBatch"/>).</param>
    /// <returns>What was done, what was skipped and why, and the cursor for the next batch (<c>null</c> when done).</returns>
    public RestoreAllBatch RestoreAll(string? after, int max = RestoreBatch)
    {
        var take = Math.Clamp(max, 1, RestoreBatch);
        var todo = Restorable().Select(r => (Key: RestoreKey(r), Result: r))
            .Where(x => after is null || string.CompareOrdinal(x.Key, after) > 0)
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToList();
        int restored = 0, removed = 0, gone = 0;
        var skipped = new List<RestoreSkip>();
        var touched = new List<string>();
        foreach (var (_, r) in todo.Take(take))
        {
            var removal = IsRemoval(r);
            if (!File.Exists(r.SubtitlePath) && !removal)
            {
                skipped.Add(new RestoreSkip(r.Name, Path.GetFileName(r.SubtitlePath), "The file is no longer there, so its original wasn't put back."));
                continue;
            }

            try
            {
                var existed = File.Exists(r.SubtitlePath);
                Undo(r);
                touched.Add(r.SubtitlePath);
                if (!removal)
                {
                    restored++;
                }
                else if (existed)
                {
                    removed++;
                }
                else
                {
                    gone++;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                skipped.Add(new RestoreSkip(r.Name, Path.GetFileName(r.SubtitlePath), ex.Message));
            }
        }

        var next = todo.Count > take ? todo[take - 1].Key : null;
        return new RestoreAllBatch(restored, removed, gone, skipped, next, todo.Count - Math.Min(take, todo.Count)) { Touched = touched };
    }

    // What restoring everything undoes: files holding this plugin's changes (with the original kept), and subtitles it
    // added or generated that are still as it left them (as far as the results know)
    private IEnumerable<SubtitleResult> Restorable()
        => _results.All().Where(r => r.Changed && (IsRemoval(r) || r.Backup is not null));

    private static bool IsRemoval(SubtitleResult r) => r.Status is ResultStatus.Added or ResultStatus.Generated;

    // Originals first: a subtitle the plugin added and then corrected must be back as added before it can be removed
    private static string RestoreKey(SubtitleResult r) => (IsRemoval(r) ? "1:" : "0:") + r.Id;

    private SubtitleResult Undo(SubtitleResult r)
    {
        if (r.Status == ResultStatus.Generated && r.Changed)
        {
            try
            {
                SubtitleFiles.RemoveAdded(r.SubtitlePath, r.Fingerprint);
                return Save(r with { Status = ResultStatus.Undone, Changed = false, Examples = [], Time = _clock.GetUtcNow(), Explanation = "Undone: the generated subtitle was removed (it won't be generated again on its own; a subtitle found later is still added)." });
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(ex.Message, ex);
            }
        }

        if (r.Status == ResultStatus.Added && r.Changed)
        {
            try
            {
                SubtitleFiles.RemoveAdded(r.SubtitlePath, r.Fingerprint);
                return Save(r with { Status = ResultStatus.Undone, Changed = false, Examples = [], Time = _clock.GetUtcNow(), Explanation = "Undone: the added subtitle was removed (it won't be searched for again on its own)." });
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(ex.Message, ex);
            }
        }

        if (!r.Changed || r.Backup is null)
        {
            throw new InvalidOperationException("There's no change to undo for this subtitle.");
        }

        try
        {
            var restored = _files.Restore(r.SubtitlePath, r.Backup, r.Fingerprint);
            return Save(r with
            {
                Status = ResultStatus.Undone,
                Fingerprint = restored,
                Changed = false,
                Cleaned = new Dictionary<string, int>(),
                CleanupPending = new Dictionary<string, int>(),
                Examples = [],
                Findings = [],
                Sections = null,
                Time = _clock.GetUtcNow(),
                Explanation = "Undone: the original subtitle file is back.",
            });
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    /// <summary>
    /// Clean-up options that change no text (for text that didn't decode cleanly): only empty lines are removed.
    /// </summary>
    /// <param name="o">The options.</param>
    /// <returns>The options without text changes.</returns>
    public static CleanOptions WithoutTextChanges(CleanOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);
        return o with { RemoveAdverts = false, MergeDuplicates = false, StripHearingImpaired = false, FixOverlaps = false, ExtendShortCues = false };
    }

    /// <summary>
    /// Clean-up options with every kind that waits for review switched off.
    /// </summary>
    /// <param name="p">The policies.</param>
    /// <returns>The options.</returns>
    public static CleanOptions AutomaticOptions(Policies p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var o = CleanupPolicy.Options(p.Cleanup);
        bool Auto(CleanChangeKind kind) => CleanupPolicy.For(kind, p.Cleanup, p.Text, p.Timing) == ChangePolicy.Automatic;
        return o with
        {
            RemoveAdverts = o.RemoveAdverts && Auto(CleanChangeKind.RemovedAdvert),
            MergeDuplicates = o.MergeDuplicates && Auto(CleanChangeKind.MergedDuplicate),
            StripHearingImpaired = o.StripHearingImpaired && Auto(CleanChangeKind.StrippedHearingImpaired),
            FixOverlaps = o.FixOverlaps && Auto(CleanChangeKind.FixedOverlap),
            ExtendShortCues = o.ExtendShortCues && Auto(CleanChangeKind.ExtendedShortCue),
        };
    }

    // Whether the file still holds this plugin's last change (so undo should go back to the original from before it)
    private static bool StillOurs(SubtitleResult? previous, string fingerprint)
        => previous is { Changed: true, Backup: not null } && string.Equals(previous.Fingerprint, fingerprint, StringComparison.Ordinal);

    private static Dictionary<string, int> Count(IEnumerable<CleanChange> changes)
        => changes.GroupBy(c => c.Kind.ToString()).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    private static string Describe(CleanChange c, bool pending)
    {
        static string Quote(string text)
        {
            var plain = SubtitleMarkup.ToPlainText(text).Replace('\n', ' ');
            return "\u201c" + (plain.Length > 60 ? plain[..57] + "…" : plain) + "\u201d";
        }

        var at = c.At.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        var what = c.Kind switch
        {
            CleanChangeKind.RemovedAdvert => "Removed advert " + Quote(c.Before),
            CleanChangeKind.RemovedEmpty => "Removed empty line at " + at,
            CleanChangeKind.MergedDuplicate => "Merged repeated line " + Quote(c.Before),
            CleanChangeKind.StrippedHearingImpaired => "Removed sound description: " + Quote(c.Before) + " → " + Quote(c.After),
            CleanChangeKind.FixedOverlap => "Shortened a line overlapping the next at " + at,
            CleanChangeKind.ExtendedShortCue => "Lengthened a too-brief line at " + at,
            _ => c.Kind.ToString(),
        };
        return pending ? "Waiting for review: " + what : what;
    }

    /// <summary>
    /// Records that a subtitle couldn't be checked (it is tried again next time).
    /// </summary>
    /// <param name="job">The subtitle.</param>
    /// <param name="error">What went wrong.</param>
    public void RecordFailure(SubtitleJob job, string error)
    {
        ArgumentNullException.ThrowIfNull(job);

        // With the fingerprint, the failure is tried again only after a while or when the file changes
        string fingerprint;
        try
        {
            fingerprint = SubtitleFiles.Fingerprint(File.ReadAllBytes(job.SubtitlePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            fingerprint = string.Empty;
        }

        Save(new SubtitleResult
        {
            Id = ResultStore.IdFor(job.SubtitlePath),
            ItemId = job.ItemId,
            Name = job.Name,
            SubtitlePath = job.SubtitlePath,
            Time = _clock.GetUtcNow(),
            Fingerprint = fingerprint,
            Version = CurrentVersion,
            Status = ResultStatus.Failed,
            Explanation = error + " Tried again in 3 days, or when the file changes.",
        });
    }

    /// <summary>
    /// Declines what waits for review (a correction, clean-up or suggested wording): nothing is changed, and it isn't
    /// proposed again unless the file changes.
    /// </summary>
    /// <param name="id">Result id.</param>
    /// <returns>The updated result.</returns>
    /// <exception cref="InvalidOperationException">Nothing waits for review.</exception>
    public SubtitleResult Decline(string id)
    {
        var r = Find(id);
        Require(r);
        if (!r.PendingReview)
        {
            throw new InvalidOperationException("Nothing is waiting for review for this subtitle.");
        }

        return Save(r with
        {
            Status = r.Status == ResultStatus.Proposed ? ResultStatus.Declined : r.Status,
            Scale = r.Status == ResultStatus.Proposed ? 1 : r.Scale,
            Offset = r.Status == ResultStatus.Proposed ? 0 : r.Offset,
            Sections = r.Status == ResultStatus.Proposed ? null : r.Sections,
            CleanupPending = new Dictionary<string, int>(),
            Findings = [],
            Time = _clock.GetUtcNow(),
            Explanation = r.Explanation + " Declined in review: nothing was changed.",
        });
    }

    /// <summary>
    /// Forgets a result so the subtitle is checked again on the next run, or the video searched again (also after an
    /// added subtitle was undone). A file this plugin changed must be undone first, so its own output never becomes the
    /// new original.
    /// </summary>
    /// <param name="id">Result id.</param>
    /// <exception cref="InvalidOperationException">No such result, or the file holds this plugin's changes.</exception>
    public void CheckAgain(string id)
    {
        var r = Find(id);
        Require(r);
        if (r.Changed && r.Status != ResultStatus.Added)
        {
            throw new InvalidOperationException("This subtitle holds this plugin's changes: undo them first, then check it again.");
        }

        if (r.Status == ResultStatus.Added && r.Changed)
        {
            throw new InvalidOperationException("This subtitle was added by this plugin: undo it first to search again.");
        }

        _results.Remove(id);
    }

    /// <summary>
    /// The speech-to-text service a subtitle is to be checked again with on this run (asked for with
    /// <see cref="RequestRerun"/>), or <c>null</c>.
    /// </summary>
    /// <param name="subtitlePath">The subtitle file.</param>
    /// <returns>The service id, or <c>null</c>.</returns>
    public string? RerunWith(string subtitlePath) => _results.Get(ResultStore.IdFor(subtitlePath))?.RerunWith;

    /// <summary>
    /// Whether a subtitle's last check couldn't be done for want of speech-to-text (checked again after the others).
    /// </summary>
    /// <param name="subtitlePath">The subtitle file.</param>
    /// <returns><c>true</c> if it was deferred.</returns>
    public bool IsDeferred(string subtitlePath) => _results.Get(ResultStore.IdFor(subtitlePath))?.Status == ResultStatus.Deferred;

    /// <summary>
    /// Whether a subtitle's last check was deferred for want of speech-to-text on this very file (same fingerprint): its
    /// free line-start stage has already run and would only say the same again, so it waits until speech-to-text can be
    /// used.
    /// </summary>
    /// <param name="subtitlePath">The subtitle file.</param>
    /// <param name="fingerprint">Its fingerprint now.</param>
    /// <returns><c>true</c> if only speech-to-text can take it further.</returns>
    public bool WaitsForSpeech(string subtitlePath, string fingerprint)
        => _results.Get(ResultStore.IdFor(subtitlePath)) is { Status: ResultStatus.Deferred, RerunWith: null } r
            && string.Equals(r.Fingerprint, fingerprint, StringComparison.Ordinal);

    /// <summary>
    /// Asks for a subtitle to be checked again on the next run with the speech-to-text service that was chosen when its
    /// check fell back to a free one (or went on without speech-to-text). Nothing else about the result changes until
    /// then, so Undo still works.
    /// </summary>
    /// <param name="id">Result id.</param>
    /// <returns>The updated result.</returns>
    /// <exception cref="InvalidOperationException">No such result, or it didn't fall back.</exception>
    public SubtitleResult RequestRerun(string id)
    {
        var r = Find(id);
        Require(r, subtitle: true);
        if (!CanRerun(r))
        {
            throw new InvalidOperationException("This check didn't fall back from another speech-to-text service, so there is nothing to run again.");
        }

        return r.RerunWith is not null ? r : Save(r with { RerunWith = r.SpeechFallback!.From });
    }

    /// <summary>
    /// Whether a result can be checked again with the speech-to-text service first chosen: a subtitle file's own check
    /// (not a search, generated or embedded result) whose chosen service failed.
    /// </summary>
    /// <param name="r">The result.</param>
    /// <returns><c>true</c> if "Rerun with …" applies.</returns>
    public static bool CanRerun(SubtitleResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return r.SpeechFallback is not null && !r.Id.Contains('-', StringComparison.Ordinal) && File.Exists(r.SubtitlePath);
    }

    /// <summary>
    /// Whether the plugin has any result for a subtitle file (checked, or added by it).
    /// </summary>
    /// <param name="subtitlePath">The file.</param>
    /// <returns><c>true</c> if it has seen it.</returns>
    public bool Knows(string subtitlePath) => _results.ForPath(subtitlePath) is not null;

    /// <summary>Gets whether any subtitle has been checked yet (an install that has run before counts as set up).</summary>
    public bool HasResults => _results.All().Count > 0;

    /// <summary>Gets whether results can be recorded now (the results file is readable).</summary>
    public bool ResultsReadable => _results.Readable;

    /// <summary>Gets what is wrong with the results file, if anything.</summary>
    public string? ResultsProblem => _results.Problem;

    private static LineFinding FindingAt(SubtitleResult r, int index, double time)
        => index >= 0 && index < r.Findings.Count && Math.Abs(r.Findings[index].Time - time) < 0.01
            ? r.Findings[index]
            : throw new InvalidOperationException("That finding has changed since the page was loaded; reload the results.");

    // A subtitle found in sync by speech-to-text, with nothing flagged, teaches the confidence thresholds: its matched
    // words, and which of them would have been flagged. Never stops a check.
    private void Learn(SubtitleDocument document, Func<TimeSpan, TimeSpan> map, IReadOnlyList<(double Start, Transcript Transcript)> transcripts, string? language)
    {
        try
        {
            var words = transcripts.SelectMany(t => t.Transcript.Words.Select(w => w with { Start = w.Start + t.Start, End = w.End + t.Start })).OrderBy(w => w.Start).ToList();
            var coverage = transcripts.Select(t => (t.Start, t.Start + TranscriptSynchroniser.SnippetLength.TotalSeconds)).ToList();
            var report = DiscrepancyFinder.Find(document, words, t => map(TimeSpan.FromSeconds(t)).TotalSeconds, new DiscrepancyOptions { Language = language, Coverage = coverage });
            if (report.Problem is null)
            {
                Calibration!.Add(transcripts[0].Transcript.Provider, transcripts[0].Transcript.Model, report.Samples);
            }
        }
#pragma warning disable CA1031 // Learning is a by-product: it must never fail the check it learns from
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private SubtitleResult Save(SubtitleResult result)
    {
        _results.Put(result);
        return result;
    }
}

/// <summary>
/// What restoring all originals would do.
/// </summary>
/// <param name="ToRestore">Changed files that would get their original back.</param>
/// <param name="ToRemove">Added or generated subtitles that would be removed.</param>
public sealed record RestorePreview(int ToRestore, int ToRemove);

/// <summary>
/// A file restoring all originals left alone, and why.
/// </summary>
/// <param name="Name">The video's name.</param>
/// <param name="File">The subtitle's file name.</param>
/// <param name="Reason">Why, in plain words.</param>
public sealed record RestoreSkip(string Name, string File, string Reason);

/// <summary>
/// One batch of restoring all originals.
/// </summary>
/// <param name="Restored">Files whose original was put back.</param>
/// <param name="Removed">Added or generated subtitles removed.</param>
/// <param name="AlreadyGone">Added or generated subtitles that were already gone (recorded as undone).</param>
/// <param name="Skipped">Files left alone, with the reason.</param>
/// <param name="Next">The cursor for the next batch, or <c>null</c> when everything has been done.</param>
/// <param name="Left">How many files are left for later batches.</param>
public sealed record RestoreAllBatch(int Restored, int Removed, int AlreadyGone, IReadOnlyList<RestoreSkip> Skipped, string? Next, int Left)
{
    /// <summary>Gets the subtitle files changed or removed (to tell Jellyfin; not sent to the page).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> Touched { get; init; } = [];
}
