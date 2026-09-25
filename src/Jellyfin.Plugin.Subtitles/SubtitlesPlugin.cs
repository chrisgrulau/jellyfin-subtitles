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
    public override string Name => "Shoal Subtitles";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("95265809-b7a4-4721-8baf-6739c8e6287b");

    /// <inheritdoc />
    public override string Description => "Finds, checks and synchronises subtitles so they match the audio.";

    /// <inheritdoc />
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        // Keep values the plugin relies on within safe bounds, whatever the settings page (or an API client) sent
        if (configuration is PluginConfiguration c)
        {
            c.Currency = SpendingLimit.NormaliseCurrency(c.Currency);
            c.MonthlyBudget = Math.Max(0, c.MonthlyBudget);
            c.ExtraChargesPercent = SpendingLimit.NormaliseExtraPercent(c.ExtraChargesPercent);
            c.MaxSubtitlesPerRun = Math.Clamp(c.MaxSubtitlesPerRun, 1, 5000);
            c.MaxFindsPerRun = Math.Clamp(c.MaxFindsPerRun, 1, 1000);
            c.MaxDownloadsPerDay = Math.Clamp(c.MaxDownloadsPerDay, 0, 10000);
            var languages = SpendingLimit.EffectiveLanguages(c.Languages);
            c.Languages.Clear();
            foreach (var l in languages)
            {
                c.Languages.Add(l);
            }
        }

        base.UpdateConfiguration(configuration);
    }

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
