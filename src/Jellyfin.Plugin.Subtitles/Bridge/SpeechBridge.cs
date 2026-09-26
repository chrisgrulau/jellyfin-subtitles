using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Resilience;
using Jellyfin.Plugin.Subtitles.Audio;
using Jellyfin.Plugin.Subtitles.Configuration;
using Jellyfin.Plugin.Subtitles.Pipeline;
using Jellyfin.Plugin.Subtitles.Pricing;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Jellyfin.Plugin.Subtitles.SpeechToText.BuiltIn;
using MediaBrowser.Controller.MediaEncoding;

namespace Jellyfin.Plugin.Subtitles.Bridge;

/// <summary>
/// The entry point the other plugins of the family use to get a short transcript, in the same server process: they
/// find this type by name and call <see cref="TranscribeAsync"/> with JSON, so no C# types are shared between plugins.
/// There is no HTTP endpoint for it.
/// <para>
/// Request (version 1): <c>{"version":1,"caller":"ingest","purpose":"ingest.episode","path":"/…/video.mkv",
/// "start":300,"length":120,"language":"en"}</c>. Reply: <c>{"version":1,"ok":true,"text":"…","language":"en",
/// "provider":"builtin"}</c> or <c>{"version":1,"ok":false,"error":"…","failure":"not-allowed|not-set-up|
/// authentication|provider-limit|transient|bad-request|no-connection"}</c>.
/// </para>
/// <para>
/// Checks: the version, that the caller is allowed on the settings page, that the purpose belongs to the caller, and
/// the stretch (at most <see cref="MaxSeconds"/>). The service is the one set for "Context for AI decisions" (built-in
/// by default); a paid service is metered against this plugin's spending limits like any other call. One transcription
/// runs at a time.
/// </para>
/// </summary>
public static partial class SpeechBridge
{
    /// <summary>The contract version.</summary>
    public const int Version = 1;

    /// <summary>The longest stretch transcribed, in seconds.</summary>
    public const int MaxSeconds = 180;

    /// <summary>The largest request accepted, in characters.</summary>
    public const int MaxRequest = 8 * 1024;

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static SpeechBridgeService? _service;

    /// <summary>
    /// Transcribes a stretch of a video for another plugin (the work is done by <see cref="SpeechBridgeService"/>).
    /// </summary>
    /// <param name="requestJson">The request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reply, as JSON. Never throws for a bad request or a failed transcription.</returns>
    public static Task<string> TranscribeAsync(string requestJson, CancellationToken cancellationToken)
        => _service?.TranscribeAsync(requestJson, cancellationToken) ?? Task.FromResult(Reply(false, "The Subtitles plugin isn't ready yet.", "transient"));

    /// <summary>
    /// Connects the entry point to the plugin's service (at start-up).
    /// </summary>
    /// <param name="service">The service.</param>
    internal static void Attach(SpeechBridgeService service) => _service = service;

