using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Subtitles.Discrepancy;
using Jellyfin.Plugin.Subtitles.Generation;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// Stage 4: confidence thresholds learned from subtitles known to be good, and full transcripts kept for reuse
public sealed class CalibrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "subs-calibration-" + Guid.NewGuid().ToString("N"));

    public CalibrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static CalibrationCounts Counts(params (double Confidence, long Matched, long Flagged)[] bins)
    {
        var matched = new long[ConfidenceCalibration.Bins];
        var flagged = new long[ConfidenceCalibration.Bins];
        foreach (var (c, m, f) in bins)
        {
            var b = Math.Min(ConfidenceCalibration.Bins - 1, (int)Math.Floor(c * ConfidenceCalibration.Bins));
            matched[b] += m;
            flagged[b] += f;
        }

        return new CalibrationCounts { Matched = matched, Flagged = flagged };
    }

    [Fact]
    public void Each_service_starts_at_its_documented_floor()
    {
        Assert.Equal(0.90, ConfidenceCalibration.FloorFor("deepgram"));
        Assert.Equal(0.741, ConfidenceCalibration.FloorFor("builtin"), 3);
        Assert.Equal(ConfidenceCalibration.WhisperFloor, ConfidenceCalibration.FloorFor("local"));
        Assert.Equal(ConfidenceCalibration.WhisperFloor, ConfidenceCalibration.FloorFor("openai"));
        Assert.Equal(0.90, ConfidenceCalibration.FloorFor("someday-service"));
    }

    [Fact]
    public void Nothing_is_learned_from_too_few_words()
        => Assert.Null(ConfidenceCalibration.Learn(Counts((0.95, 999, 50)), 0.90));

    [Fact]
    public void The_threshold_is_the_lowest_that_flags_at_most_two_percent_of_known_good_words()
    {
        // 2,000 matched words: 100 flagged at 0.95, none above, so 0.96 flags none (at most 40 allowed)
        var counts = Counts((0.92, 900, 0), (0.95, 600, 100), (0.99, 500, 0));
        Assert.Equal(0.96, ConfidenceCalibration.Learn(counts, 0.90)!.Value, 3);

        // 30 flagged at 0.95 is within 2 %: the floor stands
        Assert.Equal(0.90, ConfidenceCalibration.Learn(Counts((0.92, 900, 0), (0.95, 600, 30), (0.99, 500, 0)), 0.90));
    }

    [Fact]
    public void The_learned_threshold_never_goes_below_the_floor_and_stops_at_the_ceiling()
    {
        // Flagged words only below the floor: the floor, never lower
        Assert.Equal(ConfidenceCalibration.WhisperFloor, ConfidenceCalibration.Learn(Counts((0.4, 1000, 500), (0.9, 1000, 0)), ConfidenceCalibration.WhisperFloor));

        // Flagged even at full confidence: the ceiling
        Assert.Equal(ConfidenceCalibration.MaxThreshold, ConfidenceCalibration.Learn(Counts((0.999, 1000, 500), (0.9, 1000, 0)), 0.90));
    }

    [Fact]
    public void Samples_are_stored_per_service_and_model_and_used_only_when_tuning_is_on()
    {
        var path = Path.Combine(_dir, "calibration.json");
        var calibration = new ConfidenceCalibration(path);
        var samples = Enumerable.Range(0, 2000).Select(i => new WordSample(i % 20 == 0 ? 0.95 : 0.91, i % 20 == 0)).ToList();
        Assert.Equal(2000, calibration.Add("deepgram", "nova-3", samples));

        Assert.Equal(0.90, calibration.ThresholdFor("deepgram", "nova-3", tuned: false));
        Assert.Equal(0.96, calibration.ThresholdFor("deepgram", "nova-3", tuned: true), 3);
        Assert.Equal(0.90, calibration.ThresholdFor("deepgram", "nova-2", tuned: true));

        // Kept on disk
        var again = new ConfidenceCalibration(path);
        Assert.Equal(2000, again.CountsFor("deepgram", "nova-3")!.Matched.Sum());
        Assert.Equal(0.96, again.ThresholdFor("deepgram", "nova-3", tuned: true), 3);
    }

    [Fact]
    public void Findings_confident_mishearings_of_good_subtitles_raise_the_threshold()
    {
        // Known-good subtitles whose transcript keeps mishearing a number and a name with 0.935 confidence
        var calibration = new ConfidenceCalibration(Path.Combine(_dir, "calibration.json"));
        var good = DiscrepancyTests.Subtitle(DiscrepancyTests.Script);
        var misheard = DiscrepancyTests.Said(DiscrepancyTests.Script)
            .Select(w => w.Text switch { "five" => w with { Text = "nine", Confidence = 0.935 }, "Katherine" => w with { Text = "Margaret", Confidence = 0.935 }, _ => w })
            .ToList();
        for (var i = 0; i < 40; i++)
        {
            calibration.Add("deepgram", "nova-3", DiscrepancyFinder.Find(good, misheard, t => t).Samples);
        }

        var threshold = calibration.ThresholdFor("deepgram", "nova-3", tuned: true);
        Assert.True(threshold > 0.935);

        // A doubtful subtitle with the same kind of mishearing is no longer flagged once tuned
        var options = new DiscrepancyOptions { MinConfidence = threshold };
        Assert.Empty(DiscrepancyFinder.Find(good, misheard, t => t, options).Findings);
        Assert.Equal(2, DiscrepancyFinder.Find(good, misheard, t => t, options with { MinConfidence = 0.90 }).Findings.Count);
    }

    [Fact]
    public void A_transcript_is_kept_compactly_and_read_back()
    {
        var cache = new TranscriptCache(Path.Combine(_dir, "transcripts"));
        var key = TranscriptCache.KeyFor("/v/Film.mkv", 1000, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 0, "en", "builtin/base");
        var full = new FullTranscript([new TranscribedWord("Hello,", 1.2345678, 1.5, 0.91), new TranscribedWord("there.", 1.6, 1.9, null)], "builtin", "base", 600, 2);
        cache.Put(key, full);

        var back = cache.Get(key)!;
        Assert.Equal("builtin", back.Provider);
        Assert.Equal("base", back.Model);
        Assert.Equal(2, back.Chunks);
        Assert.Equal(0, back.SecondsSent);
        Assert.Equal(["Hello,", "there."], back.Words.Select(w => w.Text));
        Assert.Equal(1.235, back.Words[0].Start, 3);
        Assert.Equal(0.91, back.Words[0].Confidence);
        Assert.Null(back.Words[1].Confidence);
        Assert.EndsWith(".json.gz", Assert.Single(Directory.GetFiles(Path.Combine(_dir, "transcripts"))), StringComparison.Ordinal);
    }

    [Fact]
    public void The_key_changes_with_the_file_the_track_the_language_and_the_service()
    {
        var at = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var key = TranscriptCache.KeyFor("/v/Film.mkv", 1000, at, 0, "en", "builtin/base");
        Assert.Equal(key, TranscriptCache.KeyFor("/v/Film.mkv", 1000, at, 0, "en", "builtin/base"));
        Assert.NotEqual(key, TranscriptCache.KeyFor("/v/Film.mkv", 1001, at, 0, "en", "builtin/base"));
        Assert.NotEqual(key, TranscriptCache.KeyFor("/v/Film.mkv", 1000, at.AddSeconds(1), 0, "en", "builtin/base"));
        Assert.NotEqual(key, TranscriptCache.KeyFor("/v/Film.mkv", 1000, at, 1, "en", "builtin/base"));
        Assert.NotEqual(key, TranscriptCache.KeyFor("/v/Film.mkv", 1000, at, 0, "fr", "builtin/base"));
        Assert.NotEqual(key, TranscriptCache.KeyFor("/v/Film.mkv", 1000, at, 0, "en", "deepgram/nova-3"));
        Assert.Null(TranscriptCache.KeyForFile(Path.Combine(_dir, "missing.mkv"), 0, "en", "builtin/base"));
    }

    [Fact]
    public void The_folder_is_kept_under_its_size_cap_least_recently_used_first()
    {
        var folder = Path.Combine(_dir, "transcripts");
        var words = Enumerable.Range(0, 300).Select(i => new TranscribedWord("w" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), i, i + 0.5, 0.9)).ToList();
        var keys = Enumerable.Range(0, 3).Select(i => TranscriptCache.KeyFor("/v/" + i, 1, DateTime.UnixEpoch, 0, "en", "s")).ToList();
        new TranscriptCache(folder).Put(keys[0], new FullTranscript(words, "p", "m", 0, 1));
        var size = new FileInfo(Path.Combine(folder, keys[0] + ".json.gz")).Length;

        // Room for two and a half
        var cache = new TranscriptCache(folder, maxBytes: (size * 5) / 2);
        File.SetLastWriteTimeUtc(Path.Combine(folder, keys[0] + ".json.gz"), DateTime.UtcNow.AddHours(-2));
        cache.Put(keys[1], new FullTranscript(words, "p", "m", 0, 1));
        File.SetLastWriteTimeUtc(Path.Combine(folder, keys[1] + ".json.gz"), DateTime.UtcNow.AddHours(-1));
        cache.Put(keys[2], new FullTranscript(words, "p", "m", 0, 1));

        Assert.Null(cache.Get(keys[0]));
        Assert.NotNull(cache.Get(keys[1]));
        Assert.NotNull(cache.Get(keys[2]));
    }

    [Fact]
    public void Odd_keys_and_damaged_files_are_ignored()
    {
        var folder = Path.Combine(_dir, "transcripts");
        var cache = new TranscriptCache(folder);
        cache.Put("../../escape", new FullTranscript([], "p", "m", 0, 1));
        Assert.False(Directory.Exists(folder) && Directory.GetFiles(folder).Length > 0);
        Assert.Null(cache.Get("../../escape"));

        var key = TranscriptCache.KeyFor("/v/x", 1, DateTime.UnixEpoch, 0, null, "s");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, key + ".json.gz"), "not gzip");
        Assert.Null(cache.Get(key));
        Assert.False(File.Exists(Path.Combine(folder, key + ".json.gz")));
    }
}
