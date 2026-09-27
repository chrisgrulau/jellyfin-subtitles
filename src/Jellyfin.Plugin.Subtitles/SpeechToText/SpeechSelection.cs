using System;
using System.Linq;
using System.Net.Http;
using Jellyfin.Plugin.Subtitles.Configuration;
using Jellyfin.Plugin.Subtitles.Pricing;
using Jellyfin.Plugin.Subtitles.SpeechToText.BuiltIn;

namespace Jellyfin.Plugin.Subtitles.SpeechToText;

/// <summary>
/// Which speech-to-text service a use of it gets: the service its tier names, built by <see cref="SpeechToTextFactory"/>,
/// and for a paid service kept within the spending limits (see <see cref="MeteredSpeechToText"/>). Used by the scheduled
/// tasks, the entry point for other plugins and the settings page's Test.
/// </summary>
internal static class SpeechSelection
{
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
    /// A remote service (Deepgram, OpenAI, a local OpenAI-compatible service) retries transient failures inside the
    /// metering, so a call is reserved once (see <see cref="RetryingSpeechToText"/>). For checks (not full transcripts that
    /// become subtitles), a free service on this server stands in when the chosen one fails, if
    /// <see cref="PluginConfiguration.FallBackToFree"/> allows it (see <see cref="FallbackSpeechToText"/>).
    /// </summary>
    /// <param name="config">Plugin settings.</param>
    /// <param name="tier">The tier (for example <see cref="PluginConfiguration.AiContext"/>).</param>
    /// <param name="keys">Speech-to-text keys.</param>
    /// <param name="http">HTTP client.</param>
    /// <param name="builtIn">The built-in speech-to-text.</param>
    /// <param name="spending">Prices, ledger and exchange rates.</param>
    /// <param name="purpose">What the calls are for.</param>
    /// <param name="problem">Why no service is used, if none.</param>
    /// <param name="forSubtitles">Whether the transcript becomes subtitles (see <see cref="SpeechToTextFactory.Create"/>).</param>
    /// <param name="errors">Where calls and failures are counted, if anywhere.</param>
    /// <param name="run">The run the calls belong to.</param>
    /// <returns>The service, or <c>null</c>.</returns>
    public static ISpeechToText? SpeechFor(PluginConfiguration config, TranscriptionTier? tier, SpeechToTextKeys keys, HttpClient http, BuiltInHost? builtIn, Spending spending, string purpose, out string? problem, bool forSubtitles = false, SpeechErrorLog? errors = null, string? run = null)
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
        var (service, why) = SpeechToTextFactory.Create(tier.Provider, tier.Model, config.LocalServiceUrl, spending.PaidMayBeUsed(limits), config.AllowBuiltInDownload, keys, http, builtIn, forSubtitles);
        problem = why;
        if (service is null)
        {
            return null;
        }

        service = Retrying(service, errors, run);
        if (paid)
        {
            service = new MeteredSpeechToText(service, ModelOf(tier.Provider, tier.Model), spending, limits, purpose);
        }

        // A mixed transcript (some chunks from one service, some from another) would be cached under the wrong service, so
        // full transcripts don't fall back
        var chain = forSubtitles ? [] : SpeechFallback.Chain(config.FallBackToFree, tier.Provider, config.LocalServiceUrl, config.AllowBuiltInDownload, BuiltInModelInstalled(builtIn) is not null);
        if (chain.Count == 0)
        {
            return service;
        }

        return new FallbackSpeechToText(service, [.. chain.Select(standIn => (standIn, (Func<ISpeechToText?>)(() =>
        {
            var model = standIn == SpeechToTextFactory.BuiltIn ? BuiltInModelInstalled(builtIn) ?? string.Empty : string.Empty;
            var (free, _) = SpeechToTextFactory.Create(standIn, model, config.LocalServiceUrl, paidAllowed: false, config.AllowBuiltInDownload, keys, http, builtIn);
            return free is null ? null : Retrying(free, errors, run);
        })))]);
    }

    /// <summary>
    /// A remote service with its transient failures retried; the built-in service (which runs here, with its own time
    /// limit) isn't retried, but its calls and failures are counted too.
    /// </summary>
    /// <param name="service">The service.</param>
    /// <param name="errors">Where calls and failures are counted.</param>
    /// <param name="run">The run.</param>
    /// <returns>The service to call.</returns>
    internal static ISpeechToText Retrying(ISpeechToText service, SpeechErrorLog? errors, string? run)
        => new RetryingSpeechToText(service, errors, run, maxAttempts: service.Id == SpeechToTextFactory.BuiltIn ? 1 : SpeechRetry.MaxAttempts);

    /// <summary>
    /// The built-in model already installed (the default first), if any: a fallback never starts a download.
    /// </summary>
    /// <param name="builtIn">The built-in speech-to-text.</param>
    /// <returns>The model, or <c>null</c>.</returns>
    internal static string? BuiltInModelInstalled(BuiltInHost? builtIn)
        => builtIn is null || builtIn.Platform is null || builtIn.Problem is not null ? null
            : builtIn.IsInstalled("base") ? "base"
            : builtIn.IsInstalled("small") ? "small"
            : null;

    /// <summary>
    /// A service kept within the spending limits when it is paid (as the settings page's Test uses it); a free one as is.
    /// </summary>
    /// <param name="service">The service.</param>
    /// <param name="model">The model setting.</param>
    /// <param name="limits">The spending limits (the page's unsaved values for Test, SUB-29).</param>
    /// <param name="spending">Prices, ledger and exchange rates.</param>
    /// <param name="purpose">What the calls are for.</param>
    /// <returns>The service to call.</returns>
    public static ISpeechToText Metered(ISpeechToText service, string? model, Jellyfin.Plugin.Common.Costs.SpendLimits limits, Spending spending, string purpose)
    {
        ArgumentNullException.ThrowIfNull(service);
        return SpeechToTextFactory.IsPaid(service.Id)
            ? new MeteredSpeechToText(service, ModelOf(service.Id, model), spending, limits, purpose)
            : service;
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
}
