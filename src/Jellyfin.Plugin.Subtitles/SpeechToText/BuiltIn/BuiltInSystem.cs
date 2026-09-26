using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.Subtitles.SpeechToText.BuiltIn;

/// <summary>
/// Whether this server's system can start the published built-in program. The Linux builds are made on Ubuntu 22.04,
/// so they need glibc 2.35 or newer and don't start on musl (Alpine-based images, Jellyfin's musl builds). This is
/// checked before anything is downloaded, so an unsuitable server never fetches a program it can't run.
/// </summary>
internal static partial class BuiltInSystem
{
    /// <summary>The oldest glibc the Linux builds run on.</summary>
    internal static readonly Version MinimumGlibc = new(2, 35);

    private static readonly Lazy<string?> CurrentProblem = new(() => Problem(
        OperatingSystem.IsLinux(),
        RuntimeInformation.RuntimeIdentifier,
        OperatingSystem.IsLinux() ? GlibcVersion() : null));

    /// <summary>
    /// Why the built-in program can't run on this server, or <c>null</c> if it can.
    /// </summary>
    /// <returns>The reason, in plain language.</returns>
    internal static string? ProblemHere() => CurrentProblem.Value;

    /// <summary>
    /// Why the built-in program can't run on a system, or <c>null</c> if it can.
    /// </summary>
    /// <param name="isLinux">Whether the system is Linux (only the Linux builds depend on the C library).</param>
    /// <param name="runtimeIdentifier">.NET's runtime identifier, e.g. <c>linux-musl-x64</c>.</param>
    /// <param name="glibcVersion">glibc's version as it reports it, or <c>null</c> if it couldn't be read.</param>
    /// <returns>The reason, in plain language.</returns>
    internal static string? Problem(bool isLinux, string? runtimeIdentifier, string? glibcVersion)
    {
        if (!isLinux)
        {
            return null;
        }

        const string Instead = " Use a local speech-to-text service or a cloud service instead.";
        if (runtimeIdentifier?.Contains("musl", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "The built-in speech-to-text can't run on this server: its system uses the musl C library (for example an Alpine-based image), and the built-in program needs glibc " + MinimumGlibc + " or newer. Nothing was downloaded." + Instead;
        }

        if (ParseVersion(glibcVersion) is not { } version)
        {
            return "The built-in speech-to-text can't run on this server: its C library version couldn't be read, and the built-in program needs glibc " + MinimumGlibc + " or newer. Nothing was downloaded." + Instead;
        }

        return version < MinimumGlibc
            ? "The built-in speech-to-text can't run on this server: its system has glibc " + version + ", and the built-in program needs " + MinimumGlibc + " or newer (for example Debian 12 or Ubuntu 22.04). Nothing was downloaded." + Instead
            : null;
    }

    /// <summary>
    /// Reads a glibc version string such as <c>2.35</c> or <c>2.31.1</c>.
    /// </summary>
    /// <param name="text">The version text.</param>
    /// <returns>The major and minor version, or <c>null</c> if it isn't one.</returns>
    internal static Version? ParseVersion(string? text)
    {
        var parts = (text ?? string.Empty).Trim().Split('.');
        return parts.Length >= 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
                ? new Version(major, minor)
                : null;
    }

    // Any failure (no glibc, a C library without this function) counts as "unknown", which is treated as unsuitable
    private static string? GlibcVersion()
    {
        try
        {
            var text = GnuGetLibcVersion();
            return text == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(text);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;
        }
    }

    // Returns a pointer to a static string that must not be freed, so it's read by hand rather than marshalled
    [LibraryImport("libc.so.6", EntryPoint = "gnu_get_libc_version")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial IntPtr GnuGetLibcVersion();
}
