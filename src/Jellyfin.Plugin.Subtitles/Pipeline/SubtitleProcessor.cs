using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.Audio;
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
public sealed record Policies(ChangePolicy Timing, ChangePolicy Text, CleanupSettings Cleanup);

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

        var outcome = await new SyncCheck(audio, speech, refine: speech is not null)
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

        if (status != ResultStatus.Corrected && applied.Count == 0)
        {
            return Save(result);
        }

        var (backup, written) = _files.Replace(job.SubtitlePath, fingerprint, SubtitleWriter.ToBytes(cleaned));
        return Save(result with { Backup = result.Backup ?? backup, Fingerprint = written, Changed = true });
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
                Cleaned = cleanedCounts,
                CleanupPending = new Dictionary<string, int>(),
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
