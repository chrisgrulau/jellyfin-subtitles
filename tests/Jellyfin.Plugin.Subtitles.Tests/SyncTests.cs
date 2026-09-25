using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.Audio;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.Sync;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// Synthetic audio: speech-band sound where "someone speaks", faint noise elsewhere.
public class SyncTests
{
    private const int Rate = AudioFormat.SampleRate;

    /// <summary>Invented dialogue timing: lines of 1–4 s with gaps of 0.3–6 s, over the whole video.</summary>
    private static List<(double Start, double End)> Dialogue(double duration, int seed)
    {
        var random = new Random(seed);
        var lines = new List<(double, double)>();
        for (var t = 5.0; t < duration - 5;)
        {
            var length = 1 + (random.NextDouble() * 3);
            lines.Add((t, t + length));
            t += length + 0.3 + (random.NextDouble() * 5.7);
        }

        return lines;
    }

    private static SubtitleDocument Subtitles(IEnumerable<(double Start, double End)> lines) => new()
    {
        Format = SubtitleFormat.Srt,
        Cues = [.. lines.Select((l, i) => new SubtitleCue { Start = TimeSpan.FromSeconds(l.Start), End = TimeSpan.FromSeconds(l.End), Text = "Line " + i })],
    };

    /// <summary>Audio in which the speech happens at scale × subtitle time + offset.</summary>
    private sealed class FakeAudio(IReadOnlyList<(double Start, double End)> lines, double scale, double offset, int seed = 1) : IAudioSource
    {
        public int Reads { get; private set; }

        public Task<float[]> ReadAsync(TimeSpan start, TimeSpan length, CancellationToken cancellationToken)
        {
            Reads++;
            var random = new Random(seed + (int)start.TotalSeconds);
            var n = (int)(length.TotalSeconds * Rate);
            var samples = new float[n];
            var spoken = lines.Select(l => (From: (scale * l.Start) + offset, To: (scale * l.End) + offset)).OrderBy(l => l.From).ToList();
            var next = 0;
            for (var i = 0; i < n; i++)
            {
                var t = start.TotalSeconds + (i / (double)Rate);
                var noise = (float)((random.NextDouble() * 2) - 1);
                while (next < spoken.Count && spoken[next].To <= t)
                {
                    next++;
                }

                var speaking = next < spoken.Count && t >= spoken[next].From;
                samples[i] = speaking
                    ? (float)((0.15 * Math.Sin(2 * Math.PI * 440 * t)) + (0.1 * Math.Sin(2 * Math.PI * 1300 * t)) + (0.05 * noise))
                    : 0.004f * noise;
            }

            return Task.FromResult(samples);
        }
    }

    [Fact]
    public async Task Speech_activity_is_high_during_speech_and_low_between()
    {
        var lines = new List<(double, double)> { (1, 3), (5, 6.5), (8, 9) };
        var samples = await new FakeAudio(lines, 1, 0).ReadAsync(TimeSpan.Zero, TimeSpan.FromSeconds(10), CancellationToken.None);

        var activity = VoiceActivity.Detect(samples);

        Assert.InRange(activity.Length, 995, 1000);
        Assert.True(activity[150..280].Average() > 0.8);
        Assert.True(activity[350..480].Average() < 0.2);
    }

    [Fact]
    public void Silence_has_no_speech()
        => Assert.All(VoiceActivity.Detect(new float[Rate * 5]), a => Assert.Equal(0, a));

    [Theory]
    [InlineData("Where did you park?", true)]
    [InlineData("<i>[door slams]</i>", false)]
    [InlineData("♪ La la la ♪", true)]
    [InlineData("♪ ♪", false)]
    [InlineData("(laughs) No way.", true)]
    public void Only_dialogue_counts_as_speech(string text, bool speech) => Assert.Equal(speech, SpeechTimeline.IsSpeech(text));

    [Fact]
    public void The_timeline_samples_coverage()
    {
        var timeline = SpeechTimeline.From(Subtitles([(1, 2), (3, 3.5)]));

        var values = timeline.Sample(0, 0.5, 8);

        Assert.Equal(new float[] { 0, 0, 1, 1, 0, 0, 1, 0 }, values);
    }

    [Fact]
    public void Line_starts_are_peaks()
    {
        var timeline = SpeechTimeline.From(Subtitles([(1, 2), (3, 3.5)]));

        var values = timeline.SampleStarts(0, 0.01, 400);

        Assert.Equal(1, values[100], 3);
        Assert.Equal(1, values[300], 3);
        Assert.Equal(0, values[200]);
        Assert.True(values[95] is > 0 and < 1);
    }

