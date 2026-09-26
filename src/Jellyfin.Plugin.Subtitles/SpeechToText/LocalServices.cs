using System;
using System.Collections.Generic;
using System.IO;
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
/// the network; in a container also the suggested service's name and the host), and suggests how to run one that suits the server's hardware acceleration. The plugin never installs or
/// starts a service itself.
/// </summary>
public static class LocalServices
{
    /// <summary>The ports speech-to-text servers usually listen on (speaches, faster-whisper-server, LocalAI …).</summary>
    public static readonly IReadOnlyList<int> UsualPorts = [8000, 8080, 8880, 9000, 5000];

    /// <summary>The model suggested for a new service: accurate enough to align words, fast on a small GPU.</summary>
    public const string SuggestedModel = "Systran/faster-whisper-small";

    /// <summary>The Docker network suggested for sharing with a Jellyfin container.</summary>
    public const string SharedNetwork = "speech";

    private const int MaxReplyBytes = 256 * 1024;

    /// <summary>
    /// Whether Jellyfin runs in a container (Docker or Podman), where <c>localhost</c> is the container itself.
    /// </summary>
    /// <returns><c>true</c> in a container.</returns>
    public static bool InContainer() => IsContainer(File.Exists);

    /// <summary>
    /// Whether the files a container runtime leaves exist (<c>/.dockerenv</c>, <c>/run/.containerenv</c>).
    /// </summary>
    /// <param name="exists">Whether a file exists.</param>
    /// <returns><c>true</c> in a container.</returns>
    public static bool IsContainer(Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        return exists("/.dockerenv") || exists("/run/.containerenv");
    }

    /// <summary>
    /// The addresses looked at: the configured one (if on this machine) and the usual local ports.
    /// </summary>
    /// <param name="configured">The configured local service address.</param>
    /// <param name="inContainer">Whether Jellyfin runs in a container: then a service on a shared network or on the
    /// host is looked for too.</param>
    /// <returns>The addresses, each ending in <c>/v1</c>.</returns>
    public static IReadOnlyList<string> Candidates(string? configured, bool inContainer = false)
    {
        var list = new List<string>();
        if (Uri.TryCreate(configured?.Trim(), UriKind.Absolute, out var uri) && uri.IsLoopback && uri.Scheme is "http" or "https")
        {
            list.Add(uri.AbsoluteUri.TrimEnd('/'));
        }

        list.AddRange(UsualPorts.Select(p => $"http://localhost:{p}/v1"));
        if (inContainer)
        {
            list.Add("http://speaches:8000/v1");
            list.Add("http://host.docker.internal:8000/v1");
        }

        return [.. list.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Looks for OpenAI-compatible services at the candidate addresses (a short <c>GET …/models</c> each).
    /// </summary>
    /// <param name="http">HTTP client.</param>
    /// <param name="configured">The configured address.</param>
    /// <param name="inContainer">Whether Jellyfin runs in a container (see <see cref="Candidates"/>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The services that answered.</returns>
    public static async Task<IReadOnlyList<FoundService>> FindAsync(HttpClient http, string? configured, bool inContainer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        var probes = Candidates(configured, inContainer).Select(async address =>
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
    /// <param name="inContainer">Whether Jellyfin runs in a container: then the service joins a Docker network shared
    /// with Jellyfin and is reached by its name, because <c>localhost</c> inside Jellyfin's container is the container
    /// itself.</param>
    /// <returns>The suggestion.</returns>
    public static SetupSuggestion Suggest(string? hardwareAcceleration, bool inContainer = false)
    {
        var nvidia = string.Equals(hardwareAcceleration, "nvenc", StringComparison.OrdinalIgnoreCase);
        var image = nvidia ? "ghcr.io/speaches-ai/speaches:latest-cuda" : "ghcr.io/speaches-ai/speaches:latest-cpu";
        var why = nvidia
            ? "Jellyfin uses an NVIDIA GPU (NVENC), so this runs speaches on the GPU. It needs the NVIDIA Container Toolkit."
            : "Speech-to-text runs fastest on an NVIDIA GPU; Jellyfin isn't set to use one (" + (string.IsNullOrEmpty(hardwareAcceleration) ? "none" : hardwareAcceleration)
                + "), so this runs speaches on the CPU. Intel and AMD GPUs aren't supported by it; the Built-in option is simpler if a CPU service is all you need.";
        const string Volume = " --volume hf-hub-cache:/home/ubuntu/.cache/huggingface/hub";
        var gpu = nvidia ? " --gpus=all " : " ";
        if (inContainer)
        {
            return new SetupSuggestion(
                nvidia,
                why + " Jellyfin runs in a container, where localhost is Jellyfin's own container, so the service joins a Docker network shared with Jellyfin and is reached by its name."
                    + " Replace \"jellyfin\" with your Jellyfin container's name. (If the service runs directly on the host instead, publish its port on the host rather than only on 127.0.0.1,"
                    + " start Jellyfin's container with --add-host=host.docker.internal:host-gateway, and use http://host.docker.internal:8000/v1.)",
                [
                    "docker network create " + SharedNetwork,
                    "docker run --detach --restart unless-stopped --name speaches --network " + SharedNetwork + Volume + gpu + image,
                    "docker network connect " + SharedNetwork + " jellyfin",
                    "docker run --rm --network " + SharedNetwork + " curlimages/curl -X POST http://speaches:8000/v1/models/" + SuggestedModel,
                ],
                "http://speaches:8000/v1");
        }

        return new SetupSuggestion(
            nvidia,
            why,
            [
                "docker run --detach --restart unless-stopped --name speaches --publish 127.0.0.1:8000:8000" + Volume + gpu + image,
                "curl -X POST http://localhost:8000/v1/models/" + SuggestedModel,
            ],
            "http://localhost:8000/v1");
    }
}
