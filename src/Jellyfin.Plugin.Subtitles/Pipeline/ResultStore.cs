using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Jellyfin.Plugin.Common.Storage;

namespace Jellyfin.Plugin.Subtitles.Pipeline;

/// <summary>
/// What happened to a subtitle.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ResultStatus>))]
public enum ResultStatus
{
    /// <summary>Already in sync; nothing changed.</summary>
    InSync = 0,

    /// <summary>Timing corrected (can be undone).</summary>
    Corrected,

    /// <summary>A correction was found and is waiting for review.</summary>
    Proposed,

    /// <summary>No clear answer; left alone.</summary>
    Unreliable,

    /// <summary>Speech was heard but doesn't match the text: another language, another version, or not dialogue (commentary, notes).</summary>
    WrongLanguage,

    /// <summary>Something went wrong (reading, writing, a service).</summary>
    Failed,

    /// <summary>A correction was undone.</summary>
    Undone,

    /// <summary>A missing subtitle was found, checked against the audio and added.</summary>
    Added,

    /// <summary>No subtitle that fits this video was found (searched again after a while).</summary>
    NotFound,

    /// <summary>The file is too large to be a subtitle; it isn't read again until it changes.</summary>
    TooLarge,

    /// <summary>Jellyfin's account can't write there (a read-only mount, permissions); tried again after a while.</summary>
    CantWrite,

    /// <summary>A proposed change was declined in review; nothing was changed, and it isn't proposed again unless the file changes.</summary>
    Declined,

    /// <summary>No subtitle was found, so one was generated from a full transcript and added (labelled as generated; Undo removes it).</summary>
    Generated,

    /// <summary>A full transcript heard almost no speech, so nothing was generated; not tried again unless the service or model changes.</summary>
    NoSpeech,

    /// <summary>A generated subtitle was replaced by one found later (the generated file was removed; a copy is kept).</summary>
    Replaced,
}

/// <summary>
/// A line whose meaning differs from what is said, found by an audit.
/// </summary>
/// <param name="Time">When the line starts, in the file's time (to find it again).</param>
/// <param name="Current">The line's text as it was in the file (to find it again).</param>
/// <param name="Suggestion">The line as it should read, or <c>null</c> when it is only flagged.</param>
/// <param name="Kind">What differs: name, number, negation, missing, wrong or extra.</param>
/// <param name="Reason">Why, in one sentence.</param>
public sealed record LineFinding(double Time, string Current, string? Suggestion, string Kind, string Reason)
{
    /// <summary>Gets which check found it: <c>null</c> for the wording audit, <see cref="Discrepancy.DiscrepancyReview.WholeFile"/> for the whole-file check.</summary>
    public string? From { get; init; }

    /// <summary>Gets what was heard there (whole-file check), if anything.</summary>
    public string? Heard { get; init; }

    /// <summary>Gets when a line heard but missing from the subtitle ends, in the file's time (it is added from <see cref="Time"/> to here).</summary>
    public double? End { get; init; }
}

/// <summary>
/// A whole subtitle file compared with a full transcript of its video.
/// </summary>
public sealed record WholeFileCheck
{
    /// <summary>Gets when.</summary>
    public DateTimeOffset Time { get; init; }

    /// <summary>Gets the speech-to-text service and model (for example <c>builtin/base</c>).</summary>
    public string Setup { get; init; } = string.Empty;

    /// <summary>Gets the lines found to differ, by kind (before any were applied or declined).</summary>
    public IReadOnlyDictionary<string, int> Counts { get; init; } = new Dictionary<string, int>();

    /// <summary>Gets what the check found, in one or two sentences.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether it couldn't be done (the transcript failed); tried again after a while.</summary>
    public bool Failed { get; init; }
}

/// <summary>
/// The latest result for one subtitle file.
/// </summary>
public sealed record SubtitleResult
{
    /// <summary>Gets a stable id (from the subtitle's path).</summary>
    public required string Id { get; init; }

