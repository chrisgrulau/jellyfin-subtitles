using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.Subtitles.Pipeline;

/// <summary>
/// A Jellyfin library of films or shows, as the plugin sees it: its id, name and folders.
/// </summary>
/// <param name="Id">The library's id (its collection folder's item id).</param>
/// <param name="Name">Its name, for display.</param>
/// <param name="Locations">Its folders.</param>
public sealed record LibraryInfo(string Id, string Name, IReadOnlyList<string> Locations);

/// <summary>
/// Which libraries the plugin works on (all by default; the settings page can leave some out, such as anime or children's
/// libraries). Every walk of the library, the handling of new videos and generating subtitles ask it. A video belongs to
/// the library whose folder holds it (the longest match, when folders nest); a video in no known library's folder is
/// never left out. Libraries are stored as left out rather than chosen, so a library added later is worked on, like
/// every other, until it is unticked.
/// </summary>
public sealed class LibraryScope
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly HashSet<string> _excluded;
    private readonly List<(string Folder, LibraryInfo Library)> _folders;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryScope"/> class.
    /// </summary>
    /// <param name="libraries">The server's libraries.</param>
    /// <param name="excluded">The ids of libraries left out (see <see cref="Configuration.PluginConfiguration.ExcludedLibraries"/>).</param>
    public LibraryScope(IEnumerable<LibraryInfo> libraries, IEnumerable<string>? excluded)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        Libraries = [.. libraries.Where(l => l is not null)];
        _excluded = (excluded ?? []).Select(NormaliseId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        _folders = [.. Libraries
            .SelectMany(l => (l.Locations ?? []).Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => (Folder: Path.TrimEndingDirectorySeparator(f.Trim()), Library: l)))
            .OrderByDescending(x => x.Folder.Length)];
    }

    /// <summary>Gets a scope with no libraries known and none left out (everything is worked on).</summary>
    public static LibraryScope Everything { get; } = new([], null);

    /// <summary>Gets the server's libraries.</summary>
    public IReadOnlyList<LibraryInfo> Libraries { get; }

    /// <summary>
    /// A library id in one form (32 hex digits, lower case), however it was written; <c>null</c> when it isn't an id.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>The id, or <c>null</c>.</returns>
    public static string? NormaliseId(string? id) => Guid.TryParse(id?.Trim(), out var g) && g != Guid.Empty ? g.ToString("N") : null;

    /// <summary>
    /// Whether a library is worked on.
    /// </summary>
    /// <param name="library">The library.</param>
    /// <returns><c>true</c> unless it was left out.</returns>
    public bool IsIncluded(LibraryInfo library)
    {
        ArgumentNullException.ThrowIfNull(library);
        return NormaliseId(library.Id) is not { } id || !_excluded.Contains(id);
    }

    /// <summary>
    /// The library whose folder holds a file (the deepest folder, when libraries' folders nest).
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>The library, or <c>null</c> when no library's folder holds it.</returns>
    public LibraryInfo? LibraryOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var (folder, library) in _folders)
        {
            // A folder only holds paths below it ("/media/tv" doesn't hold "/media/tv2/…"); a root such as "/" holds all
            if (path.Length > folder.Length && path.StartsWith(folder, PathComparison)
                && (folder[^1] is '/' or '\\' || path[folder.Length] is '/' or '\\'))
            {
                return library;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a video is worked on: it isn't in a library that was left out.
    /// </summary>
    /// <param name="path">The video's file.</param>
    /// <returns><c>true</c> to work on it.</returns>
    public bool Includes(string? path) => LibraryOf(path) is not { } library || IsIncluded(library);
}
