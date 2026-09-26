using System;
using System.Net.Http;
using Jellyfin.Plugin.Subtitles.SpeechToText.BuiltIn;

namespace Jellyfin.Plugin.Subtitles.SpeechToText;

/// <summary>
/// Builds the speech-to-text service a setting names, or says in plain language why it can't be used.
/// </summary>
public static class SpeechToTextFactory
{
    /// <summary>The built-in Whisper (downloaded on first use, after consent).</summary>
    public const string BuiltIn = Configuration.TranscriptionTier.BuiltIn;

    /// <summary>A local OpenAI-compatible service (free).</summary>
    public const string Local = "local";

    /// <summary>Deepgram (paid).</summary>
    public const string Deepgram = "deepgram";

    /// <summary>OpenAI (paid).</summary>
    public const string OpenAi = "openai";

    /// <summary>
    /// Whether a service charges for use.
    /// </summary>
    /// <param name="provider">Service id.</param>
    /// <returns><c>true</c> for cloud services.</returns>
    public static bool IsPaid(string provider) => provider is Deepgram or OpenAi;

    /// <summary>
    /// Builds a service.
    /// </summary>
    /// <param name="provider">Service id.</param>
    /// <param name="model">Model name (empty for the service's default).</param>
    /// <param name="localAddress">The local service's address (for <see cref="Local"/>).</param>
    /// <param name="paidAllowed">Whether the spending limit allows paid services at all.</param>
    /// <param name="builtInAllowed">Whether the administrator has allowed the built-in download.</param>
    /// <param name="keys">The keys.</param>
    /// <param name="http">HTTP client.</param>
    /// <param name="builtIn">The built-in speech-to-text, if available.</param>
    /// <returns>The service, or why it can't be used.</returns>
    public static (ISpeechToText? Service, string? Problem) Create(string provider, string model, string localAddress, bool paidAllowed, bool builtInAllowed, SpeechToTextKeys keys, HttpClient http, BuiltInHost? builtIn)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(http);
        model ??= string.Empty;

        if (IsPaid(provider) && !paidAllowed)
        {
            return (null, "The monthly spending limit is 0, so paid services aren't used. Raise it to use this service.");
        }

        try
        {
            switch (provider)
            {
                case Local:
                    if (string.IsNullOrWhiteSpace(localAddress) || !Uri.TryCreate(localAddress.Trim(), UriKind.Absolute, out var address))
                    {
                        return (null, "Enter the local service's address, e.g. http://localhost:8000/v1.");
                    }

                    return (new OpenAiCompatibleSpeechToText(http, Local, address, keys.Get(Local), model), null);
                case OpenAi:
                    return keys.Get(OpenAi) is { } openAiKey
                        ? (new OpenAiCompatibleSpeechToText(http, OpenAi, OpenAiCompatibleSpeechToText.OpenAiAddress, openAiKey, model), null)
                        : (null, "Add an OpenAI API key first.");
                case Deepgram:
                    return keys.Get(Deepgram) is { } deepgramKey
                        ? (new DeepgramSpeechToText(http, deepgramKey, model), null)
                        : (null, "Add a Deepgram API key first.");
                case BuiltIn:
                    if (!builtInAllowed)
                    {
                        return (null, "The built-in speech-to-text needs your permission to download first (see above).");
                    }

                    if (builtIn?.Platform is null)
                    {
                        return (null, "The built-in speech-to-text has no build for this server's system. Choose a local service or a cloud provider.");
                    }

                    return BuiltInSource.ModelName(model) is { } builtInModel
                        ? (builtIn.Create(builtInModel), null)
                        : (null, "Unknown built-in model; choose base or small.");
                default:
                    return (null, "Unknown speech-to-text service.");
            }
        }
        catch (SpeechToTextException ex)
        {
            return (null, ex.Message);
        }
    }
}
