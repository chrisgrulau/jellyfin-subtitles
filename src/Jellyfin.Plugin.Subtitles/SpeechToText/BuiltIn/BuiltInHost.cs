using System;
using System.IO;

namespace Jellyfin.Plugin.Subtitles.SpeechToText.BuiltIn;

/// <summary>
/// The server's built-in speech-to-text: where it is installed (the plugin's data folder), which build this server
/// needs, and one shared installer so downloads never run twice at once.
/// </summary>
public sealed class BuiltInHost : IDisposable
{
    private readonly string _work;

    /// <summary>
    /// Initializes a new instance of the <see cref="BuiltInHost"/> class.
    /// </summary>
    /// <param name="dataFolder">The plugin's data folder.</param>
    /// <param name="installer">Installer (for tests); by default one for the published release.</param>
    /// <param name="platform">Platform (for tests); by default this server's.</param>
    /// <param name="legacyFolder">Where earlier versions installed it (inside the plugin folder), moved on first start.</param>
    public BuiltInHost(string dataFolder, BuiltInInstaller? installer = null, string? platform = null, string? legacyFolder = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataFolder);
        var folder = Path.Combine(dataFolder, "builtin");
        if (legacyFolder is not null)
        {
            MoveFromLegacy(Path.Combine(legacyFolder, "builtin"), folder);
        }

        _work = Path.Combine(folder, "work");
        Installer = installer ?? new BuiltInInstaller(folder);
        Platform = platform ?? BuiltInSource.CurrentPlatform();

        // A platform given by a test is taken as able to run the program
        Problem = platform is null && Platform is not null ? BuiltInSystem.ProblemHere() : null;
        ClearWork();
    }

    /// <summary>Gets the installer.</summary>
    public BuiltInInstaller Installer { get; }

    /// <summary>Gets this server's platform, or <c>null</c> if there is no build for it.</summary>
    public string? Platform { get; }

    /// <summary>
    /// Gets why this server can't run the built-in program although there is a build for its platform (musl, or a glibc
    /// older than the build needs), or <c>null</c> if it can. When set, nothing is downloaded.
    /// </summary>
    public string? Problem { get; init; }

    /// <summary>
    /// Creates the service for a model.
    /// </summary>
    /// <param name="model">Model setting value (<c>base</c> or <c>small</c>).</param>
    /// <returns>The service.</returns>
    public ISpeechToText Create(string model)
        => new BuiltInSpeechToText(Installer, _work, Platform ?? throw new InvalidOperationException("No build for this server."), model);

    /// <inheritdoc />
    public void Dispose() => Installer.Dispose();

    /// <summary>
    /// Moves an install from where earlier versions put it (under Jellyfin's plugins folder, where on Windows its DLLs
    /// made Jellyfin list a broken plugin whose Uninstall deletes the plugin's data). If it can't be moved (another
    /// disk, or already there), the old copy is deleted: the program is downloaded again, verified, when next needed.
    /// </summary>
    /// <param name="legacy">The old folder.</param>
    /// <param name="folder">The new folder.</param>
    public static void MoveFromLegacy(string legacy, string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacy);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (!Directory.Exists(legacy) || string.Equals(Path.GetFullPath(legacy), Path.GetFullPath(folder), StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
                Directory.Move(legacy, folder);
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another disk (or in use): fall through and remove the old copy instead
        }

        try
        {
            Directory.Delete(legacy, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tried again at the next start
        }
    }

    // Leftovers from runs interrupted by a restart
    private void ClearWork()
    {
        try
        {
            if (Directory.Exists(_work))
            {
                Directory.Delete(_work, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tried again at the next start
        }
    }
}
