using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Resilience;

namespace Jellyfin.Plugin.Subtitles.SpeechToText;

/// <summary>
/// One recognised word.
/// </summary>
/// <param name="Text">The word as recognised (may carry punctuation).</param>
/// <param name="Start">When it starts, in seconds from the start of the audio sent.</param>
/// <param name="End">When it ends, in seconds.</param>
/// <param name="Confidence">The provider's confidence, 0 to 1, if it gives one.</param>
public sealed record TranscribedWord(string Text, double Start, double End, double? Confidence);

/// <summary>
/// What speech-to-text heard in a stretch of audio.
/// </summary>
/// <param name="Words">The words, in order, with times.</param>
/// <param name="Language">The language the provider detected or was told (two-letter code), if known.</param>
/// <param name="Provider">The provider id.</param>
/// <param name="Model">The model used.</param>
/// <param name="AudioSeconds">How much audio was sent (what paid providers charge for).</param>
public sealed record Transcript(IReadOnlyList<TranscribedWord> Words, string? Language, string Provider, string Model, double AudioSeconds);

/// <summary>
/// A speech-to-text service.
/// </summary>
public interface ISpeechToText
{
    /// <summary>Gets the provider id (<c>local</c>, <c>openai</c>, <c>deepgram</c>, <c>builtin</c>).</summary>
    string Id { get; }

    /// <summary>
    /// Transcribes audio with word timings.
    /// </summary>
    /// <param name="samples">16 kHz mono samples.</param>
    /// <param name="language">Two-letter language code, or <c>null</c> to let the service detect it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The transcript.</returns>
    /// <exception cref="SpeechToTextException">The service failed; its class says whether and when to retry.</exception>
    Task<Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken cancellationToken);
}

/// <summary>
/// A speech-to-text call failed. The message is safe to show and log (keys are removed).
/// </summary>
public sealed class SpeechToTextException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SpeechToTextException"/> class.
    /// </summary>
    public SpeechToTextException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SpeechToTextException"/> class.
    /// </summary>
    /// <param name="message">A safe message.</param>
    public SpeechToTextException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SpeechToTextException"/> class.
    /// </summary>
    /// <param name="message">A safe message.</param>
    /// <param name="innerException">The cause.</param>
    public SpeechToTextException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Gets what kind of failure it was (decides retries and alerts).</summary>
    internal FailureClass Failure { get; init; } = FailureClass.Transient;

    /// <summary>Gets how long the provider asked us to wait, if it said.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>Gets a value indicating whether the problem is the configuration (key, address) rather than the service.</summary>
    public bool NeedsAttention => Failure is FailureClass.Authentication or FailureClass.BadRequest;
}
