using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Subtitles.SpeechToText;

/// <summary>
/// A speech-to-text service found on this machine.
/// </summary>
/// <param name="Address">Its OpenAI-compatible address (ending in <c>/v1</c>).</param>
/// <param name="Models">The models it lists.</param>
public sealed record FoundService(string Address, IReadOnlyList<string> Models);

/// <summary>
/// A suggested way to run a local speech-to-text service, matched to the server's hardware acceleration.
/// </summary>
/// <param name="Gpu">Whether the suggestion uses the GPU.</param>
/// <param name="Why">Why this suggestion, in plain words.</param>
/// <param name="Commands">Commands to run on the server, one per item.</param>
/// <param name="Address">The address to enter afterwards.</param>
public sealed record SetupSuggestion(bool Gpu, string Why, IReadOnlyList<string> Commands, string Address);

/// <summary>
/// Helps set up a local speech-to-text service: looks for one on this machine's usual ports (only this machine, never
/// the network), and suggests how to run one that suits the server's hardware acceleration. The plugin never installs or
/// starts a service itself.
/// </summary>
public static class LocalServices
{
    /// <summary>The ports speech-to-text servers usually listen on (speaches, faster-whisper-server, LocalAI …).</summary>
    public static readonly IReadOnlyList<int> UsualPorts = [8000, 8080, 8880, 9000, 5000];

    /// <summary>The model suggested for a new service: accurate enough to align words, fast on a small GPU.</summary>
    public const string SuggestedModel = "Systran/faster-whisper-small";

    private const int MaxReplyBytes = 256 * 1024;

    /// <summary>
    /// The addresses looked at: the configured one (if on this machine) and the usual local ports.
    /// </summary>
    /// <param name="configured">The configured local service address.</param>
    /// <returns>The addresses, each ending in <c>/v1</c>.</returns>
    public static IReadOnlyList<string> Candidates(string? configured)
    {
        var list = new List<string>();
        if (Uri.TryCreate(configured?.Trim(), UriKind.Absolute, out var uri) && uri.IsLoopback && uri.Scheme is "http" or "https")
        {
            list.Add(uri.AbsoluteUri.TrimEnd('/'));
        }

        list.AddRange(UsualPorts.Select(p => $"http://localhost:{p}/v1"));
        return [.. list.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Looks for OpenAI-compatible services at the candidate addresses (a short <c>GET …/models</c> each).
    /// </summary>
    /// <param name="http">HTTP client.</param>
    /// <param name="configured">The configured address.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The services that answered.</returns>
    public static async Task<IReadOnlyList<FoundService>> FindAsync(HttpClient http, string? configured, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        var probes = Candidates(configured).Select(async address =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                using var response = await http.GetAsync(new Uri(address + "/models"), HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxReplyBytes)
                {
                    return null;
                }

                var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                return body.Length > MaxReplyBytes ? null : ModelsOf(body) is { } models ? new FoundService(address, models) : null;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        });
        return [.. (await Task.WhenAll(probes).ConfigureAwait(false)).OfType<FoundService>()];
    }

    /// <summary>
    /// Reads an OpenAI-style model list (<c>{"data":[{"id":…}]}</c>).
    /// </summary>
    /// <param name="json">The reply.</param>
    /// <returns>The model ids, or <c>null</c> if it isn't such a list.</returns>
    public static IReadOnlyList<string>? ModelsOf(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            return [.. data.EnumerateArray()
                .Select(m => m.ValueKind == JsonValueKind.Object && m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null)
                .OfType<string>()
                .Where(id => id.Length is > 0 and <= 200)
                .Take(50)];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Suggests how to run a local service, from Jellyfin's hardware acceleration setting.
    /// </summary>
    /// <param name="hardwareAcceleration">Jellyfin's setting (<c>nvenc</c>, <c>qsv</c>, <c>vaapi</c>, <c>none</c> …).</param>
    /// <returns>The suggestion.</returns>
    public static SetupSuggestion Suggest(string? hardwareAcceleration)
    {
        var nvidia = string.Equals(hardwareAcceleration, "nvenc", StringComparison.OrdinalIgnoreCase);
        var image = nvidia ? "ghcr.io/speaches-ai/speaches:latest-cuda" : "ghcr.io/speaches-ai/speaches:latest-cpu";
        var why = nvidia
            ? "Jellyfin uses an NVIDIA GPU (NVENC), so this runs speaches on the GPU. It needs the NVIDIA Container Toolkit."
            : "Speech-to-text runs fastest on an NVIDIA GPU; Jellyfin isn't set to use one (" + (string.IsNullOrEmpty(hardwareAcceleration) ? "none" : hardwareAcceleration)
                + "), so this runs speaches on the CPU. Intel and AMD GPUs aren't supported by it; the Built-in option is simpler if a CPU service is all you need.";
        return new SetupSuggestion(
            nvidia,
            why,
            [
                "docker run --detach --restart unless-stopped --name speaches --publish 127.0.0.1:8000:8000 --volume hf-hub-cache:/home/ubuntu/.cache/huggingface/hub" + (nvidia ? " --gpus=all " : " ") + image,
                "curl -X POST http://localhost:8000/v1/models/" + SuggestedModel,
            ],
            "http://localhost:8000/v1");
    }
}
