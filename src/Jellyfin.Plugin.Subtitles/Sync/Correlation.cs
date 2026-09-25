using System;
using System.Numerics;

namespace Jellyfin.Plugin.Subtitles.Sync;

/// <summary>
/// Normalised cross-correlation of an audio feature with a (longer) subtitle signal at every alignment, computed with a
/// fast Fourier transform so that trying thousands of offsets costs about as much as trying one.
/// </summary>
public static class Correlation
{
    /// <summary>
    /// Correlates <paramref name="audio"/> with every stretch of <paramref name="subtitles"/> of the same length.
    /// </summary>
    /// <param name="audio">The audio feature (n frames).</param>
    /// <param name="subtitles">The subtitle signal (n + lags − 1 frames or more).</param>
    /// <param name="lags">How many alignments to score: alignment k compares audio frame f with subtitle frame f + k.</param>
    /// <returns>Pearson correlation for each alignment (0 where either side is constant).</returns>
    public static double[] Curve(ReadOnlySpan<float> audio, ReadOnlySpan<float> subtitles, int lags)
    {
        var n = audio.Length;
        var result = new double[Math.Max(0, lags)];
        if (n == 0 || lags <= 0 || subtitles.Length < n + lags - 1)
        {
            return result;
        }

        double mean = 0;
        foreach (var a in audio)
        {
            mean += a;
        }

        mean /= n;
        double va = 0;
        foreach (var a in audio)
        {
            va += (a - mean) * (a - mean);
        }

        if (va <= 1e-12)
        {
            return result;
        }

        // Cross-correlation Σ a'[f]·s[f + k] via FFT: IFFT(conj(FFT(a')) × FFT(s))
        var size = 1;
        while (size < subtitles.Length + n)
        {
            size <<= 1;
        }

        var fa = new Complex[size];
        var fs = new Complex[size];
        for (var i = 0; i < n; i++)
        {
            fa[i] = audio[i] - mean;
        }

        for (var i = 0; i < subtitles.Length; i++)
        {
            fs[i] = subtitles[i];
        }

        Transform(fa, inverse: false);
        Transform(fs, inverse: false);
        for (var i = 0; i < size; i++)
        {
            fs[i] = Complex.Conjugate(fa[i]) * fs[i];
        }

        Transform(fs, inverse: true);

        // The subtitle side's variance over each stretch, from running sums
        var sum = new double[subtitles.Length + 1];
        var squares = new double[subtitles.Length + 1];
        for (var i = 0; i < subtitles.Length; i++)
        {
            sum[i + 1] = sum[i] + subtitles[i];
            squares[i + 1] = squares[i] + (subtitles[i] * subtitles[i]);
        }

        for (var k = 0; k < lags; k++)
        {
            var s = sum[k + n] - sum[k];
            var vs = squares[k + n] - squares[k] - (s * s / n);
            result[k] = vs <= 1e-12 ? 0 : fs[k].Real / size / Math.Sqrt(va * vs);
        }

        return result;
    }

    // In-place iterative radix-2 FFT (unscaled; the caller divides by the size after the inverse)
    private static void Transform(Complex[] x, bool inverse)
    {
        var n = x.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (x[i], x[j]) = (x[j], x[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = 2 * Math.PI / len * (inverse ? 1 : -1);
            var step = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (var j = 0; j < len / 2; j++)
                {
                    var u = x[i + j];
                    var v = x[i + j + (len / 2)] * w;
                    x[i + j] = u + v;
                    x[i + j + (len / 2)] = u - v;
                    w *= step;
                }
            }
        }
    }
}
