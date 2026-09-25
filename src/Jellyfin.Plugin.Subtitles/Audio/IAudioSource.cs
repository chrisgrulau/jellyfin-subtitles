using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Subtitles.Audio;

/// <summary>
/// Reads a stretch of a video's audio as 16 kHz mono samples (the rate speech detection and speech-to-text use).
/// </summary>
public interface IAudioSource
{
    /// <summary>
    /// Reads audio.
    /// </summary>
    /// <param name="start">Where to start, from the beginning of the video.</param>
    /// <param name="length">How much to read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Samples in [-1, 1] at <see cref="AudioFormat.SampleRate"/>; shorter than asked near the end of the video.</returns>
    Task<float[]> ReadAsync(TimeSpan start, TimeSpan length, CancellationToken cancellationToken);
}

/// <summary>
/// The audio format used throughout.
/// </summary>
public static class AudioFormat
{
    /// <summary>Samples per second (16 kHz mono, as Whisper expects).</summary>
    public const int SampleRate = 16000;
}
