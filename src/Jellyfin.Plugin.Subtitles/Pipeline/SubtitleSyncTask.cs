using System;
using System.Collections.Generic;
using System.IO;
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
/// The daily run: finds text subtitle files beside the library's films and episodes, in the chosen languages, and checks
/// the ones not checked before (or changed since). A paid speech-to-text service is used only within the monthly
/// spending limit: each call is priced, reserved and recorded (see <see cref="MeteredSpeechToText"/>).
/// </summary>
public sealed partial class SubtitleSyncTask : IScheduledTask
{
    private readonly ILibraryManager _library;
    private readonly IMediaSourceManager _media;
    private readonly IMediaEncoder _encoder;
    private readonly IHttpClientFactory _http;
    private readonly SpeechToTextKeys _keys;
    private readonly BuiltInHost _builtIn;
    private readonly Spending _spending;
    private readonly EmbeddedChecker _embedded;
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
    /// <param name="builtIn">The built-in speech-to-text.</param>
    /// <param name="spending">Prices, spend ledger and exchange rates.</param>
    /// <param name="embedded">Checks subtitle tracks inside videos (when switched on).</param>
    /// <param name="processor">Processes one subtitle.</param>
    /// <param name="logger">Logger.</param>
    public SubtitleSyncTask(ILibraryManager library, IMediaSourceManager media, IMediaEncoder encoder, IHttpClientFactory http, SpeechToTextKeys keys, BuiltInHost builtIn, Spending spending, EmbeddedChecker embedded, SubtitleProcessor processor, ILogger<SubtitleSyncTask> logger)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _media = media ?? throw new ArgumentNullException(nameof(media));
        _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _builtIn = builtIn ?? throw new ArgumentNullException(nameof(builtIn));
        _spending = spending ?? throw new ArgumentNullException(nameof(spending));
        _embedded = embedded ?? throw new ArgumentNullException(nameof(embedded));
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
        try
        {
            await RunAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Results are written in batches; whatever is still waiting is written when the run ends, however it ends
            _processor.FlushResults();
        }
    }

    private async Task RunAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = SubtitlesPlugin.Instance?.Configuration;
        if (config is null || !config.Enabled)
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
        var speech = run.Speech(config, config.SyncSnippets, "subtitles.sync", out var problem);
        var policies = RunStart.PoliciesFor(config);
        if (speech is null && problem is not null)
        {
            LogNoSpeech(_logger, problem);
        }
        var wanted = LanguageSettings.EffectiveLanguages(config.Languages).Select(Languages.ToTwoLetter).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var unknown in LanguageSettings.UnknownLanguages(config.Languages))
        {
            LogUnknownLanguage(_logger, unknown);
        }

        // The results file holds the undo records: if it can't be read, nothing runs (and nothing overwrites it)
        if (!_processor.ResultsReadable)
        {
            LogNoResults(_logger, _processor.ResultsProblem ?? "unknown");
            return;
        }

        // A new install does nothing until its settings page has been saved once (an install that has run before counts)
        if (!config.SetupSaved && !_processor.HasResults)
        {
            LogNoResults(_logger, "waiting for the plugin's settings to be saved once (Dashboard → Plugins → Subtitles)");
            return;
        }

        // Results for subtitle files that were deleted are no longer needed
        var pruned = _processor.PruneGone();
        if (pruned > 0)
        {
            LogPruned(_logger, pruned);
        }

        var videos = new LibraryVideos(_library, _media, JellyfinLibraries.Scope(_library, config));
        var jobs = videos.SubtitleFiles(wanted).ToList();
        var todo = new List<SubtitleJob>();
        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var fingerprint = await SubtitleFiles.FingerprintFileAsync(job.SubtitlePath, cancellationToken).ConfigureAwait(false);
                if (_processor.NeedsCheck(job.SubtitlePath, fingerprint, speech?.Id ?? string.Empty))
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
                var result = await _processor.ProcessAsync(job, new FfmpegAudioSource(ffmpeg, job.VideoPath, job.AudioStream), speech, policies, cancellationToken).ConfigureAwait(false);
                LogResult(_logger, job.Name, result.Status, result.Explanation);
            }
