using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Subtitles.Pipeline;

/// <summary>
/// Changes subtitle files safely: the original is copied to the plugin's backup folder first (once), the new content is
/// written to a hidden temporary file beside it and then renamed into place (so a crash never leaves a half-written
/// subtitle), and undo only restores the original if nobody has changed the file since.
/// </summary>
public sealed partial class SubtitleFiles
{
    private static readonly System.Collections.Generic.Dictionary<string, (bool Ok, DateTimeOffset At)> Writable = new(StringComparer.Ordinal);

    private readonly string _backups;
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleFiles"/> class.
    /// </summary>
    /// <param name="backupFolder">Where originals are kept (the plugin's data folder).</param>
    /// <param name="logger">Logger (for the details of what couldn't be kept, at debug level).</param>
    public SubtitleFiles(string backupFolder, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupFolder);
        _backups = backupFolder;
        _logger = logger;
    }

    /// <summary>
    /// A file's content fingerprint (SHA-256, hex).
    /// </summary>
    /// <param name="bytes">The content.</param>
    /// <returns>The fingerprint.</returns>
    public static string Fingerprint(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// A subtitle file's fingerprint, read from disk. A file larger than <see cref="Formats.SubtitleReader.MaxBytes"/> is
    /// not read: its fingerprint comes from its size and time instead, so it is recorded as too large once and not read
    /// again until it changes.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The fingerprint.</returns>
    public static async Task<string> FingerprintFileAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        return info.Length > Formats.SubtitleReader.MaxBytes
            ? TooLargeFingerprint(info)
            : Fingerprint(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// The fingerprint used for a file too large to read.
    /// </summary>
    /// <param name="info">The file.</param>
    /// <returns>The fingerprint.</returns>
    public static string TooLargeFingerprint(FileInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"large-{info.Length}-{info.LastWriteTimeUtc.Ticks}");
    }

    /// <summary>
    /// The backup file name for a subtitle path.
    /// </summary>
    /// <param name="subtitlePath">The subtitle's path.</param>
    /// <returns>A stable file name.</returns>
    public static string BackupName(string subtitlePath)
    {
        ArgumentNullException.ThrowIfNull(subtitlePath);
        return Fingerprint(System.Text.Encoding.UTF8.GetBytes(subtitlePath))[..24] + Path.GetExtension(subtitlePath);
    }

    /// <summary>
    /// The backup file name for one original of a subtitle: the path and the content it replaces, so a file replaced from
    /// outside after this plugin changed it gets its own backup and undo never brings back an older original.
    /// </summary>
    /// <param name="subtitlePath">The subtitle's path.</param>
    /// <param name="originalFingerprint">The fingerprint of the content being replaced.</param>
    /// <returns>A stable file name.</returns>
    public static string BackupName(string subtitlePath, string originalFingerprint)
    {
        ArgumentNullException.ThrowIfNull(subtitlePath);
        ArgumentNullException.ThrowIfNull(originalFingerprint);
        return Fingerprint(System.Text.Encoding.UTF8.GetBytes(subtitlePath))[..24] + "-" + originalFingerprint[..Math.Min(16, originalFingerprint.Length)] + Path.GetExtension(subtitlePath);
    }

    /// <summary>
    /// Replaces a subtitle file's content, keeping the original.
    /// </summary>
    /// <param name="subtitlePath">The subtitle file.</param>
    /// <param name="expected">The fingerprint the file must still have (it was checked with this content).</param>
    /// <param name="content">The new content.</param>
    /// <returns>The backup of the content that was replaced, and the new content's fingerprint. Callers that already hold an
    /// earlier original for this file (their own change is still in place) keep that one for undo.</returns>
    /// <exception cref="IOException">The file changed since it was checked, or can't be written.</exception>
    public (string Backup, string Fingerprint) Replace(string subtitlePath, string expected, byte[] content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subtitlePath);
        ArgumentNullException.ThrowIfNull(content);

        var current = File.ReadAllBytes(subtitlePath);
        if (!string.Equals(Fingerprint(current), expected, StringComparison.Ordinal))
        {
            throw new IOException("The subtitle changed since it was checked; it will be checked again.");
        }

        Directory.CreateDirectory(_backups);
        var backup = BackupName(subtitlePath, expected);
        var backupPath = Path.Combine(_backups, backup);
        if (!File.Exists(backupPath))
        {
            File.WriteAllBytes(backupPath + ".tmp", current);
            File.Move(backupPath + ".tmp", backupPath, overwrite: true);
        }

        WriteAtomically(subtitlePath, content);
        return (backup, Fingerprint(content));
    }

    /// <summary>
    /// Keeps a copy of a subtitle's content in the backup folder (before this plugin removes a file it added, so it can
    /// still be recovered by hand).
    /// </summary>
    /// <param name="subtitlePath">The subtitle's path.</param>
    /// <param name="content">Its content.</param>
    /// <returns>The backup file name.</returns>
    public string Keep(string subtitlePath, byte[] content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subtitlePath);
        ArgumentNullException.ThrowIfNull(content);
        Directory.CreateDirectory(_backups);
        var backup = BackupName(subtitlePath, Fingerprint(content));
        var backupPath = Path.Combine(_backups, backup);
        if (!File.Exists(backupPath))
        {
            File.WriteAllBytes(backupPath + ".tmp", content);
            File.Move(backupPath + ".tmp", backupPath, overwrite: true);
        }

        return backup;
    }

    /// <summary>
    /// Puts the original back, if the file is still exactly as this plugin left it.
    /// </summary>
    /// <param name="subtitlePath">The subtitle file.</param>
    /// <param name="backup">The backup file name.</param>
    /// <param name="written">The fingerprint of what this plugin wrote.</param>
    /// <returns>The restored content's fingerprint.</returns>
    /// <exception cref="IOException">The file was changed since, or the backup is missing.</exception>
    public string Restore(string subtitlePath, string backup, string written)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subtitlePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(backup);
        var backupPath = Path.Combine(_backups, Path.GetFileName(backup));
        if (!File.Exists(backupPath))
        {
            throw new IOException("The original is no longer kept, so this can't be undone.");
        }

        if (File.Exists(subtitlePath) && !string.Equals(Fingerprint(File.ReadAllBytes(subtitlePath)), written, StringComparison.Ordinal))
        {
            throw new IOException("The subtitle was changed after this plugin changed it; undo would lose that change.");
        }

        var original = File.ReadAllBytes(backupPath);
        WriteAtomically(subtitlePath, original);
        return Fingerprint(original);
    }

    /// <summary>
    /// Whether Jellyfin's account can write a file in a folder (and, if given, replace that file): a hidden test file is
    /// created and removed. Answers are remembered for an hour per folder.
    /// </summary>
    /// <param name="folder">The folder.</param>
    /// <param name="file">A file in it that would be replaced, if any.</param>
    /// <returns>Whether writing looks possible.</returns>
    public static bool CanWrite(string folder, string? file = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (file is not null && File.Exists(file) && new FileInfo(file).IsReadOnly)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        lock (Writable)
        {
            if (Writable.TryGetValue(folder, out var known) && now - known.At < TimeSpan.FromHours(1))
            {
                return known.Ok;
            }
        }

        bool ok;
        var probe = Path.Combine(folder, ".shoal-write-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            ok = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ok = false;
        }

        lock (Writable)
        {
            Writable[folder] = (ok, now);
        }

        return ok;
    }

    /// <summary>
    /// Creates a new subtitle file; never replaces one that exists.
    /// </summary>
    /// <param name="subtitlePath">Where to create it.</param>
    /// <param name="content">The content.</param>
    /// <returns>The content's fingerprint.</returns>
    /// <exception cref="IOException">A file is already there.</exception>
    public static string Create(string subtitlePath, byte[] content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subtitlePath);
        ArgumentNullException.ThrowIfNull(content);
        var temp = Path.Combine(Path.GetDirectoryName(subtitlePath)!, ".shoal-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temp, content);

            // Without overwrite: if something appeared meanwhile, it stays and this fails
            File.Move(temp, subtitlePath, overwrite: false);
            return Fingerprint(content);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>
    /// Removes a subtitle file this plugin added, if it is still exactly as added.
    /// </summary>
    /// <param name="subtitlePath">The file.</param>
    /// <param name="written">The fingerprint of what was added.</param>
    /// <exception cref="IOException">The file was changed since it was added.</exception>
    public static void RemoveAdded(string subtitlePath, string written)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subtitlePath);
        if (!File.Exists(subtitlePath))
        {
            return;
        }

        if (!string.Equals(Fingerprint(File.ReadAllBytes(subtitlePath)), written, StringComparison.Ordinal))
        {
            throw new IOException("The subtitle was changed after it was added; removing it would lose that change.");
        }

        File.Delete(subtitlePath);
    }

    // The new content goes to a temporary file that takes the place of the original, keeping how the original could be
    // used: on Linux its group, where the server's user may set it (SUB-24); on Unix its permission bits (so group write
    // access for other tools or people survives); on Windows File.Replace keeps the original's access list. The owner
    // can't be kept without privileges.
    private void WriteAtomically(string path, byte[] content)
    {
        var temp = Path.Combine(Path.GetDirectoryName(path)!, ".shoal-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temp, content);
            var exists = File.Exists(path);
            if (exists && OperatingSystem.IsLinux())
            {
                // Before the mode: changing the group may clear set-id bits, which the mode then restores
                CopyGroup(path, temp);
            }

            if (exists && !OperatingSystem.IsWindows())
            {
                CopyMode(path, temp);
            }

            if (exists && OperatingSystem.IsWindows() && TryReplace(temp, path))
            {
                return;
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    // Best effort: the rewrite never fails over the group (EPERM when the server's user isn't in it, a file system without
    // groups, a C library without statx); the file then keeps the server's group, as before
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private void CopyGroup(string from, string to)
    {
        if (UnixGroup.GroupOf(from, out var problem) is not { } gid)
        {
            if (_logger is not null)
            {
                LogGroupUnread(_logger, from, problem);
            }

            return;
        }

        if (UnixGroup.GroupOf(to, out _) != gid && !UnixGroup.SetGroup(to, gid, out problem) && _logger is not null)
        {
            LogGroupNotKept(_logger, from, gid, problem);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Shoal Subtitles: couldn't read the group of {Path}: {Problem}")]
    private static partial void LogGroupUnread(ILogger logger, string path, string? problem);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Shoal Subtitles: the rewritten {Path} couldn't keep its group {Group}: {Problem}")]
    private static partial void LogGroupNotKept(ILogger logger, string path, uint group, string? problem);

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void CopyMode(string from, string to)
    {
        try
        {
            File.SetUnixFileMode(to, File.GetUnixFileMode(from));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Some shares don't allow changing modes: the file keeps the default mode, as before
        }
    }

    // Some file systems (network shares, FAT) don't support File.Replace; a plain rename is used there
    private static bool TryReplace(string temp, string path)
    {
        try
        {
            File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
