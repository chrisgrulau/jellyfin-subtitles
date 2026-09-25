namespace Jellyfin.Plugin.Subtitles.Configuration;

/// <summary>
/// One use of speech-to-text, each switched on or off and given its own provider and model, so for example sync
/// snippets can use a local model while full transcripts use a cloud service.
/// </summary>
public class TranscriptionTier
{
    /// <summary>Provider id of the built-in, on-demand Whisper (no setup, CPU).</summary>
    public const string BuiltIn = "builtin";

    /// <summary>
    /// Gets or sets a value indicating whether this use of speech-to-text is allowed at all.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the speech-to-text provider id (<c>builtin</c>, <c>local</c>, <c>deepgram</c>, <c>openai</c> …).
    /// </summary>
    public string Provider { get; set; } = BuiltIn;

    /// <summary>
    /// Gets or sets the model, or empty for the provider's current recommended model.
    /// </summary>
    public string Model { get; set; } = string.Empty;
}
