namespace Jellyfin.Plugin.Subtitles.Configuration;

/// <summary>
/// Which key reads a provider's credit balance.
/// </summary>
public enum BalanceSource
{
    /// <summary>The balance isn't shown.</summary>
    Off = 0,

    /// <summary>A separate key kept only for reading the balance (recommended; the transcription key stays limited).</summary>
    SeparateKey,

    /// <summary>The transcription key itself (it must be an Admin or Owner key, which can also manage keys).</summary>
    TranscriptionKey,
}
