using System;
using System.Linq;

namespace Jellyfin.Plugin.Subtitles.Audio;

/// <summary>
/// Finds where people are speaking, frame by frame, without speech-to-text: the speech band (about 250–3500 Hz) is
/// isolated, its loudness measured every 10 ms, and compared with the recording's own quiet and loud levels. It is a
/// cheap, rough signal: music and effects also register, which is fine for lining subtitles up, since the correlation
/// with subtitle timing is what counts.
/// </summary>
public static class VoiceActivity
{
    /// <summary>Frames per second of the activity signal (one every 10 ms).</summary>
    public const int FramesPerSecond = 100;

    private const int Hop = AudioFormat.SampleRate / FramesPerSecond;
    private const int Window = Hop * 2;

    /// <summary>
    /// Measures where speech starts: how sharply the speech band gets louder (the rise over the quietest point of the
    /// previous 50 ms, above 3 dB). Line starts are the most telling part of subtitle timing, and unlike "is anyone
    /// talking" they still vary in dense dialogue, where the subtitles cover almost all the time.
    /// </summary>
    /// <param name="samples">16 kHz mono samples.</param>
    /// <returns>One value per 10 ms frame, from 0 (no new sound) to 1 (a sharp start).</returns>
    public static float[] Onsets(ReadOnlySpan<float> samples)
    {
        var db = BandLevels(samples);
        var onsets = new float[db.Length];
        for (var f = 5; f < db.Length; f++)
        {
            var quietest = float.MaxValue;
            for (var j = f - 5; j < f; j++)
            {
                quietest = Math.Min(quietest, db[j]);
            }

            onsets[f] = Math.Clamp(db[f] - quietest - 3f, 0f, 15f) / 15f;
        }

        return Smooth(onsets, 5);
    }

    /// <summary>
    /// Measures speech activity.
    /// </summary>
    /// <param name="samples">16 kHz mono samples.</param>
    /// <returns>One value per 10 ms frame, from 0 (silence or background) to 1 (speech-like sound).</returns>
    public static float[] Detect(ReadOnlySpan<float> samples)
    {
        var db = BandLevels(samples);
        var frames = db.Length;
        if (frames == 0)
        {
            return [];
        }

        // The recording's own quiet and loud levels decide what counts as speech
        var sorted = db.ToArray();
        Array.Sort(sorted);
        var quiet = sorted[(int)(0.10 * (sorted.Length - 1))];
        var loud = sorted[(int)(0.95 * (sorted.Length - 1))];
        if (loud - quiet < 6)
        {
            // No contrast: silence, or one steady sound throughout
            return new float[frames];
        }

        var threshold = quiet + Math.Max(6, 0.4f * (loud - quiet));
        var activity = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            activity[f] = 1f / (1f + MathF.Exp(-(db[f] - threshold) / 2f));
        }

        return Smooth(activity, 5);
    }

    // Speech-band loudness in dB, one value per 10 ms frame (20 ms windows)
    private static float[] BandLevels(ReadOnlySpan<float> samples)
    {
        var frames = samples.Length < Window ? 0 : 1 + ((samples.Length - Window) / Hop);
        var band = BandPass(samples);
        var db = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            double sum = 0;
            for (var i = f * Hop; i < (f * Hop) + Window; i++)
            {
                sum += band[i] * band[i];
            }

            db[f] = (float)(10 * Math.Log10((sum / Window) + 1e-10));
        }

        return db;
    }

    private static float[] BandPass(ReadOnlySpan<float> x)
    {
        var y = new float[x.Length];
        var high = Biquad.HighPass(250, AudioFormat.SampleRate);
        var low = Biquad.LowPass(3500, AudioFormat.SampleRate);
        for (var i = 0; i < x.Length; i++)
        {
            y[i] = low.Next(high.Next(x[i]));
        }

        return y;
    }

    private static float[] Smooth(float[] x, int width)
    {
        var y = new float[x.Length];
        var half = width / 2;
        for (var i = 0; i < x.Length; i++)
        {
            float sum = 0;
            var n = 0;
            for (var j = Math.Max(0, i - half); j <= Math.Min(x.Length - 1, i + half); j++)
            {
                sum += x[j];
                n++;
            }

            y[i] = sum / n;
        }

        return y;
    }

    /// <summary>A second-order Butterworth filter (RBJ audio-EQ cookbook).</summary>
    private sealed class Biquad
    {
        private readonly float _b0, _b1, _b2, _a1, _a2;
        private float _x1, _x2, _y1, _y2;

        private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            (_b0, _b1, _b2, _a1, _a2) = ((float)(b0 / a0), (float)(b1 / a0), (float)(b2 / a0), (float)(a1 / a0), (float)(a2 / a0));
        }

        public static Biquad HighPass(double cutoff, int rate)
        {
            var (cos, alpha) = Coefficients(cutoff, rate);
            return new Biquad((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
        }

        public static Biquad LowPass(double cutoff, int rate)
        {
            var (cos, alpha) = Coefficients(cutoff, rate);
            return new Biquad((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
        }

        public float Next(float x)
        {
            var y = (_b0 * x) + (_b1 * _x1) + (_b2 * _x2) - (_a1 * _y1) - (_a2 * _y2);
            (_x2, _x1, _y2, _y1) = (_x1, x, _y1, y);
            return y;
        }

        private static (double Cos, double Alpha) Coefficients(double cutoff, int rate)
        {
            var w = 2 * Math.PI * cutoff / rate;
            return (Math.Cos(w), Math.Sin(w) / (2 * Math.Sqrt(0.5)));
        }
    }
}
