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
public sealed record SubtitleJob(Guid ItemId, string Name, string VideoPath, string SubtitlePath, string? Language, TimeSpan Duration, int AudioStream);

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
    /// The latest results, newest first.
    /// </summary>
    /// <param name="limit">How many.</param>
    /// <returns>The results.</returns>
    public IReadOnlyList<SubtitleResult> Recent(int limit)
    {
        // Everything waiting for review is always included, however old
        var all = _results.All();
        var recent = all.Take(Math.Clamp(limit, 1, 5000)).ToList();
        return [.. recent.Concat(all.Skip(recent.Count).Where(r => r.PendingReview))];
    }

    /// <summary>
    /// Drops results for subtitle files that were deleted (see <see cref="ResultStore.Prune"/>).
    /// </summary>
    /// <returns>How many were dropped.</returns>
    public int PruneGone() => _results.Prune(File.Exists, Directory.Exists);

    /// <summary>The pipeline version: raised when a new stage is added, so files are checked once more.</summary>
    public const int CurrentVersion = 3;

    /// <summary>How many change examples a result keeps.</summary>
    public const int MaxExamples = 12;

    /// <summary>The <see cref="SubtitleResult.Cleaned"/> key counting lines reworded from an audit.</summary>
    public const string RewordedKind = "RewordedFromAudit";

    /// <summary>
    /// Whether a subtitle needs checking: it hasn't been, it changed since, the check failed, or an older pipeline version
    /// checked it. A correction someone undid is never redone on its own.
    /// </summary>
    /// <param name="subtitlePath">The subtitle file.</param>
    /// <param name="fingerprint">Its current content fingerprint.</param>
    /// <returns><c>true</c> if it should be checked.</returns>
    public bool NeedsCheck(string subtitlePath, string fingerprint)
    {
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

        return r.Status != ResultStatus.Undone && (r.Status == ResultStatus.Failed || r.Version < CurrentVersion);
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

        var outcome = await new SyncCheck(audio, speech, refine: speech is not null, matcher: policies.Matcher)
            .RunAsync(document, job.Duration, Languages.ToTwoLetter(job.Language), cancellationToken).ConfigureAwait(false);
        var model = outcome.Model;
        var explanation = outcome.Note is null ? model.Explanation : model.Explanation + " " + outcome.Note;
        var status = outcome.WrongLanguageSuspected ? ResultStatus.WrongLanguage : model.Status switch
        {
            SyncStatus.InSync => ResultStatus.InSync,
            SyncStatus.Unreliable => ResultStatus.Unreliable,
            _ => policies.Timing == ChangePolicy.Automatic ? ResultStatus.Corrected : ResultStatus.Proposed,
        };
        result = result with
        {
            Status = status,
            Scale = status is ResultStatus.Corrected or ResultStatus.Proposed ? model.Scale : 1,
            Offset = status is ResultStatus.Corrected or ResultStatus.Proposed ? model.Offset : 0,
            Stage = outcome.Stage,
            Confidence = model.Confidence,
            Explanation = explanation,
        };

        // Timing first (when applied), then clean-up: what the policies allow now, the rest held for review
        // A subtitle that doesn't match the speech (another language, version, or a notes track) gets only the harmless
        // clean-up (adverts, empty lines): its timing isn't touched and nothing is suggested
        var timed = status == ResultStatus.Corrected ? document.Retime(model.Map) : document;
        var fitting = status != ResultStatus.WrongLanguage;
        var automatic = AutomaticOptions(policies);
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
            Examples = [.. applied.Select(c => Describe(c, false)).Concat(pending.Select(c => Describe(c, true))).Take(MaxExamples)],
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

        if (!writes)
        {
            return Save(result);
        }

        var (backup, written) = _files.Replace(job.SubtitlePath, fingerprint, SubtitleWriter.ToBytes(cleaned));
        return Save(result with { Backup = result.Backup ?? backup, Fingerprint = written, Changed = true });
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
        var r = _results.Get(id) ?? throw new InvalidOperationException("No such result.");
        if (!r.PendingReview)
        {
            throw new InvalidOperationException("Nothing is waiting for review for this subtitle.");
        }

        var bytes = File.ReadAllBytes(r.SubtitlePath);
        var document = SubtitleReader.Read(bytes, r.SubtitlePath) ?? throw new InvalidOperationException("The subtitle can no longer be read.");

        // Suggested wording first (the lines are found by their text and time as the file has them now), then timing
        var reworded = 0;
        if (r.Findings.Count > 0)
        {
            (document, reworded) = WordingAudit.Apply(document, r.Findings);
        }

        if (r.Status == ResultStatus.Proposed)
        {
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

            return Save(r with
            {
                Status = r.Status == ResultStatus.Proposed ? ResultStatus.Corrected : r.Status,
                Backup = r.Changed ? r.Backup ?? backup : backup,
                Fingerprint = written,
                Changed = true,
                Cleaned = reworded > 0 ? new Dictionary<string, int>(cleanedCounts, StringComparer.Ordinal) { [RewordedKind] = cleanedCounts.GetValueOrDefault(RewordedKind) + reworded } : cleanedCounts,
                CleanupPending = new Dictionary<string, int>(),
                Findings = [],
                Time = _clock.GetUtcNow(),
            });
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    /// <summary>
    /// Undoes this plugin's changes to a file: the first original comes back, if the file is still as this plugin left it.
    /// </summary>
    /// <param name="id">Result id.</param>
    /// <returns>The updated result.</returns>
    /// <exception cref="InvalidOperationException">Nothing to undo, or the file changed since.</exception>
    public SubtitleResult Undo(string id)
    {
        var r = _results.Get(id) ?? throw new InvalidOperationException("No such result.");
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
        Save(new SubtitleResult
        {
            Id = ResultStore.IdFor(job.SubtitlePath),
            ItemId = job.ItemId,
            Name = job.Name,
            SubtitlePath = job.SubtitlePath,
            Time = _clock.GetUtcNow(),
            Status = ResultStatus.Failed,
            Explanation = error,
        });
    }

    private SubtitleResult Save(SubtitleResult result)
    {
        _results.Put(result);
        return result;
    }
}
