using System;
using System.IO;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.Subtitles.Pipeline;

/// <summary>
/// Changes subtitle files safely: the original is copied to the plugin's backup folder first (once), the new content is
/// written to a hidden temporary file beside it and then renamed into place (so a crash never leaves a half-written
/// subtitle), and undo only restores the original if nobody has changed the file since.
/// </summary>
public sealed class SubtitleFiles
{
    private readonly string _backups;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleFiles"/> class.
    /// </summary>
    /// <param name="backupFolder">Where originals are kept (the plugin's data folder).</param>
    public SubtitleFiles(string backupFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupFolder);
        _backups = backupFolder;
    }

    /// <summary>
    /// A file's content fingerprint (SHA-256, hex).
    /// </summary>
    /// <param name="bytes">The content.</param>
    /// <returns>The fingerprint.</returns>
    public static string Fingerprint(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

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

    private static void WriteAtomically(string path, byte[] content)
    {
        var temp = Path.Combine(Path.GetDirectoryName(path)!, ".shoal-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temp, content);
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
}
