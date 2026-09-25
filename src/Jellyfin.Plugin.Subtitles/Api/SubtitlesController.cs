using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.Configuration;
using Jellyfin.Plugin.Subtitles.SpeechToText;
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
    private readonly SpeechToTextKeys _keys;
    private readonly IHttpClientFactory _http;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitlesController"/> class.
    /// </summary>
    /// <param name="keys">The key store.</param>
    /// <param name="http">HTTP client factory.</param>
    public SubtitlesController(SpeechToTextKeys keys, IHttpClientFactory http)
    {
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
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
        var (service, problem) = SpeechToTextFactory.Create(request.Provider ?? string.Empty, request.Model ?? string.Empty, request.LocalServiceUrl ?? config.LocalServiceUrl, paidAllowed, config.AllowBuiltInDownload, _keys, http);
        if (service is null)
        {
            return new TestResult(false, problem ?? "Can't be used.");
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
}

/// <summary>
/// Result of <see cref="SubtitlesController.Test"/>.
/// </summary>
/// <param name="Ok">Whether the service answered.</param>
/// <param name="Message">What to show (keys removed).</param>
public sealed record TestResult(bool Ok, string Message);
