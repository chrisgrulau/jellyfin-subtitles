using System.IO;
using Jellyfin.Plugin.Subtitles.Pipeline;
using Jellyfin.Plugin.Subtitles.SpeechToText;
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

        // Results and the originals of changed subtitles also live in the plugin's data folder
        serviceCollection.AddSingleton(sp => new SubtitleProcessor(
            new ResultStore(Path.Combine(DataFolder(sp), "results.json")),
            new SubtitleFiles(Path.Combine(DataFolder(sp), "originals"))));
    }

    private static string DataFolder(System.IServiceProvider services)
        => Path.Combine(services.GetRequiredService<IApplicationPaths>().PluginsPath, typeof(SubtitlesPlugin).Assembly.GetName().Name!);
}
