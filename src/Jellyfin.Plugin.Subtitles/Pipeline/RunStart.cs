using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.Configuration;
using Jellyfin.Plugin.Subtitles.Pricing;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Jellyfin.Plugin.Subtitles.SpeechToText.BuiltIn;
using MediaBrowser.Controller.MediaEncoding;

namespace Jellyfin.Plugin.Subtitles.Pipeline;

/// <summary>
/// What every run starts with, in one place (the sync and find tasks and the entry point for other plugins): Jellyfin's
/// ffmpeg, an HTTP client with a 3-minute timeout, exchange rates refreshed when due, and then the speech-to-text service
/// the run's use of it names.
/// </summary>
internal sealed class RunStart : IDisposable
{
    /// <summary>The HTTP timeout for a run's provider calls.</summary>
    public static readonly TimeSpan HttpTimeout = TimeSpan.FromMinutes(3);

    private readonly SpeechToTextKeys _keys;
    private readonly BuiltInHost? _builtIn;
    private readonly Spending _spending;

    private RunStart(string ffmpeg, HttpClient http, SpeechToTextKeys keys, BuiltInHost? builtIn, Spending spending)
    {
        Ffmpeg = ffmpeg;
        Http = http;
        _keys = keys;
        _builtIn = builtIn;
        _spending = spending;
    }

    /// <summary>Gets Jellyfin's ffmpeg.</summary>
    public string Ffmpeg { get; }

    /// <summary>Gets the run's HTTP client.</summary>
    public HttpClient Http { get; }

    /// <summary>
    /// Starts a run: finds ffmpeg, makes the HTTP client and refreshes the exchange rates when due.
    /// </summary>
    /// <param name="encoder">Jellyfin's media encoder (for ffmpeg).</param>
    /// <param name="http">HTTP client factory.</param>
    /// <param name="keys">Speech-to-text keys.</param>
    /// <param name="builtIn">The built-in speech-to-text.</param>
    /// <param name="spending">Prices, ledger and exchange rates.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="httpTimeout">The HTTP timeout, or <c>null</c> for <see cref="HttpTimeout"/> (full transcripts send ten
    /// minutes of audio per call, which a local service on a CPU can take longer than that to transcribe).</param>
    /// <returns>The run's start, or <c>null</c> when Jellyfin's ffmpeg wasn't found (nothing can be checked).</returns>
    public static async Task<RunStart?> BeginAsync(IMediaEncoder encoder, IHttpClientFactory http, SpeechToTextKeys keys, BuiltInHost? builtIn, Spending spending, CancellationToken cancellationToken, TimeSpan? httpTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(spending);
        if (Audio.FfmpegLocator.Resolve(encoder.EncoderPath) is not { } ffmpeg)
        {
            return null;
        }

        var client = http.CreateClient();
        try
        {
            client.Timeout = httpTimeout ?? HttpTimeout;
            await spending.CurrentRatesAsync(client, cancellationToken).ConfigureAwait(false);
            return new RunStart(ffmpeg, client, keys, builtIn, spending);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The settings for one run, with the AI plugin's help where the settings allow it: matching lines by meaning and
    /// auditing the wording, sharing that run's allowance of AI checks.
    /// </summary>
    /// <param name="config">Plugin settings.</param>
    /// <returns>The policies.</returns>
    public static Policies PoliciesFor(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var policies = config.Policies();
        if (!config.UseAi || config.MaxAiChecksPerRun <= 0)
        {
            return policies;
        }

        var checks = new Ai.AiChecks(config.MaxAiChecksPerRun);
        return policies with { Matcher = new Ai.AiLineMatcher(checks), Auditor = config.AuditWording ? new Ai.AiTextAuditor(checks) : null };
    }

    /// <summary>
    /// The speech-to-text service for a use of it (see <see cref="SpeechSelection"/>).
    /// </summary>
    /// <param name="config">Plugin settings.</param>
    /// <param name="tier">The use (for example <see cref="PluginConfiguration.SyncSnippets"/>).</param>
    /// <param name="purpose">What the calls are for (<c>subtitles.sync</c> …).</param>
    /// <param name="problem">Why no service is used, if none.</param>
    /// <param name="forSubtitles">Whether the transcript becomes subtitles (full transcripts).</param>
    /// <returns>The service, or <c>null</c>.</returns>
    public ISpeechToText? Speech(PluginConfiguration config, TranscriptionTier? tier, string purpose, out string? problem, bool forSubtitles = false)
        => SpeechSelection.SpeechFor(config, tier, _keys, Http, _builtIn, _spending, purpose, out problem, forSubtitles);

    /// <inheritdoc />
    public void Dispose() => Http.Dispose();
}