    /// <summary>
    /// Checks and reads a request.
    /// </summary>
    /// <param name="json">The request.</param>
    /// <param name="config">The settings that decide.</param>
    /// <param name="request">The request, when valid.</param>
    /// <returns>Why it can't be answered, or <c>null</c> when it can.</returns>
    internal static Problem? Parse(string? json, Settings config, out SpeechRequest? request)
    {
        ArgumentNullException.ThrowIfNull(config);
        request = null;
        if (string.IsNullOrEmpty(json) || json.Length > MaxRequest)
        {
            return new("The request is empty or too large.", "bad-request");
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return new("The request isn't valid JSON.", "bad-request");
        }

        if (root is not JsonObject o)
        {
            return new("The request isn't a JSON object.", "bad-request");
        }

        int? version = null;
        double? start = null, length = null;
        string? caller = null, purpose = null, path = null, language = null;
        try
        {
            version = (int?)o["version"];
            caller = (string?)o["caller"];
            purpose = (string?)o["purpose"];
            path = (string?)o["path"];
            start = (double?)o["start"];
            length = (double?)o["length"];
            language = (string?)o["language"];
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return new("A field has the wrong type.", "bad-request");
        }

        if (version != Version)
        {
            return new("Only version 1 requests are understood.", "bad-request");
        }

        if (!config.Enabled)
        {
            return new("The Subtitles plugin is turned off.", "not-allowed");
        }

        var allowed = caller switch
        {
            "ingest" => config.AllowIngest,
            _ => false,
        };
        if (!allowed)
        {
            return new("The Subtitles plugin's settings don't allow this plugin to ask for transcripts.", "not-allowed");
        }

        if (purpose is null || !purpose.StartsWith(caller + ".", StringComparison.Ordinal) || purpose.Length > 64)
        {
            return new("The request's purpose must start with \"" + caller + ".\".", "not-allowed");
        }

        if (!config.AiContextOn)
        {
            return new("Speech-to-text for \"Context for AI decisions\" is off in the Subtitles plugin's settings.", "not-set-up");
        }

        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path))
        {
            return new("The video wasn't found.", "bad-request");
        }

        if (start is not { } from || !double.IsFinite(from) || from < 0 || from > TimeSpan.FromDays(1).TotalSeconds
            || length is not { } span || !double.IsFinite(span) || span <= 0 || span > MaxSeconds)
        {
            return new("The stretch must start at 0 or later and be at most " + MaxSeconds + " seconds long.", "bad-request");
        }

        if (language is not null && !LanguageCode().IsMatch(language))
        {
            return new("The language must be a two- or three-letter code.", "bad-request");
        }

        request = new SpeechRequest(caller!, purpose, path, TimeSpan.FromSeconds(from), TimeSpan.FromSeconds(span), language);
        return null;
    }

    /// <summary>
    /// The words heard, as one line of text.
    /// </summary>
    /// <param name="transcript">The transcript.</param>
    /// <returns>The text.</returns>
    internal static string Text(Transcript transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        return Spaces().Replace(string.Join(' ', transcript.Words.Select(w => w.Text.Trim())), " ").Trim();
    }

    /// <summary>
    /// The reply's name for a failure class.
    /// </summary>
    /// <param name="failure">The class.</param>
    /// <returns>The name.</returns>
    internal static string Name(FailureClass failure) => failure switch
    {
        FailureClass.Authentication => "authentication",
        FailureClass.ProviderLimit => "provider-limit",
        FailureClass.BadRequest => "bad-request",
        FailureClass.NoConnection => "no-connection",
        _ => "transient",
    };

    /// <summary>
    /// A reply that says why nothing was transcribed.
    /// </summary>
    /// <param name="ok">Always <c>false</c>.</param>
    /// <param name="error">Why, in words safe to show.</param>
    /// <param name="failure">The failure name.</param>
    /// <returns>The reply, as JSON.</returns>
    internal static string Reply(bool ok, string error, string failure)
        => JsonSerializer.Serialize(new { version = Version, ok, error, failure }, Options);

    /// <summary>
    /// A reply with what was heard.
    /// </summary>
    /// <param name="text">The words heard.</param>
    /// <param name="language">The language.</param>
    /// <param name="provider">The service that heard them.</param>
    /// <returns>The reply, as JSON.</returns>
    internal static string Heard(string text, string? language, string provider)
        => JsonSerializer.Serialize(new { version = Version, ok = true, text, language, provider }, Options);

    [GeneratedRegex("^[a-z]{2,3}$")]
    private static partial Regex LanguageCode();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>
    /// The settings that decide whether a request is answered.
    /// </summary>
    /// <param name="Enabled">Whether the plugin is on.</param>
    /// <param name="AllowIngest">Whether Ingest may ask.</param>
    /// <param name="AiContextOn">Whether "Context for AI decisions" speech-to-text is on.</param>
    internal sealed record Settings(bool Enabled, bool AllowIngest, bool AiContextOn);

    /// <summary>
    /// Why a request can't be answered.
    /// </summary>
    /// <param name="Message">In words safe to show.</param>
    /// <param name="Failure">The failure name.</param>
    internal sealed record Problem(string Message, string Failure);

    /// <summary>
    /// A checked request.
    /// </summary>
    /// <param name="Caller">The calling plugin.</param>
    /// <param name="Purpose">What for.</param>
    /// <param name="Path">The video.</param>
    /// <param name="Start">Where to start.</param>
    /// <param name="Length">How long.</param>
    /// <param name="Language">The expected language, if known.</param>
    internal sealed record SpeechRequest(string Caller, string Purpose, string Path, TimeSpan Start, TimeSpan Length, string? Language);
}