    /// <summary>Gets the library item id.</summary>
    public Guid ItemId { get; init; }

    /// <summary>Gets the item's name, for display.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the subtitle file.</summary>
    public required string SubtitlePath { get; init; }

    /// <summary>Gets the video, for results about a video rather than a file that exists (generated subtitles), if recorded.</summary>
    public string? VideoPath { get; init; }

    /// <summary>Gets when it was checked.</summary>
    public DateTimeOffset Time { get; init; }

    /// <summary>Gets the outcome.</summary>
    public ResultStatus Status { get; init; }

    /// <summary>Gets the frame-rate ratio found.</summary>
    public double Scale { get; init; } = 1;

    /// <summary>Gets the offset found, in seconds.</summary>
    public double Offset { get; init; }

    /// <summary>Gets which stage decided.</summary>
    public string Stage { get; init; } = string.Empty;

    /// <summary>Gets the explanation shown to people.</summary>
    public string Explanation { get; init; } = string.Empty;

    /// <summary>Gets the confidence, 0 to 1.</summary>
    public double Confidence { get; init; }

    /// <summary>Gets the fingerprint of the file as this plugin last saw or left it (unchanged since → not checked again).</summary>
    public string Fingerprint { get; init; } = string.Empty;

    /// <summary>Gets the backup of the original, when the file was changed.</summary>
    public string? Backup { get; init; }

    /// <summary>Gets the pipeline version that produced this result (a newer version checks files again once).</summary>
    public int Version { get; init; }

    /// <summary>Gets a value indicating whether the file currently holds this plugin's changes (so it can be undone).</summary>
    public bool Changed { get; init; }

    /// <summary>Gets the clean-up changes applied, by kind.</summary>
    public IReadOnlyDictionary<string, int> Cleaned { get; init; } = new Dictionary<string, int>();

    /// <summary>Gets the clean-up changes waiting for review, by kind.</summary>
    public IReadOnlyDictionary<string, int> CleanupPending { get; init; } = new Dictionary<string, int>();

    /// <summary>Gets a few examples of what was (or would be) changed, for review.</summary>
    public IReadOnlyList<string> Examples { get; init; } = [];

    /// <summary>Gets where an added subtitle came from (source, release name, score), for provenance.</summary>
    public string? Origin { get; init; }

    /// <summary>Gets the speech-to-text service the check used (empty when none), so an unclear result is checked again once one is set up.</summary>
    public string SpeechSetup { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether the wording was audited (so the nightly audit of earlier results skips it).</summary>
    public bool Audited { get; init; }

    /// <summary>Gets lines whose meaning differs from what is said (from an audit), with suggested wording where given.</summary>
    public IReadOnlyList<LineFinding> Findings { get; init; } = [];

    /// <summary>Gets the latest comparison of the whole file with a full transcript, if one was made.</summary>
    public WholeFileCheck? WholeFile { get; init; }

    /// <summary>Gets a value indicating whether the whole file is to be compared with a full transcript on the next run (asked for from the results).</summary>
    public bool WholeFileRequested { get; init; }

    /// <summary>Gets a value indicating whether anything waits for review (a timing correction, clean-up, suggested wording or a line the whole-file check flagged).</summary>
    public bool PendingReview => Status == ResultStatus.Proposed || CleanupPending.Count > 0 || Findings.Any(f => f.Suggestion is not null || f.From is not null);
}

/// <summary>
/// The latest result per subtitle file (and per search for a missing one), persisted as JSON in the plugin's data folder.
/// It is also the record of what was checked, changed and can be undone, so results are not evicted by age: one is kept
/// per file for as long as the file exists (see <see cref="Prune"/>). Only past <see cref="MaxResults"/> (far beyond a
/// large library) are the oldest plain results dropped, never one that can be undone, was added, waits for review, or
/// holds back a search. Damaged or unreadable files never break the task.
/// <para>
/// Results are indexed by id and by path. The file is written in batches: a result that records a change to a file (or
/// its undo), a review decision or an added subtitle is written at once; plain results are written every
/// <see cref="SaveEvery"/> results or <see cref="SaveAfter"/>, and when a run ends (<see cref="Flush"/>). A crash loses
/// at most those few plain results, and their files are simply checked again.
/// </para>
/// </summary>
public sealed class ResultStore : IDisposable
{
    /// <summary>The ceiling on kept results.</summary>
    public const int MaxResults = 200_000;

