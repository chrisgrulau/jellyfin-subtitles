using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Subtitles.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Subtitles;

/// <summary>
/// Jellyfin Subtitles: finds, checks and synchronises subtitles so they match the audio.
/// </summary>
public class SubtitlesPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitlesPlugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public SubtitlesPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Subtitles";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("95265809-b7a4-4721-8baf-6739c8e6287b");

    /// <inheritdoc />
    public override string Description => "Finds, checks and synchronises subtitles so they match the audio.";

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static SubtitlesPlugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        ];
    }
}
