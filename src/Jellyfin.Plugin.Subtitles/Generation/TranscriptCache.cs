using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.Subtitles.SpeechToText;

namespace Jellyfin.Plugin.Subtitles.Generation;

/// <summary>
/// Full transcripts kept in the data folder, so a video's audio is never paid for (or transcribed on a CPU for hours)
/// twice: generating a subtitle and checking a whole subtitle file share them, and a second check is free. Each is keyed
/// by the video file (path, size and time written), the audio stream, the language and the service and model; stored
/// gzipped, a word per compact array. The folder is kept under a size cap, the least recently used going first.
/// </summary>
public sealed class TranscriptCache
{
    /// <summary>The default size cap for the folder, in bytes.</summary>
    public const long DefaultMaxBytes = 200L * 1024 * 1024;

    /// <summary>The most words read back from one file.</summary>
    public const int MaxWords = 500_000;

    private const string Extension = ".json.gz";

    private readonly string _folder;
    private readonly long _maxBytes;
    private readonly Lock _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="TranscriptCache"/> class.
    /// </summary>
    /// <param name="folder">The folder.</param>
    /// <param name="maxBytes">The size cap.</param>
    public TranscriptCache(string folder, long maxBytes = DefaultMaxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        _folder = folder;
        _maxBytes = maxBytes;
    }

    /// <summary>
    /// The key of a video's transcript.
    /// </summary>
    /// <param name="videoPath">The video.</param>
    /// <param name="size">Its size.</param>
    /// <param name="written">When it was last written (UTC).</param>
    /// <param name="audioStream">The audio stream listened to.</param>
    /// <param name="language">The language transcribed in.</param>
    /// <param name="setup">The service and model (for example <c>builtin/base</c>).</param>
    /// <returns>The key (hex).</returns>
    public static string KeyFor(string videoPath, long size, DateTime written, int audioStream, string? language, string setup)
    {
        ArgumentNullException.ThrowIfNull(videoPath);
        var text = string.Join('|', videoPath, size.ToString(CultureInfo.InvariantCulture), written.Ticks.ToString(CultureInfo.InvariantCulture), audioStream.ToString(CultureInfo.InvariantCulture), language ?? string.Empty, setup ?? string.Empty);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
    }

    /// <summary>
    /// The key of a video file's transcript as it is now.
    /// </summary>
    /// <param name="videoPath">The video.</param>
    /// <param name="audioStream">The audio stream listened to.</param>
    /// <param name="language">The language transcribed in.</param>
    /// <param name="setup">The service and model.</param>
    /// <returns>The key, or <c>null</c> if the file can't be read.</returns>
    public static string? KeyForFile(string videoPath, int audioStream, string? language, string setup)
    {
        try
        {
            var info = new FileInfo(videoPath);
            return info.Exists ? KeyFor(videoPath, info.Length, info.LastWriteTimeUtc, audioStream, language, setup) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// A cached transcript (marked as used, so it is kept longer).
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>The transcript (nothing sent), or <c>null</c>.</returns>
    public FullTranscript? Get(string? key)
    {
        if (key is null || !IsKey(key))
        {
            return null;
        }

        lock (_lock)
        {
            var path = Path.Combine(_folder, key + Extension);
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                using var file = File.OpenRead(path);
                using var gzip = new GZipStream(file, CompressionMode.Decompress);
                using var doc = JsonDocument.Parse(gzip);
                var root = doc.RootElement;
                var words = new List<TranscribedWord>();
                foreach (var w in root.GetProperty("w").EnumerateArray())
                {
                    if (words.Count >= MaxWords || w.GetArrayLength() < 3)
                    {
                        break;
                    }

                    double? confidence = w.GetArrayLength() > 3 && w[3].ValueKind == JsonValueKind.Number ? w[3].GetDouble() : null;
                    words.Add(new TranscribedWord(w[0].GetString() ?? string.Empty, w[1].GetDouble(), w[2].GetDouble(), confidence));
                }

                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                return new FullTranscript(words, root.GetProperty("p").GetString() ?? string.Empty, root.GetProperty("m").GetString() ?? string.Empty, 0, root.GetProperty("c").GetInt32());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or InvalidDataException or FormatException)
            {
                // Damaged or unreadable: it is transcribed again (and written afresh)
                TryDelete(path);
                return null;
            }
        }
    }

    /// <summary>
    /// Keeps a transcript, then trims the folder to its size cap.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="transcript">The transcript.</param>
    public void Put(string? key, FullTranscript transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        if (key is null || !IsKey(key))
        {
            return;
        }

        lock (_lock)
        {
            var path = Path.Combine(_folder, key + Extension);
            var temp = path + ".tmp";
            try
            {
                Directory.CreateDirectory(_folder);
                using (var file = File.Create(temp))
                using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
                using (var json = new Utf8JsonWriter(gzip))
                {
                    json.WriteStartObject();
                    json.WriteNumber("v", 1);
                    json.WriteString("p", transcript.Provider);
                    json.WriteString("m", transcript.Model);
                    json.WriteNumber("c", transcript.Chunks);
                    json.WriteStartArray("w");
                    foreach (var w in transcript.Words.Take(MaxWords))
                    {
                        json.WriteStartArray();
                        json.WriteStringValue(w.Text.Length > 100 ? w.Text[..100] : w.Text);
                        json.WriteNumberValue(Math.Round(w.Start, 3));
                        json.WriteNumberValue(Math.Round(w.End, 3));
                        if (w.Confidence is { } c)
                        {
                            json.WriteNumberValue(Math.Round(c, 3));
                        }

                        json.WriteEndArray();
                    }

                    json.WriteEndArray();
                    json.WriteEndObject();
                }

                File.Move(temp, path, overwrite: true);
                Prune();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not cached this time; nothing else depends on it
                TryDelete(temp);
            }
        }
    }

    /// <summary>
    /// Removes the least recently used transcripts until the folder is under its size cap.
    /// </summary>
    /// <returns>How many were removed.</returns>
    public int Prune()
    {
        lock (_lock)
        {
            try
            {
                if (!Directory.Exists(_folder))
                {
                    return 0;
                }

                var files = new DirectoryInfo(_folder).GetFiles("*" + Extension).OrderBy(f => f.LastWriteTimeUtc).ToList();
                var total = files.Sum(f => f.Length);
                var removed = 0;
                foreach (var f in files)
                {
                    if (total <= _maxBytes)
                    {
                        break;
                    }

                    total -= f.Length;
                    TryDelete(f.FullName);
                    removed++;
                }

                return removed;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
        }
    }

    private static bool IsKey(string key) => key.Length == 32 && key.All(char.IsAsciiHexDigitLower);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next prune
        }
    }
}
