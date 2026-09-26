using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Resilience;

namespace Jellyfin.Plugin.Subtitles.SpeechToText.BuiltIn;

/// <summary>
/// Downloads, verifies and keeps the built-in speech-to-text program and models (see "Built-in speech-to-text: safety
/// requirements" in docs/DESIGN.md):
/// <list type="bullet">
/// <item>only the files of one release, whose SHA-256 values are compiled in;</item>
/// <item>only over HTTPS from this project's releases, following redirects only to GitHub's download hosts;</item>
/// <item>downloaded to a temporary name and verified before being moved into place, into folders only the server's
/// user can use; the program's zip must hold exactly the expected files;</item>
/// <item>every file checked again each time it is used; anything that no longer matches is removed and fetched again.</item>
/// </list>
/// The caller decides whether downloading is allowed (the administrator's consent).
/// </summary>
public sealed class BuiltInInstaller : IDisposable
{
    private const int MaxRedirects = 5;

    private readonly string _folder;
    private readonly BuiltInSource _source;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="BuiltInInstaller"/> class.
    /// </summary>
    /// <param name="folder">The plugin-owned folder to install into.</param>
    /// <param name="source">The release to install from (<see cref="BuiltInSource.Published"/>).</param>
    /// <param name="handler">HTTP handler (for tests); it must not follow redirects itself.</param>
    public BuiltInInstaller(string folder, BuiltInSource? source = null, HttpMessageHandler? handler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        _folder = folder;
        _source = source ?? BuiltInSource.Published;
        _http = CreateClient(handler);
    }

    /// <summary>
    /// Gets how long a download may go without receiving anything before it's given up (60 s by default). A stalled
    /// connection would otherwise hold the install lock, and everything waiting for it, until the server restarts.
    /// </summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Makes sure the program for a platform and a model are installed and intact, downloading what is missing.
    /// </summary>
    /// <param name="platform">Platform (see <see cref="BuiltInSource.CurrentPlatform"/>).</param>
    /// <param name="model">Model setting value (<c>base</c> or <c>small</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The program's path and the model's path.</returns>
    /// <exception cref="SpeechToTextException">Not available, or a download or check failed.</exception>
    public async Task<(string Program, string Model)> EnsureAsync(string platform, string model, CancellationToken cancellationToken)
    {
        var program = _source.Program(platform)
            ?? throw new SpeechToTextException("The built-in speech-to-text has no build for this server (" + platform + ").") { Failure = FailureClass.BadRequest };
        var modelFile = _source.Model(model)
            ?? throw new SpeechToTextException("Unknown built-in model \"" + model + "\"; use base or small.") { Failure = FailureClass.BadRequest };

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_folder);
            Restrict(_folder, isDirectory: true);

            var programDir = Path.Combine(_folder, _source.Version, platform);
            if (!await IsIntactAsync(programDir, program, cancellationToken).ConfigureAwait(false))
            {
                await InstallProgramAsync(programDir, program, cancellationToken).ConfigureAwait(false);
            }

            var modelsDir = Path.Combine(_folder, "models");
            var modelPath = Path.Combine(modelsDir, modelFile.Name);
            if (!await MatchesAsync(modelPath, modelFile, cancellationToken).ConfigureAwait(false))
            {
                Directory.CreateDirectory(modelsDir);
                Restrict(modelsDir, isDirectory: true);
                var temp = Path.Combine(modelsDir, "." + modelFile.Name + ".download");
                await DownloadAsync(modelFile, temp, cancellationToken).ConfigureAwait(false);
                Restrict(temp, isDirectory: false);
                File.Move(temp, modelPath, overwrite: true);
            }

