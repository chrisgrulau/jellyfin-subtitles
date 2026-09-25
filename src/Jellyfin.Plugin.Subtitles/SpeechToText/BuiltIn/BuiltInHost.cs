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
    public BuiltInHost(string dataFolder, BuiltInInstaller? installer = null, string? platform = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataFolder);
        var folder = Path.Combine(dataFolder, "builtin");
        _work = Path.Combine(folder, "work");
        Installer = installer ?? new BuiltInInstaller(folder);
        Platform = platform ?? BuiltInSource.CurrentPlatform();
        ClearWork();
    }

    /// <summary>Gets the installer.</summary>
    public BuiltInInstaller Installer { get; }

    /// <summary>Gets this server's platform, or <c>null</c> if there is no build for it.</summary>
    public string? Platform { get; }

    /// <summary>
    /// Creates the service for a model.
    /// </summary>
    /// <param name="model">Model setting value (<c>base</c> or <c>small</c>).</param>
    /// <returns>The service.</returns>
    public ISpeechToText Create(string model)
        => new BuiltInSpeechToText(Installer, _work, Platform ?? throw new InvalidOperationException("No build for this server."), model);

    /// <inheritdoc />
    public void Dispose() => Installer.Dispose();

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
