using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Subtitles.Audio;
using Jellyfin.Plugin.Subtitles.Candidates;
using Jellyfin.Plugin.Subtitles.Configuration;
using Jellyfin.Plugin.Subtitles.SpeechToText;
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
    private readonly SubtitleFinder _finder;
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
    /// <param name="finder">The finder.</param>
    /// <param name="logger">Logger.</param>
    public SubtitleFindTask(ILibraryManager library, IMediaSourceManager media, IMediaEncoder encoder, ISubtitleManager subtitles, ILibraryMonitor monitor, IHttpClientFactory http, SpeechToTextKeys keys, SubtitleFinder finder, ILogger<SubtitleFindTask> logger)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _media = media ?? throw new ArgumentNullException(nameof(media));
        _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
        _subtitles = subtitles ?? throw new ArgumentNullException(nameof(subtitles));
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _finder = finder ?? throw new ArgumentNullException(nameof(finder));
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
        var config = SubtitlesPlugin.Instance?.Configuration;
        if (config is null || !config.Enabled || !config.FindMissing)
        {
            return;
        }

        var ffmpeg = _encoder.EncoderPath;
        if (string.IsNullOrEmpty(ffmpeg) || !File.Exists(ffmpeg))
        {
            LogNoFfmpeg(_logger);
            return;
        }

        using var http = _http.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(3);
        var speech = SubtitleSyncTask.FreeSpeechFor(config, _keys, http, out var problem);
        if (speech is null && problem is not null)
        {
            LogNoSpeech(_logger, problem);
        }

        var source = new JellyfinSubtitleSource(_library, _subtitles);
        // Specials (season 0) last: subtitle sites rarely have them, and they'd use up the run
        var jobs = Missing(SpendingLimit.EffectiveLanguages(config.Languages), config.CountImageSubtitles)
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
                var result = await _finder.FindAsync(job, source, new FfmpegAudioSource(ffmpeg, job.VideoPath, job.AudioStream), speech, SubtitleSyncTask.PoliciesOf(config), config.MaxDownloadsPerDay, cancellationToken).ConfigureAwait(false);
                LogResult(_logger, job.Name, result.Status, result.Explanation);
                if (result.Status == ResultStatus.Added)
                {
                    // Jellyfin picks the new file up as it would from real-time monitoring
                    _monitor.ReportFileSystemChanged(result.SubtitlePath);
                }
            }
            catch (DownloadLimitReachedException ex)
            {
                LogLimit(_logger, ex.Message);
                break;
            }
            catch (Exception ex) when (FindRules.StopsTheRun(ex))
            {
                // The provider's own daily allowance is used up, or it can't sign in: every further search would fail too
                LogProviderStopped(_logger, ex.Message);
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

    private IEnumerable<FindJob> Missing(IReadOnlyList<string> languages, bool countImages)
    {
        var items = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            IsVirtualItem = false,
            Recursive = true,
        });
        foreach (var item in items)
        {
            if (item is not Video video || string.IsNullOrEmpty(video.Path) || video.RunTimeTicks is not > 0 || !File.Exists(video.Path))
            {
                continue;
            }

            var streams = _media.GetMediaStreams(item.Id);
            var audio = streams.Where(s => s.Type == MediaStreamType.Audio).OrderBy(s => s.Index).Select(s => ((string?)s.Language, s.IsDefault)).ToList();
            if (audio.Count == 0)
            {
                continue;
            }

            var have = streams.Where(s => s.Type == MediaStreamType.Subtitle && FindRules.Counts(s.IsForced, s.IsTextSubtitleStream, countImages))
                .Select(s => Languages.ToTwoLetter(s.Language)).OfType<string>().ToHashSet(StringComparer.Ordinal);
            foreach (var language in languages)
            {
                if (Languages.ToTwoLetter(language) is { } two && !have.Contains(two))
                {
                    yield return new FindJob(item.Id, item.Name, video.Path, VideoFactsReader.Read(video), language, TimeSpan.FromTicks(video.RunTimeTicks!.Value), AudioChoice.For(audio, language));
                }
            }
        }
    }

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shoal Subtitles: {Name}: the search failed: {Error}")]
    private static partial void LogFailed(ILogger logger, string name, string error);
}
