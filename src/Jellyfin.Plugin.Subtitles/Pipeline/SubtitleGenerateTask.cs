using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.Audio;
using Jellyfin.Plugin.Subtitles.Configuration;
using Jellyfin.Plugin.Subtitles.Pricing;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Jellyfin.Plugin.Subtitles.SpeechToText.BuiltIn;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Subtitles.Pipeline;

/// <summary>
/// The nightly generation of last-resort subtitles (off by default): after the search for missing subtitles, videos it
/// found nothing fitting for are transcribed whole with the "Full transcript" service, a few a night, and a subtitle
/// generated from the transcript is added (see <see cref="SubtitleGenerator"/>). Its own task, an hour after the search,
/// because transcribing whole videos can take hours on a CPU, and the search (and its button) shouldn't wait for that.
/// </summary>
public sealed partial class SubtitleGenerateTask : IScheduledTask
{
    private readonly ILibraryManager _library;
    private readonly IMediaSourceManager _media;
    private readonly IMediaEncoder _encoder;
    private readonly ILibraryMonitor _monitor;
    private readonly IHttpClientFactory _http;
    private readonly SpeechToTextKeys _keys;
    private readonly BuiltInHost _builtIn;
    private readonly Spending _spending;
    private readonly SubtitleGenerator _generator;
    private readonly ILogger<SubtitleGenerateTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleGenerateTask"/> class.
    /// </summary>
    /// <param name="library">Library manager.</param>
    /// <param name="media">Media source manager.</param>
    /// <param name="encoder">Media encoder (for Jellyfin's ffmpeg).</param>
    /// <param name="monitor">Library monitor (told about added files).</param>
    /// <param name="http">HTTP client factory.</param>
    /// <param name="keys">Speech-to-text keys.</param>
    /// <param name="builtIn">The built-in speech-to-text.</param>
    /// <param name="spending">Prices, spend ledger and exchange rates.</param>
    /// <param name="generator">The generator.</param>
    /// <param name="logger">Logger.</param>
    public SubtitleGenerateTask(ILibraryManager library, IMediaSourceManager media, IMediaEncoder encoder, ILibraryMonitor monitor, IHttpClientFactory http, SpeechToTextKeys keys, BuiltInHost builtIn, Spending spending, SubtitleGenerator generator, ILogger<SubtitleGenerateTask> logger)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _media = media ?? throw new ArgumentNullException(nameof(media));
        _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _builtIn = builtIn ?? throw new ArgumentNullException(nameof(builtIn));
        _spending = spending ?? throw new ArgumentNullException(nameof(spending));
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The HTTP timeout for one chunk (ten minutes of audio) sent to a speech-to-text service.</summary>
    public static readonly TimeSpan ChunkTimeout = TimeSpan.FromMinutes(20);

    /// <inheritdoc />
    public string Name => "Generate missing subtitles";

    /// <inheritdoc />
    public string Key => "ShoalSubtitlesGenerate";

    /// <inheritdoc />
    public string Description => "Generates subtitles from a full transcript for videos no subtitle could be found for, when switched on (Shoal Subtitles).";

    /// <inheritdoc />
    public string Category => "Shoal";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(5).Ticks };
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
            _generator.FlushResults();
        }
    }

    private async Task RunAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = SubtitlesPlugin.Instance?.Configuration;
        if (config is null || !config.Enabled || !config.GenerateMissing)
        {
            return;
        }

        var max = Math.Clamp(config.MaxGeneratedPerNight, 0, SubtitleGenerator.MaxPerNight);
        if (max == 0)
        {
            return;
        }

        if (!_generator.ResultsReadable)
        {
            LogSkipped(_logger, _generator.ResultsProblem ?? "unknown");
            return;
        }

        if (!config.SetupSaved && !_generator.HasResults)
        {
            LogSkipped(_logger, "waiting for the plugin's settings to be saved once (Dashboard → Plugins → Subtitles)");
            return;
        }

        using var run = await RunStart.BeginAsync(_encoder, _http, _keys, _builtIn, _spending, cancellationToken, ChunkTimeout).ConfigureAwait(false);
        if (run is null)
        {
            LogSkipped(_logger, "Jellyfin's ffmpeg wasn't found");
            return;
        }

        var tier = config.FullTranscript ?? new TranscriptionTier();
        if (!tier.Enabled)
        {
            LogSkipped(_logger, "the Full transcript speech-to-text is switched off");
            return;
        }

        var speech = run.Speech(config, tier, "subtitles.generate", out var problem, forSubtitles: true);
        if (speech is null)
        {
            LogSkipped(_logger, problem ?? "no speech-to-text service");
            return;
        }

        var wanted = LanguageSettings.EffectiveLanguages(config.Languages);
        var setup = SubtitleGenerator.SetupOf(tier.Provider, tier.Model);
        var jobs = _generator.Choose(new LibraryVideos(_library, _media).Missing(wanted, config.CountImageSubtitles), wanted, setup, max);
        LogStarting(_logger, jobs.Count, setup);
        int generated = 0, noSpeech = 0, failed = 0;
        for (var i = 0; i < jobs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var job = jobs[i];
            try
            {
                var result = await _generator.GenerateAsync(job, new FfmpegAudioSource(run.Ffmpeg, job.VideoPath, job.AudioStream), speech, setup, cancellationToken).ConfigureAwait(false);
                if (result is not null)
                {
                    LogResult(_logger, job.Name, result.Status, result.Explanation);
                    switch (result.Status)
                    {
                        case ResultStatus.Generated:
                            generated++;
                            _monitor.ReportFileSystemChanged(result.SubtitlePath);
                            break;
                        case ResultStatus.NoSpeech:
                            noSpeech++;
                            break;
                        default:
                            failed++;
                            break;
                    }
                }
            }
            catch (SpeechToTextException ex)
            {
                // The spending limit, the provider's own limit or its sign-in: every further video would be refused too
                LogStopped(_logger, ex.Message);
                break;
            }
#pragma warning disable CA1031 // One odd video mustn't stop the nightly run: it is recorded as failed and tried again later
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                failed++;
                LogFailed(_logger, job.Name, ex.Message);
                _generator.RecordFailure(job, setup, ex.GetType().Name + ": " + ex.Message);
            }

            progress.Report(100.0 * (i + 1) / jobs.Count);
        }

        LogSummary(_logger, generated, noSpeech, failed);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Shoal Subtitles: generating subtitles skipped: {Problem}")]
    private static partial void LogSkipped(ILogger logger, string problem);

    [LoggerMessage(Level = LogLevel.Information, Message = "Shoal Subtitles: generating subtitles for {Count} videos (full transcript: {Setup})")]
    private static partial void LogStarting(ILogger logger, int count, string setup);

    [LoggerMessage(Level = LogLevel.Information, Message = "Shoal Subtitles: {Name}: {Status}. {Explanation}")]
    private static partial void LogResult(ILogger logger, string name, ResultStatus status, string explanation);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shoal Subtitles: generating subtitles stopped for tonight: {Message}")]
    private static partial void LogStopped(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shoal Subtitles: {Name}: generating a subtitle failed: {Error}")]
    private static partial void LogFailed(ILogger logger, string name, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Shoal Subtitles: generated {Generated} subtitles; {NoSpeech} videos had no speech to transcribe; {Failed} failed")]
    private static partial void LogSummary(ILogger logger, int generated, int noSpeech, int failed);
}
