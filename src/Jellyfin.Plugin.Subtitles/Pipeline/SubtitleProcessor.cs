using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.Audio;
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
    public IReadOnlyList<SubtitleResult> Recent(int limit) => [.. _results.All().Take(Math.Clamp(limit, 1, ResultStore.MaxResults))];

    /// <summary>
    /// Whether a subtitle needs checking: it hasn't been, or it changed since (a failed check is tried again).
    /// </summary>
    /// <param name="subtitlePath">The subtitle file.</param>
    /// <param name="fingerprint">Its current content fingerprint.</param>
    /// <returns><c>true</c> if it should be checked.</returns>
    public bool NeedsCheck(string subtitlePath, string fingerprint)
        => _results.Get(ResultStore.IdFor(subtitlePath)) is not { } r || r.Status == ResultStatus.Failed || !string.Equals(r.Fingerprint, fingerprint, StringComparison.Ordinal);

    /// <summary>
    /// Checks a subtitle and records the result.
    /// </summary>
    /// <param name="job">The subtitle.</param>
    /// <param name="audio">The video's audio.</param>
    /// <param name="speech">Speech-to-text, if available (free services only for automatic runs).</param>
    /// <param name="timingPolicy">Whether corrections are applied or held for review.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result.</returns>
    public async Task<SubtitleResult> ProcessAsync(SubtitleJob job, IAudioSource audio, ISpeechToText? speech, ChangePolicy timingPolicy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var bytes = await File.ReadAllBytesAsync(job.SubtitlePath, cancellationToken).ConfigureAwait(false);
        var fingerprint = SubtitleFiles.Fingerprint(bytes);
        var result = new SubtitleResult { Id = ResultStore.IdFor(job.SubtitlePath), ItemId = job.ItemId, Name = job.Name, SubtitlePath = job.SubtitlePath, Time = _clock.GetUtcNow(), Fingerprint = fingerprint };

        var document = SubtitleReader.Read(bytes, job.SubtitlePath);
        if (document is null || document.Cues.Count == 0)
        {
            return Save(result with { Status = ResultStatus.Failed, Explanation = "Not a readable text subtitle." });
        }

        var outcome = await new SyncCheck(audio, speech, refine: speech is not null)
            .RunAsync(document, job.Duration, Languages.ToTwoLetter(job.Language), cancellationToken).ConfigureAwait(false);
        var model = outcome.Model;
        var explanation = outcome.Note is null ? model.Explanation : model.Explanation + " " + outcome.Note;
        result = result with { Scale = model.Scale, Offset = model.Offset, Stage = outcome.Stage, Confidence = model.Confidence, Explanation = explanation };

        if (outcome.WrongLanguageSuspected)
        {
            return Save(result with { Status = ResultStatus.WrongLanguage, Scale = 1, Offset = 0 });
        }

        switch (model.Status)
        {
            case SyncStatus.InSync:
                return Save(result with { Status = ResultStatus.InSync });
            case SyncStatus.Unreliable:
                return Save(result with { Status = ResultStatus.Unreliable });
        }

        if (timingPolicy == ChangePolicy.Review)
        {
            return Save(result with { Status = ResultStatus.Proposed });
        }

        var (backup, written) = _files.Replace(job.SubtitlePath, fingerprint, SubtitleWriter.ToBytes(document.Retime(model.Map)));
        return Save(result with { Status = ResultStatus.Corrected, Backup = backup, Fingerprint = written });
    }

    /// <summary>
    /// Applies a proposed correction.
    /// </summary>
    /// <param name="id">Result id.</param>
    /// <returns>The updated result.</returns>
    /// <exception cref="InvalidOperationException">There's no proposal, or the file changed since.</exception>
    public SubtitleResult Apply(string id)
    {
        var r = _results.Get(id) ?? throw new InvalidOperationException("No such result.");
        if (r.Status != ResultStatus.Proposed)
        {
            throw new InvalidOperationException("There's no correction waiting for this subtitle.");
        }

        var bytes = File.ReadAllBytes(r.SubtitlePath);
        var document = SubtitleReader.Read(bytes, r.SubtitlePath) ?? throw new InvalidOperationException("The subtitle can no longer be read.");
        var model = new SyncModel(SyncStatus.Corrected, r.Scale, r.Offset, r.Confidence, [], r.Explanation);
        try
        {
            var (backup, written) = _files.Replace(r.SubtitlePath, r.Fingerprint, SubtitleWriter.ToBytes(document.Retime(model.Map)));
            return Save(r with { Status = ResultStatus.Corrected, Backup = backup, Fingerprint = written, Time = _clock.GetUtcNow() });
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    /// <summary>
    /// Undoes a correction: the original comes back, if the file is still as this plugin left it.
    /// </summary>
    /// <param name="id">Result id.</param>
    /// <returns>The updated result.</returns>
    /// <exception cref="InvalidOperationException">Nothing to undo, or the file changed since.</exception>
    public SubtitleResult Undo(string id)
    {
        var r = _results.Get(id) ?? throw new InvalidOperationException("No such result.");
        if (r.Status != ResultStatus.Corrected || r.Backup is null)
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
                Time = _clock.GetUtcNow(),
                Explanation = string.Create(CultureInfo.InvariantCulture, $"Undone: the original timing is back (the correction was {r.Offset:+0.00;-0.00} s)."),
            });
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
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
