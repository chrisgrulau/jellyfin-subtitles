using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.Common.Storage;

namespace Jellyfin.Plugin.Subtitles.Discrepancy;

/// <summary>
/// What has been learned about one speech-to-text service and model.
/// </summary>
public sealed record CalibrationCounts
{
    /// <summary>Gets heard words matched to lines of subtitles known to be good, by confidence (hundredths).</summary>
    public IReadOnlyList<long> Matched { get; init; } = new long[ConfidenceCalibration.Bins];

    /// <summary>Gets those of them that would have been flagged (a name, number or negation differing), by confidence.</summary>
    public IReadOnlyList<long> Flagged { get; init; } = new long[ConfidenceCalibration.Bins];

    /// <summary>Gets when it was last updated.</summary>
    public DateTimeOffset Updated { get; init; }
}

/// <summary>
/// Confidence thresholds for flagging differences, per speech-to-text service and model, since confidence isn't
/// comparable across models. Each starts at a documented floor: Deepgram word confidence 0.90; Whisper-family services
/// (built-in, local, OpenAI) a word probability of e<sup>−0.3</sup> ≈ 0.74, the equivalent of Whisper's usual average
/// log-probability guard of −0.3. With <b>automatic tuning</b> switched on, a service's threshold is learned from the
/// subtitles already known to be good (in sync by speech-to-text, with nothing flagged): the lowest threshold at which
/// at most <see cref="TargetFlagged"/> of their matched words would be flagged, once at least <see cref="MinSamples"/>
/// matched words with a confidence have been seen. The learned threshold can only be stricter than the floor, never
/// looser, and is at most <see cref="MaxThreshold"/>. Counts are kept compactly (a hundred bins per service) in the data
/// folder.
/// </summary>
public sealed class ConfidenceCalibration
{
    /// <summary>The number of confidence bins (hundredths).</summary>
    public const int Bins = 100;

    /// <summary>The floor for Deepgram.</summary>
    public const double DeepgramFloor = 0.90;

    /// <summary>The share of known-good matched words that may be flagged at the learned threshold.</summary>
    public const double TargetFlagged = 0.02;

    /// <summary>The fewest matched words with a confidence before a threshold is learned.</summary>
    public const long MinSamples = 1000;

    /// <summary>The strictest threshold learned.</summary>
    public const double MaxThreshold = 0.99;

    /// <summary>The most services and models kept.</summary>
    public const int MaxSetups = 50;

    /// <summary>Counts above this are halved, so recent subtitles weigh more.</summary>
    public const long HalveAbove = 5_000_000;

