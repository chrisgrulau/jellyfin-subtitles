using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.Formats;

namespace Jellyfin.Plugin.Subtitles.Audio;

/// <summary>
/// Copies a text subtitle track out of a video with Jellyfin's ffmpeg, without changing the video. ffmpeg has to read
/// the whole file to do it, so this is only used when the administrator opts in, a few videos per run. Same rules as
/// the audio reader: an argument list (no shell), a <c>file:</c> prefix, low priority, a time limit, capped output, and
/// the process tree killed on cancel.
/// </summary>
public static class FfmpegSubtitleExtractor
{
    /// <summary>The longest an extraction may take (a large file over a network share).</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(20);

    /// <summary>The track formats that can be copied out as text, by ffmpeg codec name.</summary>
    public static readonly IReadOnlyCollection<string> TextCodecs = ["subrip", "srt", "ass", "ssa", "webvtt", "mov_text", "text"];

    /// <summary>
    /// The output format for a track: ASS stays ASS (to keep its styling); everything else becomes SRT.
    /// </summary>
    /// <param name="codec">The track's codec.</param>
    /// <returns><c>ass</c> or <c>srt</c>.</returns>
    public static string OutputFormat(string? codec) => string.Equals(codec, "ass", StringComparison.OrdinalIgnoreCase) || string.Equals(codec, "ssa", StringComparison.OrdinalIgnoreCase) ? "ass" : "srt";

    /// <summary>
    /// The ffmpeg arguments.
    /// </summary>
    /// <param name="videoPath">Absolute video path.</param>
    /// <param name="streamIndex">The track's stream index in the file.</param>
    /// <param name="codec">The track's codec.</param>
    /// <returns>The argument list.</returns>
    public static string[] Arguments(string videoPath, int streamIndex, string? codec)
    {
        var format = OutputFormat(codec);
        return
        [
            "-nostdin", "-hide_banner", "-loglevel", "error", "-threads", "1",
            "-i", "file:" + videoPath,
            "-map", string.Create(CultureInfo.InvariantCulture, $"0:{streamIndex}"),
            "-vn", "-an", "-dn", "-c:s", format, "-f", format, "pipe:1",
        ];
    }

    /// <summary>
    /// Copies the track out.
    /// </summary>
    /// <param name="ffmpegPath">Jellyfin's ffmpeg.</param>
    /// <param name="videoPath">Absolute video path.</param>
    /// <param name="streamIndex">The track's stream index.</param>
    /// <param name="codec">The track's codec.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The subtitle file's bytes, or <c>null</c> if larger than <see cref="SubtitleReader.MaxBytes"/>.</returns>
    /// <exception cref="IOException">ffmpeg failed.</exception>
    /// <exception cref="TimeoutException">It took too long.</exception>
    public static async Task<byte[]?> ExtractAsync(string ffmpegPath, string videoPath, int streamIndex, string? codec, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        ArgumentOutOfRangeException.ThrowIfNegative(streamIndex);
        var info = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in Arguments(Path.GetFullPath(videoPath), streamIndex, codec))
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
                // Lowering priority is a courtesy
            }

            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            var bytes = await SubtitleReader.ReadLimitedAsync(process.StandardOutput.BaseStream, timeout.Token).ConfigureAwait(false);
            if (bytes is null)
            {
                return null;
            }

            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var text = (await errors.ConfigureAwait(false)).Trim();
                throw new IOException("ffmpeg failed: " + (text.Length > 300 ? text[..300] : text));
            }

            return bytes;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Copying the subtitle track took longer than " + Timeout.TotalMinutes.ToString(CultureInfo.InvariantCulture) + " minutes.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }
}
