using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Subtitles.Audio;
using Jellyfin.Plugin.Subtitles.Configuration;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Subtitles.Pipeline;

/// <summary>
/// The daily run: finds text subtitle files beside the library's films and episodes, in the chosen languages, and checks
/// the ones not checked before (or changed since). Paid speech-to-text isn't used by automatic runs yet: that waits for
/// cost tracking, so a run can never spend money.
/// </summary>
public sealed partial class SubtitleSyncTask : IScheduledTask
{
    private static readonly string[] TextExtensions = [".srt", ".vtt", ".ass", ".ssa"];

    private readonly ILibraryManager _library;
    private readonly IMediaSourceManager _media;
    private readonly IMediaEncoder _encoder;
    private readonly IHttpClientFactory _http;
    private readonly SpeechToTextKeys _keys;
    private readonly SubtitleProcessor _processor;
    private readonly ILogger<SubtitleSyncTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleSyncTask"/> class.
    /// </summary>
    /// <param name="library">Library manager.</param>
    /// <param name="media">Media source manager (streams, including external subtitles).</param>
    /// <param name="encoder">Media encoder (for Jellyfin's ffmpeg).</param>
    /// <param name="http">HTTP client factory.</param>
    /// <param name="keys">Speech-to-text keys.</param>
    /// <param name="processor">Processes one subtitle.</param>
    /// <param name="logger">Logger.</param>
    public SubtitleSyncTask(ILibraryManager library, IMediaSourceManager media, IMediaEncoder encoder, IHttpClientFactory http, SpeechToTextKeys keys, SubtitleProcessor processor, ILogger<SubtitleSyncTask> logger)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _media = media ?? throw new ArgumentNullException(nameof(media));
        _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Name => "Check and sync subtitles";

    /// <inheritdoc />
    public string Key => "ShoalSubtitlesSync";

    /// <inheritdoc />
    public string Description => "Checks subtitle files against the audio and fixes their timing (Shoal Subtitles).";

    /// <inheritdoc />
    public string Category => "Shoal";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(3).Ticks };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var config = SubtitlesPlugin.Instance?.Configuration;
        if (config is null || !config.Enabled)
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
        var speech = FreeSpeechFor(config, _keys, http, out var problem);
        if (speech is null && problem is not null)
        {
            LogNoSpeech(_logger, problem);
        }
        var wanted = SpendingLimit.EffectiveLanguages(config.Languages).Select(Languages.ToTwoLetter).OfType<string>().ToHashSet(StringComparer.Ordinal);

        var jobs = Jobs(wanted).ToList();
        var todo = new List<SubtitleJob>();
        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var fingerprint = await SubtitleFiles.FingerprintFileAsync(job.SubtitlePath, cancellationToken).ConfigureAwait(false);
                if (_processor.NeedsCheck(job.SubtitlePath, fingerprint))
                {
                    todo.Add(job);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogUnreadable(_logger, job.SubtitlePath, ex.Message);
            }

            if (todo.Count >= Math.Max(1, config.MaxSubtitlesPerRun))
            {
                break;
            }
        }

        LogStarting(_logger, todo.Count, jobs.Count, speech?.Id ?? "none");
        for (var i = 0; i < todo.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var job = todo[i];
            try
            {
                var result = await _processor.ProcessAsync(job, new FfmpegAudioSource(ffmpeg, job.VideoPath, job.AudioStream), speech, PoliciesOf(config), cancellationToken).ConfigureAwait(false);
                LogResult(_logger, job.Name, result.Status, result.Explanation);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
            {
                LogFailed(_logger, job.Name, ex.Message);
                _processor.RecordFailure(job, ex.Message);
            }

            progress.Report(100.0 * (i + 1) / todo.Count);
        }
    }

    /// <summary>
    /// The policies a configuration sets.
    /// </summary>
    /// <param name="config">Plugin settings.</param>
    /// <returns>The policies.</returns>
    public static Policies PoliciesOf(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new Policies(config.TimingFixes, config.TextChanges, config.Cleanup ?? new CleanupSettings());
    }

    /// <summary>
    /// The speech-to-text service automatic runs may use: the snippet tier's service, if it is free (local or built-in).
    /// Paid services wait for cost tracking.
    /// </summary>
    /// <param name="config">Plugin settings.</param>
    /// <param name="keys">Speech-to-text keys.</param>
    /// <param name="http">HTTP client.</param>
    /// <param name="problem">Why no service is used, if none.</param>
    /// <returns>The service, or <c>null</c>.</returns>
    public static ISpeechToText? FreeSpeechFor(PluginConfiguration config, SpeechToTextKeys keys, HttpClient http, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(config);
        problem = null;
        var tier = config.SyncSnippets;
        if (tier is null || !tier.Enabled)
        {
            return null;
        }

        if (SpeechToTextFactory.IsPaid(tier.Provider))
        {
            problem = "speech-to-text is set to " + tier.Provider + ", a paid service; automatic runs don't use paid services until cost tracking is available.";
            return null;
        }

        var (service, why) = SpeechToTextFactory.Create(tier.Provider, tier.Model, config.LocalServiceUrl, paidAllowed: false, config.AllowBuiltInDownload, keys, http);
        problem = why;
        return service;
    }

    private IEnumerable<SubtitleJob> Jobs(HashSet<string> languages)
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

            foreach (var sub in streams.Where(s => s.Type == MediaStreamType.Subtitle && s.IsExternal && !string.IsNullOrEmpty(s.Path)))
            {
                var ext = Path.GetExtension(sub.Path).ToUpperInvariant();
                if (!TextExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)) || Languages.ToTwoLetter(sub.Language) is not { } lang || !languages.Contains(lang))
                {
                    continue;
                }

                yield return new SubtitleJob(item.Id, item.Name, video.Path, sub.Path, sub.Language, TimeSpan.FromTicks(video.RunTimeTicks!.Value), AudioChoice.For(audio, sub.Language));
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shoal Subtitles: Jellyfin's ffmpeg wasn't found; subtitles can't be checked")]
    private static partial void LogNoFfmpeg(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shoal Subtitles: can't read {Path}: {Error}")]
    private static partial void LogUnreadable(ILogger logger, string path, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Shoal Subtitles: checking {Count} of {Total} subtitle files (speech-to-text: {Speech})")]
    private static partial void LogStarting(ILogger logger, int count, int total, string speech);

    [LoggerMessage(Level = LogLevel.Information, Message = "Shoal Subtitles: {Name}: {Status}. {Explanation}")]
    private static partial void LogResult(ILogger logger, string name, ResultStatus status, string explanation);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shoal Subtitles: {Name} couldn't be checked: {Error}")]
    private static partial void LogFailed(ILogger logger, string name, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Shoal Subtitles: speech-to-text not used: {Problem}")]
    private static partial void LogNoSpeech(ILogger logger, string problem);
}
