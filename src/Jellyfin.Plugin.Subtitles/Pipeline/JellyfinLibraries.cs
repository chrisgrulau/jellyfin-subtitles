using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Subtitles.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Subtitles.Pipeline;

/// <summary>
/// The server's film and show libraries, read from Jellyfin's virtual folders (the glue for <see cref="LibraryScope"/>).
/// </summary>
internal static class JellyfinLibraries
{
    /// <summary>
    /// The libraries that can hold films or episodes (films, shows and mixed libraries), in the server's order.
    /// </summary>
    /// <param name="library">Jellyfin's library manager.</param>
    /// <returns>The libraries.</returns>
    public static IReadOnlyList<LibraryInfo> Read(ILibraryManager library)
    {
        ArgumentNullException.ThrowIfNull(library);
        return [.. library.GetVirtualFolders()
            .Where(f => f.CollectionType is null or CollectionTypeOptions.movies or CollectionTypeOptions.tvshows or CollectionTypeOptions.mixed)
            .Where(f => LibraryScope.NormaliseId(f.ItemId) is not null)
            .Select(f => new LibraryInfo(f.ItemId, f.Name ?? string.Empty, f.Locations ?? []) { SubtitleLanguages = f.LibraryOptions?.SubtitleDownloadLanguages ?? [] })];
    }

    /// <summary>
    /// The scope the settings give: the server's libraries, less those left out, and each one's languages.
    /// </summary>
    /// <param name="library">Jellyfin's library manager.</param>
    /// <param name="config">Plugin settings.</param>
    /// <param name="server">Jellyfin's configuration (its preferred metadata language, the last fallback before English).</param>
    /// <returns>The scope.</returns>
    public static LibraryScope Scope(ILibraryManager library, PluginConfiguration config, IServerConfigurationManager server)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(server);
        return new LibraryScope(Read(library), config.ExcludedLibraries, config.Languages, server.Configuration?.PreferredMetadataLanguage);
    }
}
