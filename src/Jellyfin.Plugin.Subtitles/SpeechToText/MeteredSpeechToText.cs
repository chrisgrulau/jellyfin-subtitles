using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Costs;
using Jellyfin.Plugin.Common.Resilience;
using Jellyfin.Plugin.Subtitles.Pricing;

namespace Jellyfin.Plugin.Subtitles.SpeechToText;

/// <summary>
/// A paid speech-to-text service kept within the spending limits: each call's cost (audio length × the published
/// price) is reserved before it is made and settled afterwards, or released if the call failed. A call whose price or
/// cost in the user's currency is unknown, or that would go over a limit, isn't made.
/// </summary>
internal sealed class MeteredSpeechToText : ISpeechToText
{
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

    /// <summary>Gets why the last call was refused, if it was (so a run can stop using this service).</summary>
    public string? Refused { get; private set; }

    /// <inheritdoc />
    public async Task<Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var seconds = samples.Length / (double)Audio.AudioFormat.SampleRate;
        if (_spending.Prices?.AudioCost(Id, _model, seconds) is not { } estimate)
        {
            throw Refuse("There's no published price for " + Id + (_model.Length > 0 ? " " + _model : string.Empty) + ", so it isn't used for paid calls.");
        }

        var decision = _spending.Ledger.TryReserve(Id, _purpose, estimate, _limits, _spending.Rates.Current);
        if (decision.ReservationId is not { } reservation)
        {
            throw Refuse(decision.Refusal ?? "Not allowed by the spending limits.");
        }

        try
        {
            var transcript = await _inner.TranscribeAsync(samples, language, cancellationToken).ConfigureAwait(false);
            var actual = _spending.Prices.AudioCost(Id, _model, Math.Max(seconds, transcript.AudioSeconds)) ?? estimate;
            _spending.Ledger.Settle(reservation, actual);
            return transcript;
        }
        catch (Exception ex) when (ex is SpeechToTextException or OperationCanceledException)
        {
            // Failed calls aren't charged by these providers
            _spending.Ledger.Release(reservation);
            throw;
        }
    }

    private SpeechToTextException Refuse(string why)
    {
        Refused = why;
        return new SpeechToTextException(why) { Failure = FailureClass.ProviderLimit };
    }
}