    /// <summary>How many plain results may wait before the file is written.</summary>
    public const int SaveEvery = 25;

    /// <summary>How long plain results may wait before the file is written.</summary>
    public static readonly TimeSpan SaveAfter = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly int _maxResults;
    private readonly string _path;
    private readonly Lock _lock = new();
    private Dictionary<string, SubtitleResult>? _byId;
    private Dictionary<string, List<SubtitleResult>> _byPath = new(StringComparer.Ordinal);
    private int _unsaved;
    private long? _lastSave;

    /// <summary>
    /// Gets what is wrong with the results file, if anything (it couldn't be read, or it was damaged and set aside).
    /// </summary>
    public string? Problem { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the results file can be read now (so runs may go ahead and record results).
    /// </summary>
    public bool Readable
    {
        get
        {
            lock (_lock)
            {
                return Load() is not null;
            }
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ResultStore"/> class.
    /// </summary>
    /// <param name="path">Absolute path of the JSON file.</param>
    /// <param name="maxResults">The ceiling (for tests).</param>
    public ResultStore(string path, int maxResults = MaxResults)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxResults, 1);
        _path = path;
        _maxResults = maxResults;
    }

    /// <summary>
    /// Whether a result must be kept whatever its age: it can be undone, a subtitle was added, something waits for review,
    /// or it holds back a new search.
    /// </summary>
    /// <param name="r">The result.</param>
    /// <param name="now">The current time.</param>
    /// <returns><c>true</c> if it must be kept.</returns>
    public static bool MustKeep(SubtitleResult r, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(r);
        return r.Changed || r.PendingReview || r.WholeFileRequested || r.Status is ResultStatus.Added or ResultStatus.Undone or ResultStatus.Generated or ResultStatus.NoSpeech
            || (r.Status is ResultStatus.NotFound or ResultStatus.Failed && r.Id.StartsWith("find-", StringComparison.Ordinal) && now - r.Time < SubtitleFinder.SearchAgainAfter);
    }

    /// <summary>
    /// Whether recording a result must reach the disk at once rather than with the next batch: it records a change to a
    /// file or its undo (the only record of how to undo it), a review decision, or an added subtitle.
    /// </summary>
    /// <param name="result">The new result.</param>
    /// <param name="previous">The result it replaces, if any.</param>
    /// <returns><c>true</c> to write at once.</returns>
    public static bool SaveAtOnce(SubtitleResult result, SubtitleResult? previous)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Changed || previous?.Changed == true || result.PendingReview || previous?.PendingReview == true || result.WholeFileRequested != (previous?.WholeFileRequested ?? false)
            || result.Status is ResultStatus.Added or ResultStatus.Undone or ResultStatus.Declined or ResultStatus.Generated or ResultStatus.Replaced;
    }

    /// <summary>
    /// Drops results for subtitle files that are gone, where the folder is still there (so an offline share never loses
    /// its results). "Nothing found" results are for files that don't exist yet and are kept. Results of generating a
    /// subtitle stand for the video (a generated file someone deleted isn't generated again), so they go only with the
    /// video.
    /// </summary>
    /// <param name="fileExists">Whether a file exists.</param>
    /// <param name="folderExists">Whether a folder exists.</param>
    /// <returns>How many were dropped.</returns>
    public int Prune(Func<string, bool> fileExists, Func<string, bool> folderExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(folderExists);
        lock (_lock)
        {
            if (Load() is not { } all)
            {
                return 0;
            }

            var gone = all.Values.Where(r => r.Status != ResultStatus.NotFound
                && (r.Id.StartsWith(SubtitleGenerator.IdPrefix, StringComparison.Ordinal) ? r.VideoPath : r.SubtitlePath) is { } path
                && !fileExists(path)
                && Path.GetDirectoryName(path) is { } folder && folderExists(folder)).ToList();
            foreach (var r in gone)
            {
                Unindex(r);
            }

            if (gone.Count > 0)
            {
                Save();
            }

            return gone.Count;
        }
    }