    [Fact]
    public async Task Speech_starts_are_found_in_audio()
    {
        var lines = new List<(double, double)> { (1, 3), (5, 6.5), (8, 9) };
        var samples = await new FakeAudio(lines, 1, 0).ReadAsync(TimeSpan.Zero, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var onsets = VoiceActivity.Onsets(samples);

        Assert.True(onsets[98..104].Max() > 0.5);
        Assert.True(onsets[498..504].Max() > 0.5);
        Assert.True(onsets[150..280].Max() < 0.1);
    }

    [Fact]
    public void Fft_correlation_matches_the_direct_calculation()
    {
        var random = new Random(3);
        var audio = Enumerable.Range(0, 300).Select(_ => (float)random.NextDouble()).ToArray();
        var subs = Enumerable.Range(0, 400).Select(_ => (float)random.NextDouble()).ToArray();

        var curve = Correlation.Curve(audio, subs, 101);

        for (var k = 0; k < 101; k += 17)
        {
            var b = subs.AsSpan(k, audio.Length).ToArray();
            double ma = audio.Average(), mb = b.Average();
            var direct = audio.Zip(b, (x, y) => (x - ma) * (y - mb)).Sum() / Math.Sqrt(audio.Sum(x => (x - ma) * (x - ma)) * b.Sum(y => (y - mb) * (y - mb)));
            Assert.Equal(direct, curve[k], 6);
        }
    }

    [Fact]
    public void Stretches_avoid_titles_and_credits()
    {
        var plan = Synchroniser.Plan(TimeSpan.FromMinutes(100));

        Assert.Equal(Synchroniser.Stretches, plan.Count);
        Assert.True(plan[0].Start >= TimeSpan.FromMinutes(8));
        Assert.True(plan[^1].Start + plan[^1].Length <= TimeSpan.FromMinutes(92.01));
        Assert.Empty(Synchroniser.Plan(TimeSpan.FromSeconds(10)));
        Assert.Single(Synchroniser.Plan(TimeSpan.FromSeconds(50)));
    }

    [Theory]
    [InlineData(1.0, 3.2)]
    [InlineData(1.0, -41.75)]
    [InlineData(25 / (24000 / 1001.0), -2.5)]
    [InlineData((24000 / 1001.0) / 25, 1.0)]
    public async Task A_shift_and_a_frame_rate_change_are_recovered(double scale, double offset)
    {
        var duration = 45 * 60.0;
        var lines = Dialogue(duration, 7);

        var model = await new Synchroniser(new FakeAudio(lines, scale, offset), detectorLag: 0).SolveAsync(Subtitles(lines), TimeSpan.FromSeconds(duration), CancellationToken.None);

        Assert.Equal(SyncStatus.Corrected, model.Status);
        Assert.Equal(scale, model.Scale, 6);
        Assert.Equal(offset, model.Offset, 0.05);
        Assert.True(model.Confidence > 0.6, model.Explanation);

        // A cue near the end lands where the speech is
        var last = lines[^10];
        Assert.Equal((scale * last.Start) + offset, model.Map(TimeSpan.FromSeconds(last.Start)).TotalSeconds, 0.06);
    }

    [Fact]
    public async Task Subtitles_already_in_sync_are_left_alone()
    {
        var lines = Dialogue(30 * 60, 3);

        var model = await new Synchroniser(new FakeAudio(lines, 1, 0.02), detectorLag: 0).SolveAsync(Subtitles(lines), TimeSpan.FromMinutes(30), CancellationToken.None);

        Assert.Equal(SyncStatus.InSync, model.Status);
        Assert.Equal(TimeSpan.FromSeconds(100), model.Map(TimeSpan.FromSeconds(100)));
    }

    [Fact]
    public async Task Subtitles_for_something_else_are_unreliable_not_forced()
    {
        var duration = 30 * 60.0;

        var model = await new Synchroniser(new FakeAudio(Dialogue(duration, 11), 1, 0)).SolveAsync(Subtitles(Dialogue(duration, 99)), TimeSpan.FromSeconds(duration), CancellationToken.None);

        Assert.Equal(SyncStatus.Unreliable, model.Status);
        Assert.Equal(TimeSpan.FromSeconds(100), model.Map(TimeSpan.FromSeconds(100)));
    }

    [Fact]
    public void Ffmpeg_gets_a_safe_argument_list()
    {
        var args = FfmpegAudioSource.Arguments("/media/-weird name.mkv", 1, TimeSpan.FromSeconds(90.5), TimeSpan.FromSeconds(120));

        Assert.Contains("file:/media/-weird name.mkv", args);
        Assert.Equal("90.500", args[Array.IndexOf(args, "-ss") + 1]);
        Assert.Equal("0:a:1", args[Array.IndexOf(args, "-map") + 1]);
        Assert.Equal("pipe:1", args[^1]);
        Assert.Throws<ArgumentException>(() => new FfmpegAudioSource("ffmpeg", "/media/a.mkv"));
        Assert.Throws<ArgumentException>(() => new FfmpegAudioSource("/usr/bin/ffmpeg", "a.mkv"));
    }

    [Fact]
    public void Pcm_becomes_samples()
        => Assert.Equal(new[] { 0f, 0.5f, -1f }, FfmpegAudioSource.ToSamples([0, 0, 0, 0x40, 0, 0x80]));

    [Fact]
    public async Task Real_ffmpeg_reads_a_stretch_of_audio()
    {
        var ffmpeg = new[] { "/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/usr/lib/jellyfin-ffmpeg/ffmpeg" }.FirstOrDefault(File.Exists);
        Assert.SkipWhen(ffmpeg is null, "ffmpeg isn't installed");

        var dir = Path.Combine(Path.GetTempPath(), "subs-ffmpeg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "tone.mka");
            using (var make = Process.Start(new ProcessStartInfo(ffmpeg!) { ArgumentList = { "-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=1000:duration=10", "-c:a", "flac", file } })!)
            {
                await make.WaitForExitAsync(TestContext.Current.CancellationToken);
                Assert.Equal(0, make.ExitCode);
            }

            var samples = await new FfmpegAudioSource(ffmpeg!, file).ReadAsync(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), CancellationToken.None);

            Assert.InRange(samples.Length, (Rate * 3) - 100, Rate * 3);
            Assert.True(samples.Max() > 0.05);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
