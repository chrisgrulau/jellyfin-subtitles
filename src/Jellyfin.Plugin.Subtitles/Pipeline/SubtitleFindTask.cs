using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.Audio;
using Jellyfin.Plugin.Subtitles.Candidates;
using Jellyfin.Plugin.Subtitles.Configuration;
using Jellyfin.Plugin.Subtitles.Pricing;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Jellyfin.Plugin.Subtitles.SpeechToText.BuiltIn;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Subtitles.Pipeline;

/// <summary>
/// The daily search for missing subtitles: films and episodes with no subtitle (external or embedded) in a chosen
/// language are searched through Jellyfin's subtitle providers (such as the OpenSubtitles plugin); a candidate is only
/// added once it has been checked against the audio. Downloads are capped per day, below the provider's own allowance.
/// </summary>
public sealed partial class SubtitleFindTask : IScheduledTask
{
    private readonly ILibraryManager _library;
    private readonly IMediaSourceManager _media;
    private readonly IMediaEncoder _encoder;
    private readonly ISubtitleManager _subtitles;
    private readonly ILibraryMonitor _monitor;
    private readonly IHttpClientFactory _http;
    private readonly SpeechToTextKeys _keys;
    private readonly BuiltInHost _builtIn;
    private readonly Spending _spending;
    private readonly SubtitleFinder _finder;
    private readonly SubtitleGenerator _generator;
    private readonly SubtitleActivity? _activity;
    private readonly ILogger<SubtitleFindTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleFindTask"/> class.
    /// </summary>
    /// <param name="library">Library manager.</param>
    /// <param name="media">Media source manager.</param>
    /// <param name="encoder">Media encoder (for Jellyfin's ffmpeg).</param>
    /// <param name="subtitles">Jellyfin's subtitle providers.</param>
    /// <param name="monitor">Library monitor (told about added files).</param>
    /// <param name="http">HTTP client factory.</param>
    /// <param name="keys">Speech-to-text keys.</param>
    /// <param name="builtIn">The built-in speech-to-text.</param>
    /// <param name="spending">Prices, spend ledger and exchange rates.</param>
    /// <param name="finder">The finder.</param>
    /// <param name="generator">Generated subtitles (a found subtitle replaces one).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="activity">Jellyfin's Activity log, for a search a provider stopped.</param>
    public SubtitleFindTask(ILibraryManager library, IMediaSourceManager media, IMediaEncoder encoder, ISubtitleManager subtitles, ILibraryMonitor monitor, IHttpClientFactory http, SpeechToTextKeys keys, BuiltInHost builtIn, Spending spending, SubtitleFinder finder, SubtitleGenerator generator, ILogger<SubtitleFindTask> logger, SubtitleActivity? activity = null)
    {
        _activity = activity;
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _media = media ?? throw new ArgumentNullException(nameof(media));
        _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
        _subtitles = subtitles ?? throw new ArgumentNullException(nameof(subtitles));
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _builtIn = builtIn ?? throw new ArgumentNullException(nameof(builtIn));
        _spending = spending ?? throw new ArgumentNullException(nameof(spending));
        _finder = finder ?? throw new ArgumentNullException(nameof(finder));
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Name => "Find missing subtitles";

    /// <inheritdoc />
    public string Key => "ShoalSubtitlesFind";

    /// <inheritdoc />
    public string Description => "Finds subtitles for films and episodes that have none in your languages, and adds them once they fit the audio (Shoal Subtitles).";

    /// <inheritdoc />
    public string Category => "Shoal";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(4).Ticks };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        try
        {
            await RunAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Results are written in batches; whatever is still waiting is written when the run ends, however it ends
            _finder.FlushResults();
        }
    }

    private async Task RunAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = SubtitlesPlugin.Instance?.Configuration;
        if (config is null || !config.Enabled || !config.FindMissing)
        {
            return;
        }

        using var run = await RunStart.BeginAsync(_encoder, _http, _keys, _builtIn, _spending, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            LogNoFfmpeg(_logger);
            return;
        }

        var ffmpeg = run.Ffmpeg;
        if (!_finder.ResultsReadable)
        {
            LogNoResults(_logger, _finder.ResultsProblem ?? "unknown");
            return;
        }

        // A new install downloads nothing until its settings page has been saved once
        if (!config.SetupSaved && !_finder.HasResults)
        {
            LogNoResults(_logger, "waiting for the plugin's settings to be saved once (Dashboard → Plugins → Subtitles)");
            return;
        }

        var speech = run.Speech(config, config.SyncSnippets, "subtitles.find", out var problem);
        var policies = RunStart.PoliciesFor(config) with { Auditor = null };
        if (speech is null && problem is not null)
        {
            LogNoSpeech(_logger, problem);
        }

        // Jellyfin's own subtitle providers first (such as the OpenSubtitles plugin), then SubDL when a key is set
        var jellyfin = new JellyfinSubtitleSource(_library, _subtitles);
        using var subdlHttp = _http.CreateClient();
        subdlHttp.Timeout = TimeSpan.FromSeconds(60);
        var subdlKey = _keys.Get(SubDlKey);
        var combined = subdlKey is null ? null : new CombinedSource([jellyfin, new SubDlSource(subdlHttp, subdlKey, IdsOf)]);
        ICandidateSource source = combined ?? (ICandidateSource)jellyfin;
        // Specials (season 0) last: subtitle sites rarely have them, and they'd use up the run
        var jobs = new LibraryVideos(_library, _media, JellyfinLibraries.Scope(_library, config)).Missing(LanguageSettings.EffectiveLanguages(config.Languages), config.CountImageSubtitles)
            .Where(_finder.NeedsSearch)
            .OrderBy(j => j.Video.Season == 0)
            .Take(Math.Max(1, config.MaxFindsPerRun))
            .ToList();
        LogStarting(_logger, jobs.Count, speech?.Id ?? "none");
        for (var i = 0; i < jobs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var job = jobs[i];
            try
            {
                var result = await _finder.FindAsync(job, source, new FfmpegAudioSource(ffmpeg, job.VideoPath, job.AudioStream), speech, policies, config.MaxDownloadsPerDay, cancellationToken).ConfigureAwait(false);
                LogResult(_logger, job.Name, result.Status, result.Explanation);
                foreach (var p in combined?.Problems ?? [])
                {
                    LogSourceProblem(_logger, p);
                }
                if (result.Status == ResultStatus.Added)
                {
                    // Jellyfin picks the new file up as it would from real-time monitoring
                    _monitor.ReportFileSystemChanged(result.SubtitlePath);

                    // A subtitle generated for this video and language while none could be found gives way to the real one
                    if (_generator.ReplaceGenerated(job.VideoPath, job.Language, result.SubtitlePath) is { } replaced)
                    {
                        LogResult(_logger, job.Name, replaced.Status, replaced.Explanation);
                        _monitor.ReportFileSystemChanged(replaced.SubtitlePath);
                    }
                }
            }
            catch (DownloadLimitReachedException ex)
            {
                LogLimit(_logger, ex.Message);
                break;
            }
            catch (NoSourceAnsweredException ex)
            {
                // Nothing is recorded, so the video is searched again next run; with no provider at all, the rest would
                // find nothing either
                LogNoSource(_logger, job.Name, ex.Message);
                if (ex.NoneAvailable)
                {
                    break;
                }
            }
            catch (Exception ex) when (FindRules.StopsTheRun(ex))
            {
                // The provider's own daily allowance is used up, or it can't sign in: every further search would fail too
                LogProviderStopped(_logger, ex.Message);
                if (_activity is not null)
                {
                    await _activity.NotifyStoppedAsync(ex.Message).ConfigureAwait(false);
                }

                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Anything else a provider throws fails only this video's search; the run carries on
                LogFailed(_logger, job.Name, ex.Message);
            }

            progress.Report(100.0 * (i + 1) / jobs.Count);
        }
    }

    /// <summary>The key-store id of the SubDL key.</summary>
    public const string SubDlKey = "subdl";

    // The ids SubDL searches by: the film's, or for an episode the show's, with season and episode numbers
    private VideoIds? IdsOf(Guid itemId)
    {
        switch (_library.GetItemById(itemId))
        {
            case MediaBrowser.Controller.Entities.TV.Episode ep when ep.Series is { } series && ep.ParentIndexNumber is { } season && ep.IndexNumber is { } number:
                return new VideoIds(series.GetProviderId(MetadataProvider.Imdb), series.GetProviderId(MetadataProvider.Tmdb), season, number);
            case MediaBrowser.Controller.Entities.Movies.Movie movie:
                return new VideoIds(movie.GetProviderId(MetadataProvider.Imdb), movie.GetProviderId(MetadataProvider.Tmdb), null, null);
            default:
                return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shoal Subtitles: search skipped: {Problem}")]
    private static partial void LogNoResults(ILogger logger, string problem);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shoal Subtitles: Jellyfin's ffmpeg wasn't found; missing subtitles can't be checked")]
    private static partial void LogNoFfmpeg(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Shoal Subtitles: speech-to-text not used: {Problem}")]
    private static partial void LogNoSpeech(ILogger logger, string problem);

    [LoggerMessage(Level = LogLevel.Information, Message = "Shoal Subtitles: searching for {Count} missing subtitles (speech-to-text: {Speech})")]
    private static partial void LogStarting(ILogger logger, int count, string speech);

    [LoggerMessage(Level = LogLevel.Information, Message = "Shoal Subtitles: {Name}: {Status}. {Explanation}")]
    private static partial void LogResult(ILogger logger, string name, ResultStatus status, string explanation);

    [LoggerMessage(Level = LogLevel.Information, Message = "Shoal Subtitles: {Message}")]
    private static partial void LogLimit(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shoal Subtitles: the subtitle provider stopped the search for today: {Message}")]
    private static partial void LogProviderStopped(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "Shoal Subtitles: a subtitle provider couldn't be searched: {Problem}")]
    private static partial void LogSourceProblem(ILogger logger, string problem);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shoal Subtitles: {Name}: not searched, tried again next run: {Problem}")]
    private static partial void LogNoSource(ILogger logger, string name, string problem);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shoal Subtitles: {Name}: the search failed: {Error}")]
    private static partial void LogFailed(ILogger logger, string name, string error);
}
