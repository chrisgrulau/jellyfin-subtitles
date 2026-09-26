using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.Configuration;
using Jellyfin.Plugin.Subtitles.Pipeline;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Jellyfin.Plugin.Subtitles.SpeechToText.BuiltIn;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Subtitles.Api;

/// <summary>
/// Settings-page endpoints, for administrators only: speech-to-text keys (write-only: never returned) and a connection
/// test.
/// </summary>
[ApiController]
[Route("Subtitles")]
[Authorize(Policy = "RequiresElevation")]
[Produces(MediaTypeNames.Application.Json)]
public class SubtitlesController : ControllerBase
{
    /// <summary>The longest audio clip the editor may ask for, in seconds.</summary>
    public const int MaxClipSeconds = 30;

    private readonly SpeechToTextKeys _keys;
    private readonly BuiltInHost _builtIn;
    private readonly IServerConfigurationManager _serverConfig;
    private readonly Pricing.Spending _spending;
    private readonly IHttpClientFactory _http;
    private readonly SubtitleProcessor _processor;
    private readonly ILibraryManager _library;
    private readonly IMediaSourceManager _media;
    private readonly IMediaEncoder _encoder;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitlesController"/> class.
    /// </summary>
    /// <param name="keys">The key store.</param>
    /// <param name="http">HTTP client factory.</param>
    /// <param name="processor">Subtitle checks, results, apply and undo.</param>
    /// <param name="builtIn">The built-in speech-to-text.</param>
    /// <param name="spending">Prices, spend ledger and exchange rates.</param>
    /// <param name="serverConfig">Jellyfin's configuration (for its hardware acceleration setting).</param>
    /// <param name="library">Jellyfin's library (for the editor's audio clips).</param>
    /// <param name="media">Jellyfin's media sources (to choose the audio track).</param>
    /// <param name="encoder">Jellyfin's media encoder (for ffmpeg).</param>
    public SubtitlesController(SpeechToTextKeys keys, IHttpClientFactory http, SubtitleProcessor processor, BuiltInHost builtIn, Pricing.Spending spending, IServerConfigurationManager serverConfig, ILibraryManager library, IMediaSourceManager media, IMediaEncoder encoder)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _media = media ?? throw new ArgumentNullException(nameof(media));
        _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
        _serverConfig = serverConfig ?? throw new ArgumentNullException(nameof(serverConfig));
        _spending = spending ?? throw new ArgumentNullException(nameof(spending));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _builtIn = builtIn ?? throw new ArgumentNullException(nameof(builtIn));
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>
    /// Which services have a key (the keys themselves are never returned).
    /// </summary>
    /// <returns>Service id → whether a key is set.</returns>
    [HttpGet("Keys")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyDictionary<string, bool>> KeyStatus() => Ok(_keys.Status());

    /// <summary>
    /// Stores or replaces a service's key.
    /// </summary>
    /// <param name="provider">Service id.</param>
    /// <param name="request">The key.</param>
    /// <returns>No content.</returns>
    [HttpPut("Keys/{provider}")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult SetKey([FromRoute] string provider, [FromBody, Required] KeyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!((IList<string>)SpeechToTextKeys.Providers).Contains(provider))
        {
            return BadRequest("Unknown service.");
        }

        if (!SpeechToTextKeys.IsWellFormed(request.Key?.Trim()))
        {
            return BadRequest("That doesn't look like an API key.");
        }

        _keys.Set(provider, request.Key!);
        DeepgramAccount.ClearCache();
        return NoContent();
    }

    /// <summary>
    /// Removes a service's key.
    /// </summary>
    /// <param name="provider">Service id.</param>
    /// <returns>No content.</returns>
    [HttpDelete("Keys/{provider}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult ClearKey([FromRoute] string provider)
    {
        _keys.Clear(provider);
        return NoContent();
    }

    /// <summary>
    /// The latest results, newest first.
    /// </summary>
    /// <param name="limit">How many (default 200).</param>
    /// <returns>The results.</returns>
    [HttpGet("Results")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<SubtitleResult>> Results([FromQuery] int limit = 200) => Ok(_processor.Recent(limit));

    /// <summary>
    /// Applies a correction that is waiting for review.
    /// </summary>
    /// <param name="id">Result id.</param>
    /// <returns>The updated result.</returns>
    [HttpPost("Results/{id}/Apply")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<SubtitleResult> Apply([FromRoute] string id)
    {
        try
        {
            return _processor.Apply(id, (SubtitlesPlugin.Instance?.Configuration ?? new PluginConfiguration()).Policies());
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
    }

    /// <summary>
    /// Declines what waits for review for a subtitle: nothing is changed.
    /// </summary>
    /// <param name="id">Result id.</param>
    /// <returns>The updated result.</returns>
    [HttpPost("Results/{id}/Decline")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<SubtitleResult> Decline([FromRoute] string id)
    {
        try
        {
            return _processor.Decline(id);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
    }

    /// <summary>
    /// Forgets a result so the subtitle is checked again on the next run (or the video searched again).
    /// </summary>
    /// <param name="id">Result id.</param>
    /// <returns>No content.</returns>
    [HttpPost("Results/{id}/CheckAgain")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult CheckAgain([FromRoute] string id)
    {
        try
        {
            _processor.CheckAgain(id);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
    }

    /// <summary>
    /// Undoes this plugin's changes to a file.
    /// </summary>
    /// <param name="id">Result id.</param>
    /// <returns>The updated result.</returns>
    [HttpPost("Results/{id}/Undo")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<SubtitleResult> Undo([FromRoute] string id)
    {
        try
        {
            return _processor.Undo(id);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
    }

    /// <summary>
    /// Opens a subtitle for editing by hand.
    /// </summary>
    /// <param name="id">Result id.</param>
    /// <returns>The lines.</returns>
    [HttpGet("Editor/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<EditorView> OpenEditor([FromRoute] string id)
        => _processor.LoadForEditing(id) is { } view ? view : NotFound("This subtitle can't be opened (it may have been moved or deleted, or isn't a text subtitle).");

    /// <summary>
    /// Saves lines edited by hand (Undo brings the original back).
    /// </summary>
    /// <param name="id">Result id.</param>
    /// <param name="request">The fingerprint loaded and the lines.</param>
    /// <returns>The updated result.</returns>
    [HttpPut("Editor/{id}")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<SubtitleResult> SaveEditor([FromRoute] string id, [FromBody, Required] EditorSave request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return _processor.SaveEdited(id, request.Fingerprint ?? string.Empty, request.Cues ?? []);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
    }

    /// <summary>
    /// A short clip of the video's audio (the track the subtitle's language would use), to check a line by ear.
    /// </summary>
    /// <param name="id">Result id.</param>
    /// <param name="start">Where to start, in seconds.</param>
    /// <param name="length">How long, in seconds (at most 30).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The clip, as WAV (16 kHz mono).</returns>
    [HttpGet("Editor/{id}/Clip")]
    [Produces("audio/wav")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Clip([FromRoute] string id, [FromQuery] double start, [FromQuery] double length, CancellationToken cancellationToken)
    {
        if (!double.IsFinite(start) || !double.IsFinite(length) || start < 0 || start > SubtitleEditing.MaxTime || length <= 0 || length > MaxClipSeconds)
        {
            return BadRequest("A clip starts at 0 or later and lasts at most " + MaxClipSeconds + " seconds.");
        }

        var result = _processor.Get(id);
        if (result is null || _library.GetItemById(result.ItemId) is not MediaBrowser.Controller.Entities.Video video || string.IsNullOrEmpty(video.Path) || !System.IO.File.Exists(video.Path))
        {
            return NotFound("The video for this subtitle wasn't found.");
        }

        var ffmpeg = Audio.FfmpegLocator.Resolve(_encoder.EncoderPath);
        if (ffmpeg is null)
        {
            return NotFound("Jellyfin's ffmpeg wasn't found.");
        }

        var streams = _media.GetMediaStreams(video.Id);
        var audio = streams.Where(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Audio).OrderBy(s => s.Index).Select(s => ((string?)s.Language, s.IsDefault)).ToList();
        var language = streams.FirstOrDefault(s => s.IsExternal && string.Equals(s.Path, result.SubtitlePath, StringComparison.Ordinal))?.Language;
        var samples = await new Audio.FfmpegAudioSource(ffmpeg, video.Path, AudioChoice.For(audio, language)).ReadAsync(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(length), cancellationToken).ConfigureAwait(false);
        return File(WavEncoder.Encode(samples), "audio/wav");
    }

    /// <summary>
    /// Deepgram's credit balance (when the settings say which key may read it), and whether the transcription key is an
    /// Admin key that should be swapped for a limited one.
    /// </summary>
    /// <param name="check">Also check whether the transcription key is an Admin key (after it was saved).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The status.</returns>
    [HttpGet("Deepgram")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<DeepgramStatus>> DeepgramStatusOf([FromQuery] bool check, CancellationToken cancellationToken)
    {
        var config = SubtitlesPlugin.Instance?.Configuration ?? new PluginConfiguration();
        using var http = _http.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(20);
        var transcription = _keys.Get(SpeechToTextFactory.Deepgram);
        var billing = _keys.Get(DeepgramAccount.BillingKey);
        bool? admin = null;
        string? problem = null;
        if (check && transcription is not null)
        {
            try
            {
                admin = await DeepgramAccount.CanReadBillingAsync(http, transcription, cancellationToken).ConfigureAwait(false);
            }
            catch (SpeechToTextException ex)
            {
                problem = ex.Message;
            }
        }

        var balanceKey = config.DeepgramBalance switch
        {
            BalanceSource.SeparateKey => billing,
            BalanceSource.TranscriptionKey => transcription,
            _ => null,
        };
        DeepgramBalance? balance = null;
        if (balanceKey is not null)
        {
            try
            {
                balance = await DeepgramAccount.BalanceAsync(http, balanceKey, TimeProvider.System, cancellationToken).ConfigureAwait(false);
            }
            catch (SpeechToTextException ex)
            {
                problem = ex.Message;
            }
        }
        else if (config.DeepgramBalance != BalanceSource.Off)
        {
            problem = config.DeepgramBalance == BalanceSource.SeparateKey ? "Add the billing key to show the balance." : "Add the Deepgram key to show the balance.";
        }

        return new DeepgramStatus(transcription is not null, billing is not null, config.DeepgramBalance, admin, balance, problem);
    }

    /// <summary>
    /// Replaces an Admin transcription key with a new key that can only transcribe, created with the Admin key. The Admin
    /// key is then either kept only for reading the balance, or forgotten (revoke it in Deepgram's console if unused).
    /// </summary>
    /// <param name="request">Whether to keep the Admin key for the balance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What was done.</returns>
    [HttpPost("Deepgram/LimitKey")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<LimitKeyResult>> LimitDeepgramKey([FromBody, Required] LimitKeyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_keys.Get(SpeechToTextFactory.Deepgram) is null)
        {
            return BadRequest("Add the Deepgram key first.");
        }

        var config = SubtitlesPlugin.Instance?.Configuration;
        var before = config?.DeepgramBalance ?? BalanceSource.Off;
        using var http = _http.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        try
        {
            var (swapped, message, after) = await DeepgramAccount.LimitTranscriptionKeyAsync(http, _keys, request.KeepForBalance, before, cancellationToken).ConfigureAwait(false);

            // The page applies the resulting setting too, so its next Save doesn't write the old one back
            if (swapped && config is not null && after != before)
            {
                config.DeepgramBalance = after;
                SubtitlesPlugin.Instance!.SaveConfiguration();
            }

            return new LimitKeyResult(swapped, message, after);
        }
        catch (SpeechToTextException ex)
        {
            return new LimitKeyResult(false, ex.Message, before);
        }
    }

    /// <summary>
    /// Helps set up a local speech-to-text service: the services answering on this machine's usual ports, and a suggested
    /// way to run one that suits Jellyfin's hardware acceleration setting. Nothing is installed or started.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What was found and the suggestion.</returns>
    [HttpGet("LocalServices")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<LocalServicesResult>> LocalServicesFound(CancellationToken cancellationToken)
    {
        var config = SubtitlesPlugin.Instance?.Configuration ?? new PluginConfiguration();
        using var http = _http.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(10);
        var inContainer = LocalServices.InContainer();
        var found = await LocalServices.FindAsync(http, config.LocalServiceUrl, inContainer, cancellationToken).ConfigureAwait(false);
        var accel = _serverConfig.GetEncodingOptions().HardwareAccelerationType.ToString();
        return new LocalServicesResult(found, accel, LocalServices.Suggest(accel, inContainer));
    }

    /// <summary>
    /// This month's spending on paid services, in the user's currency, with the limit and the exchange rates used.
    /// </summary>
    /// <returns>The spending.</returns>
    [HttpGet("Spending")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<SpendingSummary> Spending()
    {
        var config = SubtitlesPlugin.Instance?.Configuration ?? new PluginConfiguration();
        var limits = Pricing.Spending.LimitsOf(config);
        var rates = _spending.Rates.Current;
        var month = _spending.ThisMonth(limits);
        return new SpendingSummary(
            limits.Currency,
            limits.Overall,
            month.Total,
            month.PerProvider.ToDictionary(p => p.Key, p => decimal.Round(p.Value, 4), StringComparer.OrdinalIgnoreCase),
            rates?.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            rates is not null && rates.IsFresh(DateOnly.FromDateTime(DateTime.Now)),
            _spending.Prices?.Version,
            Pricing.Spending.Currencies);
    }

    /// <summary>
    /// Whether the built-in speech-to-text can run on this server, checked before anything is downloaded.
    /// </summary>
    /// <returns>The status.</returns>
    [HttpGet("BuiltIn")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<BuiltInStatus> BuiltInStatusOf()
        => new BuiltInStatus(
            _builtIn.Platform is not null && _builtIn.Problem is null,
            _builtIn.Platform is null
                ? "The built-in speech-to-text has no build for this server's system. Use a local speech-to-text service or a cloud service instead."
                : _builtIn.Problem);

    /// <summary>
    /// Where the built-in speech-to-text's download stands (SUB-25): <c>idle</c>, <c>downloading</c>, <c>verifying</c>,
    /// <c>installed</c> or <c>failed</c>, with bytes and percent while it runs. The settings page polls it.
    /// </summary>
    /// <param name="model">The model chosen on the page (empty for the default), to say whether it is installed.</param>
    /// <returns>The status.</returns>
    [HttpGet("BuiltIn/Download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<BuiltInInstallStatus> BuiltInDownloadStatus([FromQuery] string? model)
        => _builtIn.Status(BuiltInSource.ModelName(model) ?? "base");

    /// <summary>
    /// Starts downloading the built-in speech-to-text in the background (SUB-25) and returns at once; poll
    /// <see cref="BuiltInDownloadStatus"/> for progress. Pressing it while a download runs joins that download.
    /// </summary>
    /// <param name="request">The model and the consent box, as on the page (not yet saved).</param>
    /// <returns>The status.</returns>
    [HttpPost("BuiltIn/Download")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<BuiltInInstallStatus> StartBuiltInDownload([FromBody, Required] BuiltInDownloadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var config = SubtitlesPlugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!(request.AllowBuiltInDownload ?? config.AllowBuiltInDownload))
        {
            return BadRequest("Tick \"Allow the built-in speech-to-text to download and run\" first.");
        }

        if (_builtIn.Platform is null || _builtIn.Problem is not null)
        {
            return BadRequest(_builtIn.Problem ?? "The built-in speech-to-text has no build for this server's system.");
        }

        if (BuiltInSource.ModelName(request.Model) is not { } model)
        {
            return BadRequest("Unknown built-in model; choose base or small.");
        }

        if (!_builtIn.IsInstalled(model) || _builtIn.Installing)
        {
            _builtIn.StartInstall(model);
        }

        return Accepted(_builtIn.Status(model));
    }

    /// <summary>
    /// Checks that a speech-to-text service answers, by sending it one second of near-silence (for a paid service this
    /// costs a small fraction of a cent).
    /// </summary>
    /// <param name="request">The service, model and (for a local service) address to test, as on the settings page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether it worked, and a message to show.</returns>
    [HttpPost("SpeechToText/Test")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<TestResult>> Test([FromBody, Required] TestRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var config = SubtitlesPlugin.Instance?.Configuration ?? new PluginConfiguration();
        var paidAllowed = SpendingLimit.AllowsPaidUsage(SpendingLimit.Monthly(config.MonthlyBudget, config.NoSpendingLimit));
        using var http = _http.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(60);
        var (service, problem) = SpeechToTextFactory.Create(request.Provider ?? string.Empty, request.Model ?? string.Empty, request.LocalServiceUrl ?? config.LocalServiceUrl, paidAllowed, request.AllowBuiltInDownload ?? config.AllowBuiltInDownload, _keys, http, _builtIn);
        if (service is null)
        {
            return new TestResult(false, problem ?? "Can't be used.");
        }

        // The built-in download (90–200 MB) runs in the background, never inside this request (SUB-25): Test starts it
        // and says how far it is; the page shows progress and tests again once it's done
        if (service.Id == SpeechToTextFactory.BuiltIn && BuiltInSource.ModelName(request.Model) is { } builtInModel
            && (_builtIn.Installing || !_builtIn.IsInstalled(builtInModel)))
        {
            _builtIn.StartInstall(builtInModel);
            return new TestResult(false, BuiltInProgress.TestMessage(_builtIn.Status(builtInModel)), Downloading: true);
        }

        // A paid service's test is priced and counted like any other call (a fraction of a cent)
        if (SpeechToTextFactory.IsPaid(service.Id))
        {
            await _spending.CurrentRatesAsync(http, cancellationToken).ConfigureAwait(false);
            service = SpeechSelection.Metered(service, request.Model, config, _spending, "subtitles.test");
        }

        // One second of a very quiet tone: enough for the service to accept and answer
        var samples = new float[Audio.AudioFormat.SampleRate];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(0.0005 * Math.Sin(2 * Math.PI * 440 * i / Audio.AudioFormat.SampleRate));
        }

        var clock = Stopwatch.StartNew();
        try
        {
            var transcript = await service.TranscribeAsync(samples, "en", cancellationToken).ConfigureAwait(false);
            return new TestResult(true, string.Create(CultureInfo.InvariantCulture, $"Connected: {service.Id} answered in {clock.Elapsed.TotalSeconds:0.0} s (model {transcript.Model})."));
        }
        catch (SpeechToTextException ex)
        {
            return new TestResult(false, ex.Message);
        }
    }
}

/// <summary>
/// Body of <see cref="SubtitlesController.StartBuiltInDownload"/>.
/// </summary>
public sealed record BuiltInDownloadRequest
{
    /// <summary>Gets the model (empty for the default).</summary>
    public string? Model { get; init; }

    /// <summary>
    /// Gets whether the download is allowed, as currently ticked on the page (not yet saved); <c>null</c> for the saved
    /// setting.
    /// </summary>
    public bool? AllowBuiltInDownload { get; init; }
}

/// <summary>
/// Body of <see cref="SubtitlesController.SaveEditor"/>.
/// </summary>
public sealed record EditorSave
{
    /// <summary>Gets the fingerprint the editor loaded.</summary>
    public string? Fingerprint { get; init; }

    /// <summary>Gets the lines.</summary>
    public IReadOnlyList<EditorCue>? Cues { get; init; }
}

/// <summary>
/// Body of <see cref="SubtitlesController.SetKey"/>.
/// </summary>
public sealed record KeyRequest
{
    /// <summary>Gets the API key.</summary>
    public string? Key { get; init; }
}

/// <summary>
/// Body of <see cref="SubtitlesController.Test"/>.
/// </summary>
public sealed record TestRequest
{
    /// <summary>Gets the service id.</summary>
    public string? Provider { get; init; }

    /// <summary>Gets the model (empty for the default).</summary>
    public string? Model { get; init; }

    /// <summary>Gets the local service address as currently entered (not yet saved).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "As typed on the settings page; checked by the factory.")]
    public string? LocalServiceUrl { get; init; }

    /// <summary>
    /// Gets whether the built-in download is allowed, as currently ticked on the page (not yet saved); <c>null</c> for
    /// the saved setting.
    /// </summary>
    public bool? AllowBuiltInDownload { get; init; }
}

/// <summary>
/// Result of <see cref="SubtitlesController.Test"/>.
/// </summary>
/// <param name="Ok">Whether the service answered.</param>
/// <param name="Message">What to show (keys removed).</param>
/// <param name="Downloading">Whether the built-in speech-to-text is still downloading (in the background): the page
/// shows the progress and tests again when it's done.</param>
public sealed record TestResult(bool Ok, string Message, bool Downloading = false);

/// <summary>
/// Whether the built-in speech-to-text can run on this server.
/// </summary>
/// <param name="Available">Whether it can be downloaded and run.</param>
/// <param name="Problem">Why not, in plain language.</param>
public sealed record BuiltInStatus(bool Available, string? Problem);

/// <summary>
/// This month's spending on paid services.
/// </summary>
/// <param name="Currency">The user's currency.</param>
/// <param name="Limit">The monthly limit, or <c>null</c> for no limit.</param>
/// <param name="Spent">Spent so far this month (open reservations included), or <c>null</c> if it can't be converted.</param>
/// <param name="PerProvider">Spent per provider.</param>
/// <param name="RatesDate">The date of the exchange rates in use, if any.</param>
/// <param name="RatesFresh">Whether those rates are recent enough to use.</param>
/// <param name="PricesVersion">The version of the published prices shipped with the plugin.</param>
/// <param name="Currencies">The currencies that can be chosen (the settings page offers these, rather than its own copy).</param>
public sealed record SpendingSummary(string Currency, decimal? Limit, decimal? Spent, IReadOnlyDictionary<string, decimal> PerProvider, string? RatesDate, bool RatesFresh, string? PricesVersion, IReadOnlyList<string> Currencies);

/// <summary>
/// What the local-service helper found.
/// </summary>
/// <param name="Found">Services answering on this machine.</param>
/// <param name="HardwareAcceleration">Jellyfin's hardware acceleration setting.</param>
/// <param name="Suggestion">A suggested way to run one.</param>
public sealed record LocalServicesResult(IReadOnlyList<FoundService> Found, string HardwareAcceleration, SetupSuggestion Suggestion);

/// <summary>
/// Deepgram's key and balance status for the settings page (never the keys themselves).
/// </summary>
/// <param name="HasKey">Whether a transcription key is set.</param>
/// <param name="HasBillingKey">Whether a separate billing key is set.</param>
/// <param name="BalanceSource">Which key reads the balance.</param>
/// <param name="KeyIsAdmin">Whether the transcription key is an Admin or Owner key, when checked.</param>
/// <param name="Balance">The credit balance, if read.</param>
/// <param name="Problem">Why something couldn't be shown.</param>
public sealed record DeepgramStatus(bool HasKey, bool HasBillingKey, BalanceSource BalanceSource, bool? KeyIsAdmin, DeepgramBalance? Balance, string? Problem);

/// <summary>
/// Result of <see cref="SubtitlesController.LimitDeepgramKey"/>.
/// </summary>
/// <param name="Ok">Whether the key was swapped.</param>
/// <param name="Message">What to show.</param>
/// <param name="BalanceSource">Which key reads the balance now (the page applies it before its next Save).</param>
public sealed record LimitKeyResult(bool Ok, string Message, BalanceSource BalanceSource);

/// <summary>
/// Body of <see cref="SubtitlesController.LimitDeepgramKey"/>.
/// </summary>
public sealed record LimitKeyRequest
{
    /// <summary>Gets a value indicating whether to keep the Admin key, only for reading the balance.</summary>
    public bool KeepForBalance { get; init; }
}
