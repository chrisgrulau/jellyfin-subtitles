using System;
using System.IO;

namespace Jellyfin.Plugin.Subtitles.Audio;

/// <summary>
/// Finds the ffmpeg program Jellyfin uses. Jellyfin stores a full path when one is configured or packaged, and the bare
/// name <c>ffmpeg</c> when it relies on the system PATH (portable installs, source builds, hand-configured servers).
/// </summary>
public static class FfmpegLocator
{
    /// <summary>
    /// The full path of Jellyfin's ffmpeg.
    /// </summary>
    /// <param name="encoderPath">Jellyfin's <c>EncoderPath</c>: a full path, or a bare program name.</param>
    /// <param name="pathVariable">The PATH to search (defaults to the process's).</param>
    /// <returns>The full path, or <c>null</c> if it can't be found.</returns>
    public static string? Resolve(string? encoderPath, string? pathVariable = null)
    {
        if (string.IsNullOrWhiteSpace(encoderPath))
        {
            return null;
        }

        if (Path.IsPathFullyQualified(encoderPath))
        {
            return File.Exists(encoderPath) ? encoderPath : null;
        }

        // Only a bare name is looked up on PATH (a relative path with folders is ambiguous, so it isn't)
        if (encoderPath.IndexOfAny(['/', '\\']) >= 0)
        {
            return null;
        }

        var names = OperatingSystem.IsWindows() && !Path.HasExtension(encoderPath) ? new[] { encoderPath + ".exe", encoderPath } : [encoderPath];
        foreach (var folder in (pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Path.IsPathFullyQualified(folder))
            {
                continue;
            }

            foreach (var name in names)
            {
                var candidate = Path.Combine(folder, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
