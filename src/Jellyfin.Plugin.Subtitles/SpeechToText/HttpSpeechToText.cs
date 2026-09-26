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
        string body;
        try
        {
            // The shared provider HTTP helper classifies failures, reads the provider's wait and caps every body
            body = await ProviderHttp.SendAsync(_http, request, MaxReplyBytes, [_key], cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderException ex)
        {
            throw new SpeechToTextException(ProviderWording.Said(ProviderWording.NameOf(Id), ex), ex) { Failure = ex.Failure, StatusCode = ex.StatusCode };
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

    /// <summary>
    /// Reads a word list, checking every value.
    /// </summary>
    /// <param name="words">The JSON array of words.</param>
    /// <param name="text">Property holding the word.</param>
    /// <param name="confidence">Property holding the confidence.</param>
    /// <param name="preferredText">Property holding the word as it should be shown (with punctuation), used where present.</param>
    /// <returns>The words, in time order.</returns>
    protected static IReadOnlyList<TranscribedWord> ReadWords(JsonElement words, string text, string confidence, string? preferredText = null)
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
                var word = (preferredText is not null && w.TryGetProperty(preferredText, out var pt) && pt.ValueKind == JsonValueKind.String ? pt : t).GetString()!.Trim();
                if (word.Length > 0)
                {
                    result.Add(new TranscribedWord(word, start, end, c));
                }
            }
        }

        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }

    /// <summary>
    /// Reads a segment list (text with start and end), checking every value.
    /// </summary>
    /// <param name="segments">The JSON array of segments.</param>
    /// <returns>The segments, in time order.</returns>
    protected static IReadOnlyList<TranscribedSegment> ReadSegments(JsonElement segments)
    {
        var result = new List<TranscribedSegment>();
        if (segments.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var s in segments.EnumerateArray())
        {
            if (result.Count >= MaxWords)
            {
                break;
            }

            if (s.ValueKind == JsonValueKind.Object
                && s.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                && s.TryGetProperty("start", out var st) && st.TryGetDouble(out var start)
                && s.TryGetProperty("end", out var e) && e.TryGetDouble(out var end)
                && double.IsFinite(start) && double.IsFinite(end) && start >= 0 && end >= start)
            {
                var text = t.GetString()!.Trim();
                if (text.Length > 0)
                {
                    result.Add(new TranscribedSegment(text, start, end));
                }
            }
        }

        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }
}