/// <summary>
/// The work behind <see cref="SpeechBridge.TranscribeAsync"/>, as a service the plugin registers: it checks the request,
/// starts a run (see <see cref="RunStart"/>) and transcribes with the service set for "Context for AI decisions". One
/// transcription runs at a time.
/// </summary>
internal sealed class SpeechBridgeService : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SpeechToTextKeys _keys;
    private readonly BuiltInHost _builtIn;
    private readonly Spending _spending;
    private readonly IMediaEncoder _encoder;
    private readonly IHttpClientFactory _http;

    /// <summary>
    /// Initializes a new instance of the <see cref="SpeechBridgeService"/> class.
    /// </summary>
    /// <param name="keys">Speech-to-text keys.</param>
    /// <param name="builtIn">The built-in speech-to-text.</param>
    /// <param name="spending">Prices, ledger and rates.</param>
    /// <param name="encoder">Jellyfin's media encoder (for ffmpeg).</param>
    /// <param name="http">HTTP clients.</param>
    public SpeechBridgeService(SpeechToTextKeys keys, BuiltInHost builtIn, Spending spending, IMediaEncoder encoder, IHttpClientFactory http)
    {
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _builtIn = builtIn ?? throw new ArgumentNullException(nameof(builtIn));
        _spending = spending ?? throw new ArgumentNullException(nameof(spending));
        _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>
    /// Transcribes a stretch of a video for another plugin.
    /// </summary>
    /// <param name="requestJson">The request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reply, as JSON. Never throws for a bad request or a failed transcription.</returns>
    public async Task<string> TranscribeAsync(string requestJson, CancellationToken cancellationToken)
    {
        if (SubtitlesPlugin.Instance?.Configuration is not { } config)
        {
            return SpeechBridge.Reply(false, "The Subtitles plugin isn't ready yet.", "transient");
        }

        if (SpeechBridge.Parse(requestJson, new SpeechBridge.Settings(config.Enabled, config.AllowIngest, config.AiContext is { Enabled: true }), out var request) is { } problem)
        {
            return SpeechBridge.Reply(false, problem.Message, problem.Failure);
        }

        if (Audio.FfmpegLocator.Resolve(_encoder.EncoderPath) is null)
        {
            return SpeechBridge.Reply(false, "Jellyfin's ffmpeg wasn't found.", "not-set-up");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var run = await RunStart.BeginAsync(_encoder, _http, _keys, _builtIn, _spending, cancellationToken).ConfigureAwait(false);
            if (run is null)
            {
                return SpeechBridge.Reply(false, "Jellyfin's ffmpeg wasn't found.", "not-set-up");
            }

            var speech = run.Speech(config, config.AiContext, request!.Purpose, out var why);
            if (speech is null)
            {
                return SpeechBridge.Reply(false, why ?? "No speech-to-text service can be used for AI decisions.", "not-set-up");
            }

            var samples = await new FfmpegAudioSource(run.Ffmpeg, request.Path).ReadAsync(request.Start, request.Length, cancellationToken).ConfigureAwait(false);
            if (samples.Length < AudioFormat.SampleRate)
            {
                return SpeechBridge.Heard(string.Empty, request.Language, speech.Id);
            }

            var transcript = await speech.TranscribeAsync(samples, request.Language, cancellationToken).ConfigureAwait(false);
            return SpeechBridge.Heard(SpeechBridge.Text(transcript), transcript.Language ?? request.Language, speech.Id);
        }
        catch (SpeechToTextException ex)
        {
            return SpeechBridge.Reply(false, ex.Message, SpeechBridge.Name(ex.Failure));
        }
#pragma warning disable CA1031 // Reading the audio can fail many ways (unreadable file, no audio track, ffmpeg error); the caller gets a reason.
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
        {
            return SpeechBridge.Reply(false, "The audio couldn't be read (" + ex.GetType().Name + ").", "transient");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();
}

/// <summary>
/// Connects <see cref="SpeechBridge"/> to the plugin's service when the server starts.
/// </summary>
internal sealed class SpeechBridgeHost : Microsoft.Extensions.Hosting.IHostedService
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SpeechBridgeHost"/> class.
    /// </summary>
    /// <param name="service">The entry point's service.</param>
    public SpeechBridgeHost(SpeechBridgeService service) => SpeechBridge.Attach(service);

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
