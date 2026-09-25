using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Resilience;

namespace Jellyfin.Plugin.Subtitles.SpeechToText;

/// <summary>
/// Speech-to-text through the OpenAI transcription API: OpenAI itself, or any local service that speaks it
/// (faster-whisper servers, speaches, the whisper.cpp server …). Asks for word timings (<c>verbose_json</c>).
/// </summary>
public sealed class OpenAiCompatibleSpeechToText : HttpSpeechToText
{
    /// <summary>OpenAI's own API address.</summary>
    public static readonly Uri OpenAiAddress = new("https://api.openai.com/v1/");

    /// <summary>The model used with OpenAI when none is named (the one that gives word timings).</summary>
    public const string OpenAiDefaultModel = "whisper-1";

    private readonly Uri _address;
    private readonly string _model;
    private readonly string _id;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenAiCompatibleSpeechToText"/> class.
    /// </summary>
    /// <param name="http">HTTP client.</param>
    /// <param name="id">Provider id (<c>local</c> or <c>openai</c>).</param>
    /// <param name="address">The API base address, ending in <c>/v1/</c> (e.g. <c>http://127.0.0.1:8000/v1/</c>).</param>
    /// <param name="key">API key, if the service needs one.</param>
    /// <param name="model">Model name; empty for the service's default.</param>
    /// <param name="clock">Clock.</param>
    public OpenAiCompatibleSpeechToText(HttpClient http, string id, Uri address, string? key, string model, TimeProvider? clock = null)
        : base(http, key, clock)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        _address = CheckAddress(address, key);
        _id = id;
        _model = model ?? string.Empty;
    }

    /// <inheritdoc />
    public override string Id => _id;

    /// <summary>
    /// Checks a service address: absolute http(s), and a key is never sent over plain http except to this machine or the
    /// local network.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <param name="key">The key that would be sent.</param>
    /// <returns>The address, ending in a slash.</returns>
    /// <exception cref="SpeechToTextException">The address isn't acceptable.</exception>
    public static Uri CheckAddress(Uri address, string? key)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!address.IsAbsoluteUri || (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps) || !string.IsNullOrEmpty(address.UserInfo))
        {
            throw new SpeechToTextException("The service address must be an http:// or https:// address.") { Failure = FailureClass.BadRequest };
        }

        if (!string.IsNullOrEmpty(key) && address.Scheme == Uri.UriSchemeHttp && !IsLocal(address))
        {
            throw new SpeechToTextException("An API key is only sent over https, or to this machine or the local network.") { Failure = FailureClass.BadRequest };
        }

        return address.AbsoluteUri.EndsWith('/') ? address : new Uri(address.AbsoluteUri + "/");
    }

    /// <summary>
    /// Whether an address is this machine or the local network.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <returns><c>true</c> for loopback, private and link-local addresses and <c>.local</c> names.</returns>
    public static bool IsLocal(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsLoopback || address.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(address.Host.Trim('[', ']'), out var ip))
        {
            return false;
        }

        if (ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal)
        {
            return true;
        }

        var b = ip.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254));
    }

    /// <inheritdoc />
    public override async Task<Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var model = _model.Length > 0 ? _model : _id == "openai" ? OpenAiDefaultModel : string.Empty;
        using var form = new MultipartFormDataContent();
        using var audio = new ByteArrayContent(WavEncoder.Encode(samples));
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "file", "audio.wav");
        using var modelPart = new StringContent(model);
        if (model.Length > 0)
        {
            form.Add(modelPart, "model");
        }

        using var languagePart = new StringContent(language ?? string.Empty);
        if (!string.IsNullOrEmpty(language))
        {
            form.Add(languagePart, "language");
        }

        using var formatPart = new StringContent("verbose_json");
        using var granularityPart = new StringContent("word");
        form.Add(formatPart, "response_format");
        form.Add(granularityPart, "timestamp_granularities[]");

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_address, "audio/transcriptions")) { Content = form };
        if (!string.IsNullOrEmpty(Key))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        }

        using var reply = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var root = reply.RootElement;
        var words = root.TryGetProperty("words", out var w) ? ReadWords(w, "word", "probability") : [];
        var detected = root.TryGetProperty("language", out var l) && l.ValueKind == System.Text.Json.JsonValueKind.String ? l.GetString() : language;
        return new Transcript(words, Languages.ToTwoLetter(detected) ?? language, _id, model.Length > 0 ? model : "default", samples.Length / (double)Audio.AudioFormat.SampleRate);
    }
}
