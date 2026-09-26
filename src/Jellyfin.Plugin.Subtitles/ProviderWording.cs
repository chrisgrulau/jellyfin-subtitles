using System.Globalization;
using Jellyfin.Plugin.Common.Resilience;

namespace Jellyfin.Plugin.Subtitles;

/// <summary>
/// Words a provider's failure for people, naming the provider and quoting its own error reply
/// (<see cref="ProviderException.Detail"/>) rather than the host-prefixed message the shared HTTP helper writes.
/// </summary>
internal static class ProviderWording
{
    /// <summary>
    /// The provider's name for people, from a speech-to-text service id (other names pass through).
    /// </summary>
    /// <param name="id">The service id or provider name.</param>
    /// <returns>The name to show.</returns>
    public static string NameOf(string id) => id switch
    {
        "deepgram" => "Deepgram",
        "openai" => "OpenAI",
        "local" => "The local service",
        _ => id,
    };

    /// <summary>
    /// "SubDL said: …" when the provider sent an error reply, "SubDL answered HTTP 503." when it sent none, and the
    /// failure's own message when it never answered.
    /// </summary>
    /// <param name="provider">The provider's name for people.</param>
    /// <param name="ex">The classified failure.</param>
    /// <returns>The message.</returns>
    public static string Said(string provider, ProviderException ex)
    {
        System.ArgumentNullException.ThrowIfNull(ex);
        if (!string.IsNullOrWhiteSpace(ex.Detail))
        {
            return provider + " said: " + ex.Detail;
        }

        return ex.StatusCode is { } status
            ? string.Create(CultureInfo.InvariantCulture, $"{provider} answered HTTP {(int)status}.")
            : ex.Message;
    }
}
