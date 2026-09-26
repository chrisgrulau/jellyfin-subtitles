using System.IO;
using Jellyfin.Plugin.Subtitles.Pipeline;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Jellyfin.Plugin.Subtitles.SpeechToText.BuiltIn;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Subtitles;

/// <summary>
/// Registers the plugin's services with Jellyfin's dependency injection container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Speech-to-text keys live in their own owner-only file in the plugin's data folder, never in the configuration
        serviceCollection.AddSingleton(sp => new SpeechToTextKeys(Path.Combine(DataFolder(sp), "keys.json")));

        // The built-in speech-to-text installs into the plugin's data folder too (only after the administrator allows it)
        serviceCollection.AddSingleton(sp => new BuiltInHost(DataFolder(sp)));

        // Spending on paid services: published prices, the month's ledger and exchange rates, shared by tasks and page
        serviceCollection.AddSingleton(sp => new Pricing.Spending(DataFolder(sp)));

        // Results, the originals of changed subtitles and the download count also live in the plugin's data folder. One
        // results store is shared, so the checker knows what the finder added.
        serviceCollection.AddSingleton(sp => new ResultStore(Path.Combine(DataFolder(sp), "results.json")));
        serviceCollection.AddSingleton(sp => new SubtitleProcessor(sp.GetRequiredService<ResultStore>(), new SubtitleFiles(Path.Combine(DataFolder(sp), "originals"))));
        serviceCollection.AddSingleton(sp => new EmbeddedChecker(sp.GetRequiredService<ResultStore>()));
        serviceCollection.AddSingleton(sp => new SubtitleFinder(sp.GetRequiredService<ResultStore>(), new DownloadLedger(Path.Combine(DataFolder(sp), "downloads.json"))));
    }

    private static string DataFolder(System.IServiceProvider services)
        => Path.Combine(services.GetRequiredService<IApplicationPaths>().PluginsPath, typeof(SubtitlesPlugin).Assembly.GetName().Name!);
}
