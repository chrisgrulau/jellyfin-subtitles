using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Resilience;
using Jellyfin.Plugin.Common.Secrets;

namespace Jellyfin.Plugin.Subtitles.SpeechToText;

/// <summary>
/// What the HTTP providers share: sending a request, turning failures into <see cref="SpeechToTextException"/> (with the
/// key removed from anything the provider echoed), and reading a size-limited JSON reply.
/// </summary>
public abstract class HttpSpeechToText : ISpeechToText
{
    /// <summary>The largest reply accepted.</summary>
    public const int MaxReplyBytes = 5 * 1024 * 1024;

    /// <summary>The most words accepted in one reply (far more than a few minutes of speech).</summary>
    public const int MaxWords = 20000;

    private readonly HttpClient _http;
    private readonly string? _key;

    /// <summary>
    /// Initializes a new instance of the <see cref="HttpSpeechToText"/> class.
    /// </summary>
    /// <param name="http">HTTP client.</param>
    /// <param name="key">API key, if the service needs one.</param>
    protected HttpSpeechToText(HttpClient http, string? key)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _key = key;
    }

    /// <inheritdoc />
    public abstract string Id { get; }

    /// <summary>Gets the API key, if any.</summary>
    protected string? Key => _key;

    /// <inheritdoc />
    public abstract Task<Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken cancellationToken);

    /// <summary>
    /// Sends a request and returns the parsed JSON reply, or throws <see cref="SpeechToTextException"/>.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reply.</returns>
    protected async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new SpeechToTextException(Id + ": " + Redaction.Redact(ex.Message, [_key]), ex) { Failure = HttpFailure.Classify(ex, cancellationToken) };
        }

        using (response)
        {
            var body = await ReadLimitedAsync(response, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                throw new SpeechToTextException(string.Create(CultureInfo.InvariantCulture, $"{Id}: HTTP {status}: {Redaction.Redact(body, [_key])}"))
                {
                    Failure = HttpFailure.Classify(response.StatusCode, body),
                };
            }

            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new SpeechToTextException(Id + ": the reply wasn't valid JSON.", ex) { Failure = FailureClass.Transient };
            }
        }
    }

    /// <summary>
    /// Reads a word list, checking every value.
    /// </summary>
    /// <param name="words">The JSON array of words.</param>
    /// <param name="text">Property holding the word.</param>
    /// <param name="confidence">Property holding the confidence.</param>
    /// <returns>The words, in time order.</returns>
    protected static IReadOnlyList<TranscribedWord> ReadWords(JsonElement words, string text, string confidence)
    {
        var result = new List<TranscribedWord>();
        if (words.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var w in words.EnumerateArray())
        {
            if (result.Count >= MaxWords)
            {
                break;
            }

            if (w.ValueKind == JsonValueKind.Object
                && w.TryGetProperty(text, out var t) && t.ValueKind == JsonValueKind.String
                && w.TryGetProperty("start", out var s) && s.TryGetDouble(out var start)
                && w.TryGetProperty("end", out var e) && e.TryGetDouble(out var end)
                && double.IsFinite(start) && double.IsFinite(end) && start >= 0 && end >= start)
            {
                double? c = w.TryGetProperty(confidence, out var cv) && cv.TryGetDouble(out var conf) && conf is >= 0 and <= 1 ? conf : null;
                var word = t.GetString()!.Trim();
                if (word.Length > 0)
                {
                    result.Add(new TranscribedWord(word, start, end, c));
                }
            }
        }

        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int n;
        while ((n = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + n > MaxReplyBytes)
            {
                throw new SpeechToTextException("The speech-to-text reply was too large.") { Failure = FailureClass.BadRequest };
            }

            await buffer.WriteAsync(chunk.AsMemory(0, n), ct).ConfigureAwait(false);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
