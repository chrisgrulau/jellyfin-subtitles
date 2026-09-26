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
/// The nightly full-transcript run (off by default): after the search for missing subtitles, videos it found nothing
/// fitting for are transcribed whole with the "Full transcript" service, a few a night, and a subtitle generated from the
/// transcript is added (see <see cref="SubtitleGenerator"/>); and doubtful subtitle files, or those picked in the results,
/// are compared whole with a full transcript, their differing lines waiting for review (see <see cref="WholeFileChecker"/>).
/// Its own task, an hour after the search, because transcribing whole videos can take hours on a CPU, and the search
/// (and its button) shouldn't wait for that. Both share one time budget; files picked in the results go first.
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
    private readonly WholeFileChecker _checker;
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
    /// <param name="checker">The whole-file check.</param>
    /// <param name="logger">Logger.</param>
    public SubtitleGenerateTask(ILibraryManager library, IMediaSourceManager media, IMediaEncoder encoder, ILibraryMonitor monitor, IHttpClientFactory http, SpeechToTextKeys keys, BuiltInHost builtIn, Spending spending, SubtitleGenerator generator, WholeFileChecker checker, ILogger<SubtitleGenerateTask> logger)
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
        _checker = checker ?? throw new ArgumentNullException(nameof(checker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The HTTP timeout for one chunk (ten minutes of audio) sent to a speech-to-text service.</summary>
    public static readonly TimeSpan ChunkTimeout = TimeSpan.FromMinutes(20);

    /// <inheritdoc />
    public string Name => "Generate missing subtitles and check whole files";

    /// <inheritdoc />
    public string Key => "ShoalSubtitlesGenerate";

    /// <inheritdoc />
    public string Description => "Transcribes whole videos, when switched on: generates subtitles for videos no subtitle could be found for, and compares doubtful subtitles (or those picked in the results) with what is said (Shoal Subtitles).";

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
            _checker.FlushResults();
        }
    }

    private async Task RunAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = SubtitlesPlugin.Instance?.Configuration;
        if (config is null || !config.Enabled)
        {
            return;
        }

        var max = config.GenerateMissing ? Math.Clamp(config.MaxGeneratedPerNight, 0, SubtitleGenerator.MaxPerNight) : 0;
        var maxWhole = Math.Clamp(config.MaxWholeFileChecksPerNight, 0, WholeFileChecker.MaxPerNight);
        if (!_generator.ResultsReadable)
        {
            LogSkipped(_logger, _generator.ResultsProblem ?? "unknown");
            return;
        }

        var asked = maxWhole > 0 && _checker.HasRequests;
        if (max == 0 && !asked && (!config.CheckWholeFile || maxWhole == 0))
        {
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

        var wanted = LanguageSettings.EffectiveLanguages(config.Languages);
        var setup = SubtitleGenerator.SetupOf(tier.Provider, tier.Model);
        var budget = SubtitleGenerator.BudgetOf(config.MaxGenerateHours);
        var began = DateTimeOffset.UtcNow;
        var videos = new LibraryVideos(_library, _media);
        var settings = new WholeFileSettings(wanted, config.TuneConfidence, RunStart.PoliciesFor(config).Auditor);
        List<SubtitleJob>? files = null;
        List<SubtitleJob> Files() => files ??= [.. videos.SubtitleFiles(wanted.Select(Languages.ToTwoLetter).OfType<string>().ToHashSet(StringComparer.Ordinal))];
        var checks = 0;

        // Files picked in the results first (someone is waiting for them), then generation, then doubtful files
        if (asked)
        {
            // Picked files the run can't reach (the settings or the library changed since) leave the queue, with the reason
            _checker.ClearUnreachable(Files(), wanted);
            var outcome = await CheckWholeAsync(run, config, tier, setup, settings, _checker.Choose(Files(), wanted, automatic: false, maxWhole), budget, began, cancellationToken).ConfigureAwait(false);
            checks += outcome?.Checked + outcome?.Failed ?? 0;
            if (outcome?.StoppedBy is not null || outcome?.OutOfTime is not null)
            {
                return;
            }
        }

        if (max > 0)
        {
            var speech = run.Speech(config, tier, "subtitles.generate", out var problem, forSubtitles: true);
            if (speech is null)
            {
                LogSkipped(_logger, problem ?? "no speech-to-text service");
                return;
            }

            var jobs = _generator.Choose(videos.Missing(wanted, config.CountImageSubtitles), wanted, setup, max);
            LogStarting(_logger, jobs.Count, setup);
            var outcome = await _generator.RunAsync(
                jobs,
                job => new FfmpegAudioSource(run.Ffmpeg, job.VideoPath, job.AudioStream),
                speech,
                setup,
                budget,
                began,
                (job, result) =>
                {
                    LogResult(_logger, job.Name, result.Status, result.Explanation);
                    if (result.Status == ResultStatus.Generated)
                    {
                        _monitor.ReportFileSystemChanged(result.SubtitlePath);
                    }
                },
                progress,
                cancellationToken).ConfigureAwait(false);
            if (outcome.StoppedBy is { } why)
            {
                LogStopped(_logger, why);
            }

            var summary = outcome.Summary();
            LogSummary(_logger, summary);
            if (outcome.StoppedBy is not null || outcome.OutOfTime is not null)
            {
                return;
            }
        }

        if (config.CheckWholeFile && maxWhole - checks > 0)
        {
            await CheckWholeAsync(run, config, tier, setup, settings, _checker.Choose(Files(), wanted, automatic: true, maxWhole - checks), budget, began, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<WholeFileRun?> CheckWholeAsync(RunStart run, PluginConfiguration config, TranscriptionTier tier, string setup, WholeFileSettings settings, IReadOnlyList<SubtitleJob> jobs, TimeSpan? budget, DateTimeOffset began, CancellationToken cancellationToken)
    {
        if (jobs.Count == 0)
        {
            return null;
        }

        var speech = run.Speech(config, tier, "subtitles.wholefile", out var problem, forSubtitles: true);
        if (speech is null)
        {
            LogSkipped(_logger, problem ?? "no speech-to-text service");
            return null;
        }

        LogWholeStarting(_logger, jobs.Count, setup);
        var outcome = await _checker.RunAsync(
            jobs,
            job => new FfmpegAudioSource(run.Ffmpeg, job.VideoPath, job.AudioStream),
            speech,
            setup,
            settings,
            budget,
            began,
            (job, result) => LogResult(_logger, job.Name, result.Status, result.WholeFile?.Summary ?? result.Explanation),
            null,
            cancellationToken).ConfigureAwait(false);
        if (outcome.StoppedBy is { } why)
        {
            LogStopped(_logger, why);
        }

        var line = outcome.Summary();
        LogSummary(_logger, line);
        return outcome;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Shoal Subtitles: checking {Count} subtitle files whole (full transcript: {Setup})")]
    private static partial void LogWholeStarting(ILogger logger, int count, string setup);

    [LoggerMessage(Level = LogLevel.Information, Message = "Shoal Subtitles: full transcripts skipped: {Problem}")]
    private static partial void LogSkipped(ILogger logger, string problem);

    [LoggerMessage(Level = LogLevel.Information, Message = "Shoal Subtitles: generating subtitles for {Count} videos (full transcript: {Setup})")]
    private static partial void LogStarting(ILogger logger, int count, string setup);

    [LoggerMessage(Level = LogLevel.Information, Message = "Shoal Subtitles: {Name}: {Status}. {Explanation}")]
    private static partial void LogResult(ILogger logger, string name, ResultStatus status, string explanation);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shoal Subtitles: full transcripts stopped for tonight: {Message}")]
    private static partial void LogStopped(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "Shoal Subtitles: {Summary}")]
    private static partial void LogSummary(ILogger logger, string summary);
}
