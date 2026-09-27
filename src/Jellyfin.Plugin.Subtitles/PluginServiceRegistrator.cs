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

        // The built-in speech-to-text installs under Jellyfin's data folder (only after the administrator allows it), never
        // under the plugins folder, where Jellyfin would take its DLLs for a plugin (SUB-15)
        serviceCollection.AddSingleton(sp => new BuiltInHost(
            Path.Combine(sp.GetRequiredService<IApplicationPaths>().DataPath, "shoal-subtitles"),
            legacyFolder: DataFolder(sp)));

        // Spending on paid services: published prices, the month's ledger and exchange rates, shared by tasks and page
        serviceCollection.AddSingleton(sp => new Pricing.Spending(DataFolder(sp)));

        // Results, the originals of changed subtitles and the download count also live in the plugin's data folder. One
        // results store is shared, so the checker knows what the finder added.
        // What needs attention, and subtitles added, are also written to Jellyfin's Activity log (FAM-05)
        serviceCollection.AddSingleton(sp =>
        {
            var activity = sp.GetRequiredService<MediaBrowser.Model.Activity.IActivityManager>();
            return new SubtitleActivity(
                note => activity.CreateAsync(new Jellyfin.Database.Implementations.Entities.ActivityLog(note.Name, SubtitleActivity.Type, System.Guid.Empty)
                {
                    ShortOverview = note.ShortOverview,
                    Overview = note.Overview,
                    LogSeverity = note.Severity,
                }),
                System.TimeProvider.System,
                () => SubtitlesPlugin.Instance?.Configuration.WriteToActivityLog != false);
        });
        serviceCollection.AddSingleton(sp =>
        {
            var store = new ResultStore(Path.Combine(DataFolder(sp), "results.json"));
            var activity = sp.GetRequiredService<SubtitleActivity>();
            store.Recorded = r => _ = activity.NotifyAsync(r);
            return store;
        });
        // Full transcripts are kept (compactly, under a size cap) so a video is never transcribed twice; confidence
        // thresholds learned from subtitles known to be good are kept beside them
        serviceCollection.AddSingleton(sp => new Generation.TranscriptCache(Path.Combine(DataFolder(sp), "transcripts")));
        serviceCollection.AddSingleton(sp => new Discrepancy.ConfidenceCalibration(Path.Combine(DataFolder(sp), "calibration.json")));
        serviceCollection.AddSingleton(sp => new SubtitleProcessor(sp.GetRequiredService<ResultStore>(), new SubtitleFiles(Path.Combine(DataFolder(sp), "originals"), sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SubtitleFiles>>()))
        {
            Calibration = sp.GetRequiredService<Discrepancy.ConfidenceCalibration>(),
        });
        serviceCollection.AddSingleton(sp => new SubtitleGenerator(sp.GetRequiredService<ResultStore>(), new SubtitleFiles(Path.Combine(DataFolder(sp), "originals"), sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SubtitleFiles>>()), cache: sp.GetRequiredService<Generation.TranscriptCache>()));
        serviceCollection.AddSingleton(sp => new WholeFileChecker(sp.GetRequiredService<ResultStore>(), sp.GetRequiredService<Generation.TranscriptCache>(), sp.GetRequiredService<Discrepancy.ConfidenceCalibration>()));
        serviceCollection.AddSingleton(sp => new EmbeddedChecker(sp.GetRequiredService<ResultStore>()));
        // The in-process entry point other plugins of the family use for short transcripts (no HTTP endpoint): its work is
        // a registered service; the static method they find by reflection forwards to it
        serviceCollection.AddSingleton<Bridge.SpeechBridgeService>();
        serviceCollection.AddHostedService<Bridge.SpeechBridgeHost>();

        // The nightly tasks share the gate; handling new videos and restoring all originals each need it alone. New videos
        // are queued from Jellyfin's item events and handled after a quiet delay.
        serviceCollection.AddSingleton<RunGate>();
        serviceCollection.AddHostedService<NewItemsHost>();

        serviceCollection.AddSingleton(sp => new SubtitleFinder(sp.GetRequiredService<ResultStore>(), new DownloadLedger(Path.Combine(DataFolder(sp), "downloads.json"))));
    }

    private static string DataFolder(System.IServiceProvider services)
        => Path.Combine(services.GetRequiredService<IApplicationPaths>().PluginsPath, typeof(SubtitlesPlugin).Assembly.GetName().Name!);
}