    /// <summary>
    /// The stable id for a subtitle path: the first 16 hex digits (lower case) of the SHA-256 of its UTF-8 bytes. Hashed
    /// here rather than borrowed from backup naming, so a change there can't orphan every stored result; it must keep
    /// producing the ids already stored.
    /// </summary>
    /// <param name="subtitlePath">The path.</param>
    /// <returns>A short hex id.</returns>
    public static string IdFor(string subtitlePath)
    {
        ArgumentNullException.ThrowIfNull(subtitlePath);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(subtitlePath)))[..16];
    }

    /// <summary>
    /// All results, newest first.
    /// </summary>
    /// <returns>The results.</returns>
    public IReadOnlyList<SubtitleResult> All()
    {
        lock (_lock)
        {
            return Load() is { } all ? [.. all.Values.OrderByDescending(r => r.Time)] : [];
        }
    }

    /// <summary>
    /// Finds a result.
    /// </summary>
    /// <param name="id">Result id.</param>
    /// <returns>The result, or <c>null</c>.</returns>
    public SubtitleResult? Get(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_lock)
        {
            return Load() is null ? null : Find(id);
        }
    }

    /// <summary>
    /// Finds a result for one action from the settings page (apply, undo, edit …). This compares ids one by one rather
    /// than using the page's id as an index key, so the analysers still see that the file paths acted on come from this
    /// store, not from the request; one scan per click costs nothing. Runs use <see cref="Get"/>.
    /// </summary>
    /// <param name="id">Result id, as sent by the page.</param>
    /// <returns>The result, or <c>null</c>.</returns>
    public SubtitleResult? FindForRequest(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_lock)
        {
            return Load() is { } all ? all.Values.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal)) : null;
        }
    }

    /// <summary>
    /// Finds the newest result that concerns a subtitle file (including one this plugin added).
    /// </summary>
    /// <param name="subtitlePath">The subtitle file.</param>
    /// <returns>The result, or <c>null</c>.</returns>
    public SubtitleResult? ForPath(string subtitlePath)
    {
        ArgumentNullException.ThrowIfNull(subtitlePath);
        lock (_lock)
        {
            return Load() is not null && _byPath.TryGetValue(subtitlePath, out var list) ? list.MaxBy(r => r.Time) : null;
        }
    }

    /// <summary>
    /// Forgets a result, so its file is checked (or its video searched) again as new.
    /// </summary>
    /// <param name="id">Result id.</param>
    /// <returns>Whether there was one.</returns>
    public bool Remove(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_lock)
        {
            if (Load() is not { } all || !all.TryGetValue(id, out var r))
            {
                return false;
            }

            Unindex(r);
            Save();
            return true;
        }
    }

    /// <summary>
    /// Gets or sets what else happens when a result is recorded (copying it to Jellyfin's Activity log).
    /// </summary>
    public Action<SubtitleResult>? Recorded { get; set; }

    /// <summary>
    /// Adds or replaces the result for a subtitle.
    /// </summary>
    /// <param name="result">The result.</param>
    /// <exception cref="InvalidOperationException">The results file can't be read, so nothing is recorded.</exception>
    public void Put(SubtitleResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_lock)
        {
            // Never write over a results file that couldn't be read (its results were never loaded)
            var all = Load() ?? throw new InvalidOperationException(Problem ?? "The results file can't be read, so nothing is recorded.");
            all.TryGetValue(result.Id, out var previous);
            if (previous is not null)
            {
                Unindex(previous);
            }

            Index(result);
            if (all.Count > _maxResults)
            {
                // Past the ceiling, the oldest results that nothing depends on go first
                var now = result.Time;
                foreach (var r in all.Values.Where(r => !MustKeep(r, now) && r.Id != result.Id).OrderBy(r => r.Time).Take(all.Count - _maxResults).ToList())
                {
                    Unindex(r);
                }
            }

            _unsaved++;
            if (SaveAtOnce(result, previous) || _unsaved >= SaveEvery || _lastSave is not { } last || Environment.TickCount64 - last >= (long)SaveAfter.TotalMilliseconds)
            {
                Save();
            }
        }

        Recorded?.Invoke(result);
    }

    /// <summary>
    /// Writes any results still waiting (at the end of a run, and when the server stops).
    /// </summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (_unsaved > 0 && _byId is not null)
            {
                Save();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => Flush();

    private SubtitleResult? Find(string id) => _byId!.TryGetValue(id, out var r) ? r : null;

    private void Index(SubtitleResult r)
    {
        _byId![r.Id] = r;
        if (!_byPath.TryGetValue(r.SubtitlePath, out var list))
        {
            _byPath[r.SubtitlePath] = list = [];
        }

        list.Add(r);
    }

    private void Unindex(SubtitleResult r)
    {
        _byId!.Remove(r.Id);
        if (_byPath.TryGetValue(r.SubtitlePath, out var list))
        {
            list.RemoveAll(x => x.Id == r.Id);
            if (list.Count == 0)
            {
                _byPath.Remove(r.SubtitlePath);
            }
        }
    }

    // null while the file can't be read
    private Dictionary<string, SubtitleResult>? Load()
    {
        if (_byId is not null)
        {
            return _byId;
        }

        // The policy for each state of the file (FAM-06's JsonFile tells them apart)
        var read = JsonFile.Read<List<SubtitleResult>>(_path, JsonOptions);
        List<SubtitleResult>? loaded;
        switch (read.State)
        {
            case JsonFileState.Unreadable:
                // Unreadable for now (locked, permissions, a share hiccup): this file holds the undo records, so it is never
                // replaced. Nothing is cached, so nothing can be saved over it, and the next access reads it again.
                Problem = "The results file can't be read right now (" + read.Error!.GetType().Name + "); nothing is checked or recorded until it can be.";
                return null;
            case JsonFileState.Damaged:
                // Damaged: set it aside (with the undo records it held, for recovery by hand) and start afresh
                if (JsonFile.SetAside(_path) is not { } aside)
                {
                    Problem = "The results file is damaged and couldn't be set aside; nothing is recorded until it is moved or deleted.";
                    return null;
                }

                Problem = "The results file was damaged and was set aside as " + Path.GetFileName(aside) + "; results start afresh (the originals of changed files are still in the originals folder).";
                loaded = null;
                break;
            default:
                loaded = read.Value;
                break;
        }

        _byId = new Dictionary<string, SubtitleResult>(StringComparer.Ordinal);
        _byPath = new Dictionary<string, List<SubtitleResult>>(StringComparer.Ordinal);
        foreach (var r in (loaded ?? []).Where(r => r is not null && r.Id is not null && r.SubtitlePath is not null))
        {
            if (_byId.TryGetValue(r.Id, out var older))
            {
                Unindex(older);
            }

            Index(r);
        }

        if (Problem is not null && Problem.StartsWith("The results file can't", StringComparison.Ordinal))
        {
            Problem = null;
        }

        return _byId;
    }

    private void Save()
    {
        try
        {
            JsonFile.WriteAtomic(_path, _byId!.Values, JsonOptions);
            _unsaved = 0;
            _lastSave = Environment.TickCount64;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory; the next save tries again
        }
    }
}
