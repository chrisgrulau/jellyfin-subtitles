using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

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

    /// <summary>Gets a value indicating whether anything waits for review (a timing correction or clean-up).</summary>
    public bool PendingReview => Status == ResultStatus.Proposed || CleanupPending.Count > 0;
}

/// <summary>
/// The latest result per subtitle file, persisted as JSON in the plugin's data folder. Damaged or unreadable files never
/// break the task; the newest <see cref="MaxResults"/> are kept.
/// </summary>
public sealed class ResultStore
{
    /// <summary>How many results are kept.</summary>
    public const int MaxResults = 2000;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _path;
    private readonly Lock _lock = new();
    private List<SubtitleResult>? _results;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResultStore"/> class.
    /// </summary>
    /// <param name="path">Absolute path of the JSON file.</param>
    public ResultStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>
    /// The stable id for a subtitle path.
    /// </summary>
    /// <param name="subtitlePath">The path.</param>
    /// <returns>A short hex id.</returns>
    public static string IdFor(string subtitlePath) => SubtitleFiles.BackupName(subtitlePath)[..16];

    /// <summary>
    /// All results, newest first.
    /// </summary>
    /// <returns>The results.</returns>
    public IReadOnlyList<SubtitleResult> All()
    {
        lock (_lock)
        {
            return [.. Load().OrderByDescending(r => r.Time)];
        }
    }

    /// <summary>
    /// Finds a result.
    /// </summary>
    /// <param name="id">Result id.</param>
    /// <returns>The result, or <c>null</c>.</returns>
    public SubtitleResult? Get(string id)
    {
        lock (_lock)
        {
            return Load().FirstOrDefault(r => r.Id == id);
        }
    }

    /// <summary>
    /// Finds the newest result that concerns a subtitle file (including one this plugin added).
    /// </summary>
    /// <param name="subtitlePath">The subtitle file.</param>
    /// <returns>The result, or <c>null</c>.</returns>
    public SubtitleResult? ForPath(string subtitlePath)
    {
        lock (_lock)
        {
            return Load().Where(r => string.Equals(r.SubtitlePath, subtitlePath, StringComparison.Ordinal)).OrderByDescending(r => r.Time).FirstOrDefault();
        }
    }

    /// <summary>
    /// Adds or replaces the result for a subtitle.
    /// </summary>
    /// <param name="result">The result.</param>
    public void Put(SubtitleResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_lock)
        {
            var list = Load();
            list.RemoveAll(r => r.Id == result.Id);
            list.Add(result);
            if (list.Count > MaxResults)
            {
                list.Sort((a, b) => b.Time.CompareTo(a.Time));
                list.RemoveRange(MaxResults, list.Count - MaxResults);
            }

            Save(list);
        }
    }

    private List<SubtitleResult> Load()
    {
        if (_results is not null)
        {
            return _results;
        }

        try
        {
            _results = File.Exists(_path) ? JsonSerializer.Deserialize<List<SubtitleResult>>(File.ReadAllText(_path), JsonOptions) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Only history: a damaged file means subtitles are checked again, nothing worse
            _results = null;
        }

        _results = [.. (_results ?? []).Where(r => r is not null && r.Id is not null && r.SubtitlePath is not null)];
        return _results;
    }

    private void Save(List<SubtitleResult> list)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(list, JsonOptions));
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory; the next save tries again
        }
    }
}
