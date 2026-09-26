using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Subtitles.SpeechToText.BuiltIn;

/// <summary>
/// The server's built-in speech-to-text: where it is installed (the plugin's data folder), which build this server
/// needs, and one shared installer so downloads never run twice at once.
/// </summary>
public sealed class BuiltInHost : IDisposable
{
    private readonly string _work;
    private readonly SingleFlight _background = new();
    private readonly CancellationTokenSource _stopping = new();

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

    /// <summary>Gets a value indicating whether a background download (see <see cref="StartInstall"/>) is running.</summary>
    public bool Installing => _background.Running;

    /// <summary>Gets the background download started last, if any (for tests).</summary>
    internal Task? Background => _background.Current;

    /// <summary>
    /// Starts downloading the program and a model in the background (SUB-25), so the settings page doesn't wait on
    /// 90–200 MB inside one request; <see cref="BuiltInInstaller.Progress"/> says how far it is. Only one runs at a time:
    /// asking while one runs joins it. It shares the installer's lock, so a nightly run that needs the built-in
    /// speech-to-text meanwhile waits for it and then uses what it installed. It stops when the server shuts down. The
    /// caller checks the administrator's consent first.
    /// </summary>
    /// <param name="model">Model setting value (<c>base</c> or <c>small</c>).</param>
    /// <returns><c>true</c> if a download started now, <c>false</c> if one was already running.</returns>
    /// <exception cref="InvalidOperationException">There is no build for this server, or it can't run here.</exception>
    public bool StartInstall(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var platform = Platform ?? throw new InvalidOperationException("No build for this server.");
        if (Problem is not null)
        {
            throw new InvalidOperationException(Problem);
        }

        return _background.TryStart(
            async token =>
            {
                try
                {
                    await Installer.EnsureAsync(platform, model, token).ConfigureAwait(false);
                    Installer.Progress.Complete();
                }
#pragma warning disable CA1031 // Background work: any failure is shown on the settings page, never thrown into nothing
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    // Shown on the page; the next use, Test or Download now tries again
                    Installer.Progress.Fail(BuiltInProgress.Describe(ex));
                }
            },
            _stopping.Token,
            // Shown as downloading from the moment it's asked for, so the page never reads the previous outcome
            () => Installer.Progress.Begin(0));
    }

    /// <summary>
    /// Whether the program and a model are on disk (not checked; they are checked before every run).
    /// </summary>
    /// <param name="model">Model setting value.</param>
    /// <returns><c>true</c> if both are there.</returns>
    public bool IsInstalled(string model) => Platform is { } platform && Installer.IsInstalled(platform, model);

    /// <summary>
    /// Where the download stands for a model, for the settings page.
    /// </summary>
    /// <param name="model">Model setting value.</param>
    /// <returns>The status.</returns>
    public BuiltInInstallStatus Status(string model) => Installer.Progress.Report(IsInstalled(model));

    /// <inheritdoc />
    public void Dispose()
    {
        // The server is stopping: a background download is cancelled (its partial file is removed) before the installer goes
        _stopping.Cancel();
        try
        {
            _background.Current?.Wait(TimeSpan.FromSeconds(10));
        }
        catch (AggregateException)
        {
            // Its outcome no longer matters
        }

        Installer.Dispose();
        _stopping.Dispose();
    }

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
