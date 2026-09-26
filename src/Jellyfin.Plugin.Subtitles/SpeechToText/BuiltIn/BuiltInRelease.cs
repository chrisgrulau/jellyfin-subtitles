using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.Subtitles.SpeechToText.BuiltIn;

/// <summary>
/// A file the built-in speech-to-text may download: its SHA-256 and size are compiled in, never fetched.
/// </summary>
/// <param name="Name">File name in the release.</param>
/// <param name="Sha256">SHA-256, lower-case hex.</param>
/// <param name="Bytes">Size in bytes.</param>
/// <param name="Files">For a zip, every file in it and its SHA-256; <c>null</c> for a single file.</param>
public sealed record BuiltInDownload(string Name, string Sha256, long Bytes, IReadOnlyDictionary<string, string>? Files);

/// <summary>
/// Where the built-in speech-to-text comes from: one release of this project, with every file's checksum.
/// </summary>
/// <param name="BaseUrl">The release's download address (ends with <c>/</c>).</param>
/// <param name="Version">The release tag (also the install folder's name).</param>
/// <param name="Downloads">The files.</param>
/// <param name="AllowedHosts">The hosts a download may be redirected to.</param>
public sealed record BuiltInSource(Uri BaseUrl, string Version, IReadOnlyList<BuiltInDownload> Downloads, IReadOnlyCollection<string> AllowedHosts)
{
    /// <summary>Gets the models that can be chosen, by setting value.</summary>
    public static IReadOnlyDictionary<string, string> ModelFiles { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["base"] = "ggml-base-q8_0.bin",
        ["small"] = "ggml-small-q5_1.bin",
    };

    /// <summary>Gets the release compiled into this plugin.</summary>
    public static BuiltInSource Published { get; } = new(
        new Uri("https://github.com/chrisgrulau/jellyfin-subtitles/releases/download/" + BuiltInRelease.Tag + "/"),
        BuiltInRelease.Tag,
        BuiltInRelease.All,
        // GitHub serves release files from its own download hosts
        ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"]);

    /// <summary>
    /// The model a setting names: empty or <c>base</c> for the default, <c>small</c> for the larger, more accurate one.
    /// </summary>
    /// <param name="setting">The model setting.</param>
    /// <returns>The setting value it resolves to, or <c>null</c> if unknown.</returns>
    public static string? ModelName(string? setting)
    {
        var name = string.IsNullOrWhiteSpace(setting) ? "base" : setting.Trim();
        return ModelFiles.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The program for a platform.
    /// </summary>
    /// <param name="platform">Platform, e.g. <c>linux-x64</c>.</param>
    /// <returns>The download, or <c>null</c> if there is no build for it.</returns>
    public BuiltInDownload? Program(string platform) => Downloads.FirstOrDefault(d => d.Files is not null && d.Name == "whisper-cli-" + platform + ".zip");

    /// <summary>
    /// A model file.
    /// </summary>
    /// <param name="model">Model setting value (<c>base</c> or <c>small</c>).</param>
    /// <returns>The download, or <c>null</c> if unknown.</returns>
    public BuiltInDownload? Model(string model)
        => ModelFiles.TryGetValue(model, out var file) ? Downloads.FirstOrDefault(d => d.Files is null && d.Name == file) : null;

    /// <summary>
    /// The platform name of this server (<c>linux-x64</c>, <c>linux-arm64</c>, <c>windows-x64</c>, <c>macos-arm64</c>,
    /// <c>macos-x64</c>), or <c>null</c> where there is no build.
    /// </summary>
    /// <returns>The platform.</returns>
    public static string? CurrentPlatform()
    {
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };
        var os = OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : null;
        return arch is null || os is null || (os == "windows" && arch != "x64") ? null : os + "-" + arch;
    }
}

/// <summary>
/// The published release (checksums in <c>BuiltInRelease.Checksums.cs</c>, written by <c>tools/builtin_checksums.py</c>).
/// </summary>
internal static partial class BuiltInRelease
{
    /// <summary>Gets every file in the release.</summary>
    internal static IReadOnlyList<BuiltInDownload> All => Downloads;
}
