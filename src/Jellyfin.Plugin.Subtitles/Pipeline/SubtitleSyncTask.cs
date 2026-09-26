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
using Jellyfin.Plugin.Subtitles.Pricing;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Jellyfin.Plugin.Subtitles.SpeechToText.BuiltIn;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
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
    private static readonly string[] TextExtensions = [".srt", ".vtt", ".ass", ".ssa"];

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
        await _spending.Rates.RefreshAsync(http, cancellationToken).ConfigureAwait(false);
        var speech = SpeechFor(config, _keys, http, _builtIn, _spending, "subtitles.sync", out var problem);
        var policies = RunPolicies(config);
        if (speech is null && problem is not null)
        {
            LogNoSpeech(_logger, problem);
        }
        var wanted = SpendingLimit.EffectiveLanguages(config.Languages).Select(Languages.ToTwoLetter).OfType<string>().ToHashSet(StringComparer.Ordinal);

        // Results for subtitle files that were deleted are no longer needed
        var pruned = _processor.PruneGone();
        if (pruned > 0)
        {
            LogPruned(_logger, pruned);
        }

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
                var result = await _processor.ProcessAsync(job, new FfmpegAudioSource(ffmpeg, job.VideoPath, job.AudioStream), speech, policies, cancellationToken).ConfigureAwait(false);
                LogResult(_logger, job.Name, result.Status, result.Explanation);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
            {
                LogFailed(_logger, job.Name, ex.Message);
                _processor.RecordFailure(job, ex.Message);
            }

            progress.Report(100.0 * (i + 1) / todo.Count);
        }

        if (policies.Auditor is not null && speech is not null && config.MaxAuditsOfEarlierPerRun > 0)
        {
            await AuditEarlierAsync(jobs, ffmpeg, speech, policies.Auditor, Math.Min(config.MaxAuditsOfEarlierPerRun, 200), cancellationToken).ConfigureAwait(false);
        }

        if (config.CheckEmbeddedSubtitles)
        {
            await CheckEmbeddedAsync(config, wanted, ffmpeg, speech, policies, cancellationToken).ConfigureAwait(false);
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
            {
                LogFailed(_logger, job.Name, ex.Message);
            }
        }
    }

    // Text subtitle tracks inside videos, a few per run (each is copied out by reading the whole video)
    private async Task CheckEmbeddedAsync(PluginConfiguration config, HashSet<string> wanted, string ffmpeg, ISpeechToText? speech, Policies policies, CancellationToken cancellationToken)
    {
        var todo = EmbeddedJobs(wanted).Where(_embedded.NeedsCheck).Take(Math.Clamp(config.MaxEmbeddedPerRun, 1, 200)).ToList();
        LogEmbeddedStarting(_logger, todo.Count);
        foreach (var job in todo)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var bytes = await FfmpegSubtitleExtractor.ExtractAsync(ffmpeg, job.VideoPath, job.StreamIndex, job.Codec, cancellationToken).ConfigureAwait(false);
                var result = await _embedded.CheckAsync(job, bytes, new FfmpegAudioSource(ffmpeg, job.VideoPath, job.AudioStream), speech, policies, cancellationToken).ConfigureAwait(false);
                LogResult(_logger, job.Name, result.Status, result.Explanation);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
            {
                LogFailed(_logger, job.Name, ex.Message);
                _embedded.RecordFailure(job, ex.Message);
            }
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
    /// The settings for one run, with the AI plugin's help where the settings allow it: matching lines by meaning and
    /// auditing the wording, sharing that run's allowance of AI checks.
    /// </summary>
    /// <param name="config">Plugin settings.</param>
    /// <returns>The policies.</returns>
    public static Policies RunPolicies(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var policies = PoliciesOf(config);
        if (!config.UseAi || config.MaxAiChecksPerRun <= 0)
        {
            return policies;
        }

        var checks = new Ai.AiChecks(config.MaxAiChecksPerRun);
        return policies with { Matcher = new Ai.AiLineMatcher(checks), Auditor = config.AuditWording ? new Ai.AiTextAuditor(checks) : null };
    }

    /// <summary>
    /// The speech-to-text service automatic runs use: the snippet tier's service. A paid service is used only within the
    /// spending limits: every call is priced, reserved against the month's limit and settled (see
    /// <see cref="MeteredSpeechToText"/>).
    /// </summary>
    /// <param name="config">Plugin settings.</param>
    /// <param name="keys">Speech-to-text keys.</param>
    /// <param name="http">HTTP client.</param>
    /// <param name="builtIn">The built-in speech-to-text.</param>
    /// <param name="spending">Prices, ledger and exchange rates.</param>
    /// <param name="purpose">What the calls are for (<c>subtitles.sync</c> …).</param>
    /// <param name="problem">Why no service is used, if none.</param>
    /// <returns>The service, or <c>null</c>.</returns>
    public static ISpeechToText? SpeechFor(PluginConfiguration config, SpeechToTextKeys keys, HttpClient http, BuiltInHost? builtIn, Spending spending, string purpose, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(config);
        return SpeechFor(config, config.SyncSnippets, keys, http, builtIn, spending, purpose, out problem);
    }

    /// <summary>
    /// The speech-to-text service a tier uses, metered like <see cref="SpeechFor(PluginConfiguration, SpeechToTextKeys, HttpClient, BuiltInHost?, Spending, string, out string?)"/>.
    /// </summary>
    /// <param name="config">Plugin settings.</param>
    /// <param name="tier">The tier (for example <see cref="PluginConfiguration.AiContext"/>).</param>
    /// <param name="keys">Speech-to-text keys.</param>
    /// <param name="http">HTTP client.</param>
    /// <param name="builtIn">The built-in speech-to-text.</param>
    /// <param name="spending">Prices, ledger and exchange rates.</param>
    /// <param name="purpose">What the calls are for.</param>
    /// <param name="problem">Why no service is used, if none.</param>
    /// <returns>The service, or <c>null</c>.</returns>
    public static ISpeechToText? SpeechFor(PluginConfiguration config, TranscriptionTier? tier, SpeechToTextKeys keys, HttpClient http, BuiltInHost? builtIn, Spending spending, string purpose, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(spending);
        ArgumentNullException.ThrowIfNull(config);
        problem = null;
        if (tier is null || !tier.Enabled)
        {
            return null;
        }

        var limits = Spending.LimitsOf(config);
        var paid = SpeechToTextFactory.IsPaid(tier.Provider);
        var (service, why) = SpeechToTextFactory.Create(tier.Provider, tier.Model, config.LocalServiceUrl, SpendingLimit.AllowsPaidUsage(limits.Overall), config.AllowBuiltInDownload, keys, http, builtIn);
        problem = why;
        return service is not null && paid ? new MeteredSpeechToText(service, ModelOf(tier.Provider, tier.Model), spending, limits, purpose) : service;
    }

    /// <summary>
    /// The model a paid provider uses for a setting (its default when the setting is empty), for pricing.
    /// </summary>
    /// <param name="provider">Provider id.</param>
    /// <param name="model">The model setting.</param>
    /// <returns>The model name.</returns>
    public static string ModelOf(string provider, string? model)
        => !string.IsNullOrWhiteSpace(model) ? model.Trim()
            : provider == SpeechToTextFactory.Deepgram ? DeepgramSpeechToText.DefaultModel
            : provider == SpeechToTextFactory.OpenAi ? OpenAiCompatibleSpeechToText.OpenAiDefaultModel
            : string.Empty;

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

    // Embedded text tracks in the chosen languages, for videos with no subtitle file of that language beside them
    private IEnumerable<EmbeddedJob> EmbeddedJobs(HashSet<string> languages)
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

            var beside = streams.Where(s => s.Type == MediaStreamType.Subtitle && s.IsExternal).Select(s => Languages.ToTwoLetter(s.Language)).OfType<string>().ToHashSet(StringComparer.Ordinal);
            foreach (var sub in streams.Where(s => s.Type == MediaStreamType.Subtitle && !s.IsExternal && !s.IsForced && s.IsTextSubtitleStream))
            {
                if (Languages.ToTwoLetter(sub.Language) is not { } lang || !languages.Contains(lang) || beside.Contains(lang)
                    || !FfmpegSubtitleExtractor.TextCodecs.Contains(sub.Codec ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                beside.Add(lang);
                yield return new EmbeddedJob(item.Id, item.Name, video.Path, sub.Index, sub.Codec, sub.Language!, TimeSpan.FromTicks(video.RunTimeTicks!.Value), AudioChoice.For(audio, sub.Language), EmbeddedChecker.FingerprintOf(video.Path, sub.Index));
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Shoal Subtitles: speech-to-text not used: {Problem}")]
    private static partial void LogNoSpeech(ILogger logger, string problem);
}
