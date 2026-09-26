using System.Collections.Generic;
using Jellyfin.Plugin.Common.Secrets;

namespace Jellyfin.Plugin.Subtitles.SpeechToText;

/// <summary>
/// API keys for speech-to-text services, kept in an owner-only file separate from the plugin configuration (the shared
/// <c>KeyFile</c> from jellyfin-plugin-common): the settings page can only ask whether a key is set, replace it or clear it.
/// </summary>
public sealed class SpeechToTextKeys
{
    /// <summary>The services a key can be stored for (a local service may need one; most don't), and an optional separate
    /// Deepgram billing key used only to read the credit balance, and the SubDL subtitle site's key.</summary>
    public static readonly IReadOnlyList<string> Providers = [SpeechToTextFactory.Local, SpeechToTextFactory.Deepgram, SpeechToTextFactory.OpenAi, DeepgramAccount.BillingKey, "subdl"];

    private readonly KeyFile _file;

    /// <summary>
    /// Initializes a new instance of the <see cref="SpeechToTextKeys"/> class.
    /// </summary>
    /// <param name="path">Absolute path of the key file.</param>
    public SpeechToTextKeys(string path)
    {
        _file = new KeyFile(path, Providers);
    }

    /// <summary>
    /// Whether a key looks usable.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns><c>true</c> if acceptable.</returns>
    public static bool IsWellFormed(string? key) => KeyFile.IsWellFormed(key);

    /// <summary>
    /// Which services have a key.
    /// </summary>
    /// <returns>Service id → whether a key is set.</returns>
    public IReadOnlyDictionary<string, bool> Status() => _file.Status();

    /// <summary>
    /// Stores (or replaces) a service's key.
    /// </summary>
    /// <param name="provider">Service id.</param>
    /// <param name="key">The key.</param>
    public void Set(string provider, string key) => _file.Set(provider, key);

    /// <summary>
    /// Removes a service's key.
    /// </summary>
    /// <param name="provider">Service id.</param>
    public void Clear(string provider) => _file.Clear(provider);

    /// <summary>
    /// Gets a service's key, for making a call. Never pass it to a log, an alert or a response.
    /// </summary>
    /// <param name="provider">Service id.</param>
    /// <returns>The key, or <c>null</c>.</returns>
    internal string? Get(string provider) => _file.Get(provider);
}
