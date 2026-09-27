using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Resilience;

namespace Jellyfin.Plugin.Subtitles.SpeechToText;

/// <summary>
/// Which free service on this server stands in when the chosen speech-to-text service fails.
/// </summary>
public static class SpeechFallback
{
    /// <summary>How long a service that failed (after its retries) is passed over for the rest of a run.</summary>
    public static readonly TimeSpan PassOverFor = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The free service to fall back to, if any: the local service when its address is set, else the built-in one when
    /// its download is allowed and it is already installed (a fallback never starts a download). Never a paid service,
    /// and never the chosen service itself.
    /// </summary>
    /// <param name="enabled">Whether falling back is switched on.</param>
    /// <param name="chosen">The chosen service's id.</param>
    /// <param name="localAddress">The local service's address, as set.</param>
    /// <param name="builtInAllowed">Whether the built-in download is allowed.</param>
    /// <param name="builtInInstalled">Whether a built-in model is installed.</param>
    /// <returns>The service id (<see cref="SpeechToTextFactory.Local"/> or <see cref="SpeechToTextFactory.BuiltIn"/>), or <c>null</c>.</returns>
    public static string? Choose(bool enabled, string chosen, string? localAddress, bool builtInAllowed, bool builtInInstalled)
    {
        if (!enabled)
        {
            return null;
        }

        if (chosen != SpeechToTextFactory.Local && Uri.TryCreate(localAddress?.Trim(), UriKind.Absolute, out var address)
            && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps))
        {
            return SpeechToTextFactory.Local;
        }

        return chosen != SpeechToTextFactory.BuiltIn && builtInAllowed && builtInInstalled ? SpeechToTextFactory.BuiltIn : null;
    }

    /// <summary>
    /// Whether a failure of the chosen service is one to fall back from: anything but a request the service rejected as
    /// bad input (which another service would reject too).
    /// </summary>
    /// <param name="failure">The failure class.</param>
    /// <returns><c>true</c> to fall back.</returns>
    internal static bool FallsBack(FailureClass failure) => failure != FailureClass.BadRequest;

    /// <summary>
    /// Why the chosen service wasn't used, for the result ("Deepgram couldn't be reached; used the local service
    /// instead.").
    /// </summary>
    /// <param name="chosen">The chosen service's id.</param>
    /// <param name="used">The service used instead.</param>
    /// <param name="ex">The chosen service's failure.</param>
    /// <returns>One sentence.</returns>
    public static string Reason(string chosen, string used, SpeechToTextException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var name = SpeechHealth.NameOf(chosen);
        var what = ex.Failure switch
        {
            FailureClass.Authentication => name + " refused the key",
            FailureClass.ProviderLimit => name + " was over a limit (its allowance, or the monthly spending limit)",
            _ when ex.StatusCode is { } status => name + " kept failing (HTTP " + ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture) + ")",
            _ => name + " couldn't be reached",
        };
        var instead = SpeechHealth.NameOf(used);
        return what + "; used " + char.ToLowerInvariant(instead[0]) + instead[1..] + " instead.";
    }
}

/// <summary>
/// The chosen speech-to-text service, with a free service on this server standing in when it fails (after its retries)
/// for any reason but bad input. Each transcript made by the stand-in says so (<see cref="Transcript.FallbackFrom"/>), so
/// the result can say it and offer to run the check again with the chosen service. After a failure the chosen service
/// is passed over for <see cref="SpeechFallback.PassOverFor"/> (a refused key or a used-up allowance: for the rest of the
/// run), so a run doesn't retry a service that is down for every file.
/// </summary>
internal sealed class FallbackSpeechToText : ISpeechToText
{
    private readonly ISpeechToText _chosen;
    private readonly Func<ISpeechToText?> _standIn;
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private ISpeechToText? _made;
    private bool _triedMaking;
    private DateTimeOffset _passOverUntil = DateTimeOffset.MinValue;
    private SpeechToTextException? _lastFailure;

    /// <summary>
    /// Initializes a new instance of the <see cref="FallbackSpeechToText"/> class.
    /// </summary>
    /// <param name="chosen">The chosen service.</param>
    /// <param name="standIn">Makes the free service to use instead (only when first needed); <c>null</c> if it can't be made.</param>
    /// <param name="clock">Clock.</param>
    public FallbackSpeechToText(ISpeechToText chosen, Func<ISpeechToText?> standIn, TimeProvider? clock = null)
    {
        _chosen = chosen ?? throw new ArgumentNullException(nameof(chosen));
        _standIn = standIn ?? throw new ArgumentNullException(nameof(standIn));
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string Id => _chosen.Id;

    /// <inheritdoc />
    public async Task<Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken cancellationToken)
    {
        SpeechToTextException failure;
        DateTimeOffset until;
        lock (_lock)
        {
            until = _passOverUntil;
            failure = _lastFailure!;
        }

        if (_clock.GetUtcNow() >= until)
        {
            try
            {
                return await _chosen.TranscribeAsync(samples, language, cancellationToken).ConfigureAwait(false);
            }
            catch (SpeechToTextException ex) when (SpeechFallback.FallsBack(ex.Failure))
            {
                failure = ex;
                lock (_lock)
                {
                    _lastFailure = ex;
                    _passOverUntil = ex.Failure is FailureClass.Authentication or FailureClass.ProviderLimit ? DateTimeOffset.MaxValue : _clock.GetUtcNow() + SpeechFallback.PassOverFor;
                }
            }
        }

        var standIn = StandIn() ?? throw failure;
        try
        {
            var transcript = await standIn.TranscribeAsync(samples, language, cancellationToken).ConfigureAwait(false);
            return transcript with { FallbackFrom = _chosen.Id, FallbackReason = SpeechFallback.Reason(_chosen.Id, standIn.Id, failure) };
        }
        catch (SpeechToTextException ex)
        {
            throw new SpeechToTextException(failure.Message.TrimEnd() + " " + SpeechHealth.NameOf(standIn.Id) + " was tried instead and failed too: " + ex.Message, ex)
            {
                Failure = failure.Failure,
                StatusCode = failure.StatusCode,
                Attempts = failure.Attempts,
            };
        }
    }

    private ISpeechToText? StandIn()
    {
        lock (_lock)
        {
            if (!_triedMaking)
            {
                _triedMaking = true;
                _made = _standIn();
            }

            return _made;
        }
    }
}