#pragma warning disable CA1031 // One odd file (a subtitle from the internet can hold anything) mustn't stop the nightly run: it is recorded as failed
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogFailed(_logger, job.Name, ex.Message);
                _processor.RecordFailure(job, ex is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException ? ex.Message : ex.GetType().Name + ": " + ex.Message);
            }

            progress.Report(100.0 * (i + 1) / todo.Count);
        }

        if (policies.Auditor is not null && speech is not null && config.MaxAuditsOfEarlierPerRun > 0)
        {
            await AuditEarlierAsync(jobs, ffmpeg, speech, policies.Auditor, Math.Min(config.MaxAuditsOfEarlierPerRun, 200), cancellationToken).ConfigureAwait(false);
        }

        if (config.CheckEmbeddedSubtitles)
        {
            await CheckEmbeddedAsync(config, videos, wanted, ffmpeg, speech, policies, cancellationToken).ConfigureAwait(false);
        }
    }

    // Subtitles checked before the wording audit existed, a few per run (oldest results first). Each attempt counts,
    // answered or not, so a run whose AI checks are used up doesn't keep transcribing.
    private async Task AuditEarlierAsync(IReadOnlyList<SubtitleJob> jobs, string ffmpeg, ISpeechToText speech, Audit.ITextAuditor auditor, int max, CancellationToken cancellationToken)
    {
        foreach (var job in jobs.Where(j => _processor.NeedsAudit(j.SubtitlePath)).OrderBy(j => _processor.LastChecked(j.SubtitlePath)).Take(max))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await _processor.AuditAsync(job, new FfmpegAudioSource(ffmpeg, job.VideoPath, job.AudioStream), speech, auditor, cancellationToken).ConfigureAwait(false);
                if (result is not null)
                {
                    LogResult(_logger, job.Name, result.Status, result.Explanation);
                }
            }
            catch (SpeechToTextException ex)
            {
                LogFailed(_logger, job.Name, ex.Message);
                break;
            }
#pragma warning disable CA1031 // One odd file (a subtitle from the internet can hold anything) mustn't stop the nightly run: it is recorded as failed
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogFailed(_logger, job.Name, ex.Message);
            }
        }
    }

    // Text subtitle tracks inside videos, a few per run (each is copied out by reading the whole video)
    private async Task CheckEmbeddedAsync(PluginConfiguration config, LibraryVideos videos, HashSet<string> wanted, string ffmpeg, ISpeechToText? speech, Policies policies, CancellationToken cancellationToken)
    {
        var todo = videos.EmbeddedTracks(wanted, config.CountImageSubtitles).Where(_embedded.NeedsCheck).Take(Math.Clamp(config.MaxEmbeddedPerRun, 1, 200)).ToList();
        LogEmbeddedStarting(_logger, todo.Count);
        foreach (var job in todo)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A corrected copy goes beside the video: don't read the whole video if it can't be written there
            if (Path.GetDirectoryName(job.VideoPath) is { } folder && !SubtitleFiles.CanWrite(folder))
            {
                _embedded.RecordFailure(job, "Jellyfin's account can't write in this video's folder (a read-only mount, or folder permissions?).");
                continue;
            }

            try
            {
                var bytes = await FfmpegSubtitleExtractor.ExtractAsync(ffmpeg, job.VideoPath, job.StreamIndex, job.Codec, cancellationToken).ConfigureAwait(false);
                var result = await _embedded.CheckAsync(job, bytes, new FfmpegAudioSource(ffmpeg, job.VideoPath, job.AudioStream), speech, policies, cancellationToken).ConfigureAwait(false);
                LogResult(_logger, job.Name, result.Status, result.Explanation);
            }
#pragma warning disable CA1031 // One odd file (a subtitle from the internet can hold anything) mustn't stop the nightly run: it is recorded as failed
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogFailed(_logger, job.Name, ex.Message);
                _embedded.RecordFailure(job, ex is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException ? ex.Message : ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Shoal Subtitles: checking {Count} subtitle tracks inside videos")]
    private static partial void LogEmbeddedStarting(ILogger logger, int count);

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Shoal Subtitles: dropped {Count} results for subtitle files that no longer exist")]
    private static partial void LogPruned(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shoal Subtitles: \"{Language}\" in the subtitle languages isn't a language this plugin recognises; it is ignored")]
    private static partial void LogUnknownLanguage(ILogger logger, string language);

    [LoggerMessage(Level = LogLevel.Information, Message = "Shoal Subtitles: speech-to-text not used: {Problem}")]
    private static partial void LogNoSpeech(ILogger logger, string problem);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Subtitle check skipped: {Problem}")]
    private static partial void LogNoResults(ILogger logger, string problem);
}
