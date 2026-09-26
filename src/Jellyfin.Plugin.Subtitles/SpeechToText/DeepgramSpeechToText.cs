using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Subtitles.SpeechToText;

/// <summary>
/// Speech-to-text through Deepgram's pre-recorded audio API, which returns word timings and confidences.
/// </summary>
public sealed class DeepgramSpeechToText : HttpSpeechToText
{
    /// <summary>The API address.</summary>
    public static readonly Uri Address = new("https://api.deepgram.com/v1/listen");

    /// <summary>The model used when none is named.</summary>
    public const string DefaultModel = "nova-3";

    private readonly string _model;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeepgramSpeechToText"/> class.
    /// </summary>
    /// <param name="http">HTTP client.</param>
    /// <param name="key">API key.</param>
    /// <param name="model">Model name; empty for <see cref="DefaultModel"/>.</param>
    public DeepgramSpeechToText(HttpClient http, string key, string model)
        : base(http, key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
    }

    /// <inheritdoc />
    public override string Id => "deepgram";

    /// <summary>
    /// The request address for a model and language.
    /// </summary>
    /// <param name="model">Model.</param>
    /// <param name="language">Two-letter language, or <c>null</c> to detect.</param>
    /// <returns>The address.</returns>
    public static Uri RequestAddress(string model, string? language)
    {
        var query = "?model=" + Uri.EscapeDataString(model) + "&punctuate=false&smart_format=false"
            + (string.IsNullOrEmpty(language) ? "&detect_language=true" : "&language=" + Uri.EscapeDataString(language));
        return new Uri(Address.AbsoluteUri + query);
    }

    /// <inheritdoc />
    public override async Task<Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(samples);
        using var content = new ByteArrayContent(WavEncoder.Encode(samples));
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        using var request = new HttpRequestMessage(HttpMethod.Post, RequestAddress(_model, language)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", Key);

        using var reply = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var words = Array.Empty<TranscribedWord>() as System.Collections.Generic.IReadOnlyList<TranscribedWord>;
        string? detected = language;
        if (reply.RootElement.TryGetProperty("results", out var results)
            && results.TryGetProperty("channels", out var channels) && channels.ValueKind == JsonValueKind.Array && channels.GetArrayLength() > 0)
        {
            var channel = channels[0];
            if (channel.TryGetProperty("detected_language", out var dl) && dl.ValueKind == JsonValueKind.String)
            {
                detected = dl.GetString();
            }

            if (channel.TryGetProperty("alternatives", out var alternatives) && alternatives.ValueKind == JsonValueKind.Array && alternatives.GetArrayLength() > 0
                && alternatives[0].TryGetProperty("words", out var w))
            {
                words = ReadWords(w, "word", "confidence");
            }
        }

        return new Transcript(words, Languages.ToTwoLetter(detected) ?? language, Id, _model, samples.Length / (double)Audio.AudioFormat.SampleRate);
    }
}
