using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Jellyfin.Plugin.Subtitles.Pipeline;

/// <summary>
/// Counts subtitle downloads per day (UTC), so this plugin never uses more than its share of a provider's daily
/// download allowance (OpenSubtitles counts every download against the account). Kept on disk so a restart doesn't
/// reset the count.
/// </summary>
public sealed class DownloadLedger
{
    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadLedger"/> class.
    /// </summary>
    /// <param name="path">Absolute path of the ledger file.</param>
    /// <param name="clock">Clock.</param>
    public DownloadLedger(string path, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Downloads made today.
    /// </summary>
    /// <returns>The count.</returns>
    public int Today()
    {
        lock (_lock)
        {
            return Load().GetValueOrDefault(Day());
        }
    }

    /// <summary>
    /// Counts one download if today's limit allows it.
    /// </summary>
    /// <param name="limit">Downloads allowed per day.</param>
    /// <returns><c>false</c> when the limit is reached (nothing counted).</returns>
    public bool TryTake(int limit)
    {
        lock (_lock)
        {
            var counts = Load();
            var day = Day();
            var used = counts.GetValueOrDefault(day);
            if (used >= limit)
            {
                return false;
            }

            counts[day] = used + 1;

            // Only the last week is kept
            foreach (var old in counts.Keys.Where(k => string.CompareOrdinal(k, _clock.GetUtcNow().AddDays(-7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) < 0).ToList())
            {
                counts.Remove(old);
            }

            Save(counts);
            return true;
        }
    }

    private string Day() => _clock.GetUtcNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private Dictionary<string, int> Load()
    {
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(_path)) is { } counts)
            {
                return new Dictionary<string, int>(counts, StringComparer.Ordinal);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A damaged ledger counts from zero again; the provider's own limit still applies
        }

        return new Dictionary<string, int>(StringComparer.Ordinal);
    }

    private void Save(Dictionary<string, int> counts)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(counts));
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Counted in memory for this call only; worst case a few extra downloads
        }
    }
}