    /// <summary>The floor for Whisper-family services: e^−0.3, Whisper's average log-probability guard as a probability.</summary>
    public static readonly double WhisperFloor = Math.Round(Math.Exp(-0.3), 3);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private Dictionary<string, CalibrationCounts>? _counts;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfidenceCalibration"/> class.
    /// </summary>
    /// <param name="path">The JSON file.</param>
    /// <param name="clock">Clock.</param>
    public ConfidenceCalibration(string path, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// The documented starting point for a service.
    /// </summary>
    /// <param name="provider">The provider id.</param>
    /// <returns>The floor.</returns>
    public static double FloorFor(string? provider)
        => string.Equals(provider, "deepgram", StringComparison.OrdinalIgnoreCase) ? DeepgramFloor
            : provider is "builtin" or "local" or "openai" ? WhisperFloor
            : DeepgramFloor;

    /// <summary>
    /// The key a service and model are counted under.
    /// </summary>
    /// <param name="provider">The provider id.</param>
    /// <param name="model">The model.</param>
    /// <returns>For example <c>deepgram/nova-3</c>.</returns>
    public static string KeyFor(string? provider, string? model)
        => (string.IsNullOrWhiteSpace(provider) ? "unknown" : provider.Trim()) + "/" + (string.IsNullOrWhiteSpace(model) ? "default" : model.Trim());

    /// <summary>
    /// The lowest threshold, not below the floor, at which at most <see cref="TargetFlagged"/> of the matched words would
    /// be flagged (a pure function of the counts).
    /// </summary>
    /// <param name="counts">The counts.</param>
    /// <param name="floor">The floor.</param>
    /// <returns>The threshold, or <c>null</c> while there are fewer than <see cref="MinSamples"/> matched words.</returns>
    public static double? Learn(CalibrationCounts counts, double floor)
    {
        ArgumentNullException.ThrowIfNull(counts);
        if (counts.Matched is null || counts.Flagged is null || counts.Matched.Count != Bins || counts.Flagged.Count != Bins)
        {
            return null;
        }

        var total = counts.Matched.Sum();
        if (total < MinSamples)
        {
            return null;
        }

        var allowed = TargetFlagged * total;

        // Flagged words at or above each bin
        var above = new long[Bins + 1];
        for (var b = Bins - 1; b >= 0; b--)
        {
            above[b] = above[b + 1] + counts.Flagged[b];
        }

        // The floor's own bin holds everything from the floor up (and a little below), so if it passes the floor does
        var start = (int)Math.Floor(floor * Bins);
        if (above[Math.Clamp(start, 0, Bins)] <= allowed)
        {
            return floor;
        }

        for (var b = start + 1; b < Bins; b++)
        {
            if (above[b] <= allowed)
            {
                return Math.Min(MaxThreshold, Math.Max(floor, b / (double)Bins));
            }
        }

        return MaxThreshold;
    }

    /// <summary>
    /// The threshold to use for a service and model: the floor, or, with <paramref name="tuned"/>, the learned threshold
    /// where there is one.
    /// </summary>
    /// <param name="provider">The provider id.</param>
    /// <param name="model">The model.</param>
    /// <param name="tuned">Whether automatic tuning is switched on.</param>
    /// <returns>The threshold.</returns>
    public double ThresholdFor(string? provider, string? model, bool tuned)
    {
        var floor = FloorFor(provider);
        if (!tuned)
        {
            return floor;
        }

        lock (_lock)
        {
            return Load().TryGetValue(KeyFor(provider, model), out var c) && Learn(c, floor) is { } learned ? learned : floor;
        }
    }

    /// <summary>
    /// The counts for a service and model, if any.
    /// </summary>
    /// <param name="provider">The provider id.</param>
    /// <param name="model">The model.</param>
    /// <returns>The counts, or <c>null</c>.</returns>
    public CalibrationCounts? CountsFor(string? provider, string? model)
    {
        lock (_lock)
        {
            return Load().TryGetValue(KeyFor(provider, model), out var c) ? c : null;
        }
    }

    /// <summary>
    /// Adds the matched words of a subtitle known to be good.
    /// </summary>
    /// <param name="provider">The provider id.</param>
    /// <param name="model">The model.</param>
    /// <param name="samples">The matched words.</param>
    /// <returns>How many were added.</returns>
    public int Add(string? provider, string? model, IEnumerable<WordSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var list = samples.Where(s => double.IsFinite(s.Confidence) && s.Confidence is >= 0 and <= 1).ToList();
        if (list.Count == 0)
        {
            return 0;
        }

        lock (_lock)
        {
            var all = Load();
            var key = KeyFor(provider, model);
            var c = all.TryGetValue(key, out var found) && found.Matched?.Count == Bins && found.Flagged?.Count == Bins ? found : new CalibrationCounts();
            var matched = c.Matched.ToArray();
            var flagged = c.Flagged.ToArray();
            foreach (var s in list)
            {
                var b = Math.Min(Bins - 1, (int)Math.Floor(s.Confidence * Bins));
                matched[b]++;
                flagged[b] += s.Flagged ? 1 : 0;
            }

            if (matched.Sum() > HalveAbove)
            {
                for (var b = 0; b < Bins; b++)
                {
                    matched[b] /= 2;
                    flagged[b] /= 2;
                }
            }

            all[key] = new CalibrationCounts { Matched = matched, Flagged = flagged, Updated = _clock.GetUtcNow() };
            foreach (var old in all.OrderByDescending(kv => kv.Value.Updated).Skip(MaxSetups).Select(kv => kv.Key).ToList())
            {
                all.Remove(old);
            }

            try
            {
                JsonFile.WriteAtomic(_path, all, JsonOptions);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                // Kept in memory; the next addition tries again
            }

            return list.Count;
        }
    }

    /// <summary>
    /// Describes the threshold for a service, for the results list.
    /// </summary>
    /// <param name="threshold">The threshold.</param>
    /// <param name="floor">The floor.</param>
    /// <returns>For example "0.93 (tuned)".</returns>
    public static string Describe(double threshold, double floor)
        => threshold.ToString("0.00", CultureInfo.InvariantCulture) + (threshold > floor + 1e-9 ? " (tuned)" : string.Empty);

    private Dictionary<string, CalibrationCounts> Load()
    {
        if (_counts is not null)
        {
            return _counts;
        }

        var read = JsonFile.Read<Dictionary<string, CalibrationCounts>>(_path, JsonOptions);
        _counts = read.IsLoaded && read.Value is { } v
            ? new Dictionary<string, CalibrationCounts>(v.Where(kv => kv.Value is not null), StringComparer.Ordinal)
            : new Dictionary<string, CalibrationCounts>(StringComparer.Ordinal);
        return _counts;
    }
}
