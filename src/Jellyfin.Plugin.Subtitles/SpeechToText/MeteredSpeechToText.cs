using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Costs;
using Jellyfin.Plugin.Common.Resilience;
using Jellyfin.Plugin.Subtitles.Pricing;

namespace Jellyfin.Plugin.Subtitles.SpeechToText;

/// <summary>
/// A paid speech-to-text service kept within the spending limits, through the shared <see cref="MeteredCall"/>: each
/// call's cost (audio length × the published price) is reserved before it is made and settled afterwards, or released if
/// the call failed. A call whose price or cost in the user's currency is unknown, or that would go over a limit, isn't
/// made.
/// </summary>
internal sealed class MeteredSpeechToText : ISpeechToText
{
    // Failed calls aren't charged by these providers: a speech-to-text failure or a cancellation releases the reservation;
    // anything unexpected is recorded at the estimate, since it may have been billed
    private static readonly MeteredCallOptions Options = new()
    {
        IsCharged = static _ => false,
        IsUncharged = static ex => ex is SpeechToTextException or OperationCanceledException,
        ChargedCost = static _ => null,
        Refuse = Refuse,
    };

    private readonly ISpeechToText _inner;
    private readonly Spending _spending;
    private readonly SpendLimits _limits;
    private readonly string _model;
    private readonly string _purpose;

    /// <summary>
    /// Initializes a new instance of the <see cref="MeteredSpeechToText"/> class.
    /// </summary>
    /// <param name="inner">The paid service.</param>
    /// <param name="model">The model it uses (for the price).</param>
    /// <param name="spending">Prices, ledger and rates.</param>
    /// <param name="limits">The spending limits.</param>
    /// <param name="purpose">What the calls are for (<c>subtitles.sync</c> …).</param>
    public MeteredSpeechToText(ISpeechToText inner, string model, Spending spending, SpendLimits limits, string purpose)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _spending = spending ?? throw new ArgumentNullException(nameof(spending));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _model = model ?? string.Empty;
        _purpose = purpose ?? string.Empty;
    }

    /// <inheritdoc />
    public string Id => _inner.Id;

    /// <inheritdoc />
    public Task<Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var seconds = samples.Length / (double)Audio.AudioFormat.SampleRate;
        if (_spending.Prices?.AudioCost(Id, _model, seconds) is not { } estimate)
        {
            throw Refuse("There's no published price for " + Id + (_model.Length > 0 ? " " + _model : string.Empty) + ", so it isn't used for paid calls.");
        }

        var prices = _spending.Prices;
        return MeteredCall.RunAsync(
            _spending.Ledger,
            _limits,
            _spending.Rates.Current,
            Id,
            _purpose,
            estimate,
            ct => _inner.TranscribeAsync(samples, language, ct),
            transcript => prices.AudioCost(Id, _model, Math.Max(seconds, transcript.AudioSeconds)),
            Options,
            cancellationToken);
    }

    /// <summary>
    /// Runs several calls that make up one piece of work (a whole video's chunks) under one reservation: the cost of all
    /// the audio is reserved before the first call, so a video isn't left half transcribed at the limit, and the work is
    /// settled at the audio it actually sent. The work reports its own failures in its result (see
    /// <paramref name="secondsSent"/>), so audio already sent is always recorded.
    /// </summary>
    /// <typeparam name="T">What the work returns.</typeparam>
    /// <param name="seconds">How much audio the work will send at most.</param>
    /// <param name="work">The work, given the service without metering.</param>
    /// <param name="secondsSent">How much audio the work sent, from its result.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The work's result.</returns>
    /// <exception cref="SpeechToTextException">The price is unknown or the limits refuse it.</exception>
    public Task<T> RunWholeAsync<T>(double seconds, Func<ISpeechToText, CancellationToken, Task<T>> work, Func<T, double> secondsSent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(secondsSent);
        if (_spending.Prices?.AudioCost(Id, _model, seconds) is not { } estimate)
        {
            throw Refuse("There's no published price for " + Id + (_model.Length > 0 ? " " + _model : string.Empty) + ", so it isn't used for paid calls.");
        }

        var prices = _spending.Prices;
        return MeteredCall.RunAsync(
            _spending.Ledger,
            _limits,
            _spending.Rates.Current,
            Id,
            _purpose,
            estimate,
            ct => work(_inner, ct),
            result => prices.AudioCost(Id, _model, secondsSent(result)),
            Options,
            cancellationToken);
    }

    private static SpeechToTextException Refuse(string why) => new(why) { Failure = FailureClass.ProviderLimit };
}