            RemoveOldVersions();
            return (Path.Combine(programDir, OperatingSystem.IsWindows() ? "whisper-cli.exe" : "whisper-cli"), modelPath);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Whether a program and model are already installed (without checking them), to say on the settings page whether
    /// the first use will download.
    /// </summary>
    /// <param name="platform">Platform.</param>
    /// <param name="model">Model setting value.</param>
    /// <returns><c>true</c> if both are there.</returns>
    public bool IsInstalled(string platform, string model)
        => _source.Program(platform) is { } p && _source.Model(model) is { } m
            && p.Files!.Keys.All(f => File.Exists(Path.Combine(_folder, _source.Version, platform, f)))
            && File.Exists(Path.Combine(_folder, "models", m.Name));

    /// <inheritdoc />
    public void Dispose()
    {
        _http.Dispose();
        _lock.Dispose();
    }

    // Redirects are followed by hand, so each hop's host can be checked
    private static HttpClient CreateClient(HttpMessageHandler? handler)
    {
        var own = handler is null ? new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(10) } : null;
        try
        {
            var client = new HttpClient(handler ?? own!, disposeHandler: own is not null) { Timeout = TimeSpan.FromMinutes(30) };
            own = null;
            return client;
        }
        finally
        {
            own?.Dispose();
        }
    }

    private static async Task<bool> MatchesAsync(string path, BuiltInDownload expected, CancellationToken cancellationToken)
        => File.Exists(path) && new FileInfo(path).Length == expected.Bytes
            && string.Equals(await Sha256Async(path, cancellationToken).ConfigureAwait(false), expected.Sha256, StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> IsIntactAsync(string dir, BuiltInDownload program, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(dir) || new DirectoryInfo(dir).LinkTarget is not null)
        {
            return false;
        }

        var present = Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (!present.SetEquals(program.Files!.Keys))
        {
            return false;
        }

        foreach (var (name, sha) in program.Files)
        {
            var path = Path.Combine(dir, name);
            if (new FileInfo(path).LinkTarget is not null
                || !string.Equals(await Sha256Async(path, cancellationToken).ConfigureAwait(false), sha, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }
    }

    // Only the server's user may read, write or (for the program) run what is installed
    private static void Restrict(string path, bool isDirectory, bool executable = false)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        if (isDirectory || executable)
        {
            mode |= UnixFileMode.UserExecute;
        }

        File.SetUnixFileMode(path, mode);
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next attempt
        }
    }

    private async Task InstallProgramAsync(string programDir, BuiltInDownload program, CancellationToken cancellationToken)
    {
        var versionDir = Path.GetDirectoryName(programDir)!;
        Directory.CreateDirectory(versionDir);
        Restrict(versionDir, isDirectory: true);
        var zip = Path.Combine(versionDir, "." + program.Name + ".download");
        var staging = programDir + ".staging";
        DeleteQuietly(staging);
        try
        {
            await DownloadAsync(program, zip, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(staging);
            Restrict(staging, isDirectory: true);
            var archive = await ZipFile.OpenReadAsync(zip, cancellationToken).ConfigureAwait(false);
            await using (archive.ConfigureAwait(false))
            {
                var names = archive.Entries.Select(e => e.FullName).ToList();
                if (names.Count != names.Distinct(StringComparer.Ordinal).Count() || !names.ToHashSet(StringComparer.Ordinal).SetEquals(program.Files!.Keys))
                {
                    throw Integrity(program.Name + " doesn't hold exactly the expected files");
                }

                foreach (var entry in archive.Entries)
                {
                    // Names were checked against the compiled-in list above; also refuse anything that isn't a plain file name
                    var name = Path.GetFileName(entry.FullName);
                    if (!string.Equals(name, entry.FullName, StringComparison.Ordinal) || name is "." or "..")
                    {
                        throw Integrity(program.Name + " holds a file in a folder");
                    }

                    var target = Path.Combine(staging, name);
                    await entry.ExtractToFileAsync(target, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(await Sha256Async(target, cancellationToken).ConfigureAwait(false), program.Files![entry.FullName], StringComparison.OrdinalIgnoreCase))
                    {
                        throw Integrity(entry.FullName + " in " + program.Name + " doesn't match its checksum");
                    }

                    Restrict(target, isDirectory: false, executable: !entry.FullName.EndsWith(".txt", StringComparison.Ordinal));
                }
            }

            DeleteQuietly(programDir);
            Directory.Move(staging, programDir);
        }
        finally
        {
            DeleteQuietly(zip);
            DeleteQuietly(staging);
        }
    }

    private async Task DownloadAsync(BuiltInDownload file, string temp, CancellationToken cancellationToken)
    {
        var url = new Uri(_source.BaseUrl, file.Name);
        try
        {
            for (var hop = 0; ; hop++)
            {
                if (url.Scheme != Uri.UriSchemeHttps || !_source.AllowedHosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase))
                {
                    throw Integrity("the download was redirected to " + url.Host + ", which isn't allowed");
                }

                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idle.CancelAfter(IdleTimeout);
                HttpResponseMessage response;
                try
                {
                    response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw Stalled(file);
                }

                using var owned = response;
                if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (hop >= MaxRedirects || response.Headers.Location is not { } next)
                    {
                        throw new SpeechToTextException("Downloading " + file.Name + " failed: too many redirects.") { Failure = FailureClass.Transient };
                    }

                    url = next.IsAbsoluteUri ? next : new Uri(url, next);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new SpeechToTextException("Downloading " + file.Name + " failed (HTTP " + (int)response.StatusCode + ").") { Failure = FailureClass.Transient };
                }

                if (response.Content.Headers.ContentLength is { } length && length != file.Bytes)
                {
                    throw Integrity(file.Name + " isn't the expected size");
                }

                await SaveAsync(response, file, temp, IdleTimeout, cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        catch (HttpRequestException ex)
        {
            DeleteQuietly(temp);
            throw new SpeechToTextException("Downloading " + file.Name + " failed: " + ex.Message, ex) { Failure = FailureClass.Transient };
        }
        catch
        {
            DeleteQuietly(temp);
            throw;
        }
    }

    private static SpeechToTextException Stalled(BuiltInDownload file)
        => new("Downloading " + file.Name + " stalled: nothing arrived for a while. It will be tried again.") { Failure = FailureClass.Transient };

    // Each read must bring something within the idle time; the timer starts again after every chunk
    private static async Task SaveAsync(HttpResponseMessage response, BuiltInDownload file, string temp, TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            idle.CancelAfter(idleTimeout);
            await SaveAsync(response, file, temp, idle, idleTimeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Stalled(file);
        }
    }

    private static async Task SaveAsync(HttpResponseMessage response, BuiltInDownload file, string temp, CancellationTokenSource idle, TimeSpan idleTimeout)
    {
        var cancellationToken = idle.Token;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (body.ConfigureAwait(false))
        {
            var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
            await using (output.ConfigureAwait(false))
            {
                Restrict(temp, isDirectory: false);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > file.Bytes)
                    {
                        throw Integrity(file.Name + " is larger than expected");
                    }

                    sha.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    idle.CancelAfter(idleTimeout);
                }

                if (total != file.Bytes)
                {
                    throw new SpeechToTextException("Downloading " + file.Name + " stopped early.") { Failure = FailureClass.Transient };
                }
            }
        }

        if (!string.Equals(Convert.ToHexStringLower(sha.GetHashAndReset()), file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw Integrity(file.Name + " doesn't match the checksum built into the plugin");
        }
    }

    private static SpeechToTextException Integrity(string what)
        => new("The built-in speech-to-text download was refused: " + what + ". Nothing was run.") { Failure = FailureClass.BadRequest };

    // Earlier releases' programs are no longer used
    private void RemoveOldVersions()
    {
        foreach (var dir in Directory.EnumerateDirectories(_folder))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith("whisper-v", StringComparison.Ordinal) && !string.Equals(name, _source.Version, StringComparison.Ordinal))
            {
                DeleteQuietly(dir);
            }
        }
    }
}
