using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Subtitles.Audio;

/// <summary>
/// Reads audio with ffmpeg (Jellyfin's own build, found through the server's encoder setting). The process is started
/// without a shell, with its arguments as a list; the video is passed as an absolute <c>file:</c> path, so no file name
/// can be read as an option or a protocol. It runs single-threaded at low priority with a time limit, and the whole
/// process tree is killed on cancellation or timeout.
/// </summary>
public sealed class FfmpegAudioSource : IAudioSource
{
    /// <summary>The longest a single read may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private const int MaxErrorChars = 4000;

    private readonly string _ffmpeg;
    private readonly string _video;
    private readonly int _audioStream;

    /// <summary>
    /// Initializes a new instance of the <see cref="FfmpegAudioSource"/> class.
    /// </summary>
    /// <param name="ffmpegPath">Absolute path of the ffmpeg program.</param>
    /// <param name="videoPath">Absolute path of the video.</param>
    /// <param name="audioStream">Which audio stream, counting audio streams only from 0.</param>
    public FfmpegAudioSource(string ffmpegPath, string videoPath, int audioStream = 0)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !Path.IsPathFullyQualified(ffmpegPath))
        {
            throw new ArgumentException("The ffmpeg path must be absolute.", nameof(ffmpegPath));
        }

        if (string.IsNullOrWhiteSpace(videoPath) || !Path.IsPathFullyQualified(videoPath))
        {
            throw new ArgumentException("The video path must be absolute.", nameof(videoPath));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(audioStream);
        _ffmpeg = ffmpegPath;
        _video = videoPath;
        _audioStream = audioStream;
    }

    /// <summary>
    /// The arguments for reading a stretch of audio (exposed for tests).
    /// </summary>
    /// <param name="videoPath">Absolute video path.</param>
    /// <param name="audioStream">Audio stream number.</param>
    /// <param name="start">Start.</param>
    /// <param name="length">Length.</param>
    /// <returns>The argument list.</returns>
    public static string[] Arguments(string videoPath, int audioStream, TimeSpan start, TimeSpan length)
        =>
        [
            "-nostdin", "-hide_banner", "-loglevel", "error", "-threads", "1",
            "-ss", Seconds(start), "-t", Seconds(length),
            "-i", "file:" + videoPath,
            "-map", string.Create(CultureInfo.InvariantCulture, $"0:a:{audioStream}"),
            "-vn", "-sn", "-dn", "-ac", "1", "-ar", AudioFormat.SampleRate.ToString(CultureInfo.InvariantCulture),
            "-f", "s16le", "pipe:1",
        ];

    /// <inheritdoc />
    public async Task<float[]> ReadAsync(TimeSpan start, TimeSpan length, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start.Ticks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length.Ticks);

        var info = new ProcessStartInfo(_ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
        };
        foreach (var a in Arguments(_video, _audioStream, start, length))
        {
            info.ArgumentList.Add(a);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        using var process = Process.Start(info) ?? throw new IOException("ffmpeg could not be started.");
        try
        {
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
            {
                // Lowering priority is a courtesy; carry on without it
            }

            // Both pipes are drained at once so neither can fill up and stall the process
            var maxBytes = (long)(length.TotalSeconds * AudioFormat.SampleRate * 2) + 65536;
            var errors = ReadLimitedAsync(process.StandardError, timeout.Token);
            var bytes = await ReadAllAsync(process.StandardOutput.BaseStream, maxBytes, timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new IOException("ffmpeg failed: " + (await errors.ConfigureAwait(false)).Trim());
            }

            return ToSamples(bytes);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Reading audio took longer than " + Timeout.TotalMinutes.ToString(CultureInfo.InvariantCulture) + " minutes.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    /// <summary>
    /// Converts 16-bit little-endian PCM to samples in [-1, 1].
    /// </summary>
    /// <param name="pcm">The bytes (an odd trailing byte is ignored).</param>
    /// <returns>The samples.</returns>
    public static float[] ToSamples(ReadOnlySpan<byte> pcm)
    {
        var samples = new float[pcm.Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(i * 2, 2)) / 32768f;
        }

        return samples;
    }

    private static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture);

    private static async Task<byte[]> ReadAllAsync(Stream stream, long maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + n > maxBytes)
            {
                throw new IOException("ffmpeg returned more audio than asked for.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, n), ct).ConfigureAwait(false);
        }

        return buffer.ToArray();
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader, CancellationToken ct)
    {
        var text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        return text.Length > MaxErrorChars ? text[..MaxErrorChars] : text;
    }
}
