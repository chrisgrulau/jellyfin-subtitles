using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Resilience;

namespace Jellyfin.Plugin.Subtitles.SpeechToText;

/// <summary>
/// Which free services on this server stand in when the chosen speech-to-text service fails, and whether a service can
/// be used now.
/// </summary>
public static class SpeechFallback
{
    /// <summary>How long a service that failed (after its retries) is passed over for the rest of a run.</summary>
    public static readonly TimeSpan PassOverFor = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The free services to fall back to, in order: the local service when its address is set, then the built-in one
    /// when its download is allowed and it is already installed (a fallback never starts a download). Never a paid
    /// service, and never the chosen service itself; so a failing local service falls back to the built-in one, and a
    /// failing built-in one to the local service.
    /// </summary>
    /// <param name="enabled">Whether falling back is switched on.</param>
    /// <param name="chosen">The chosen service's id.</param>
    /// <param name="localAddress">The local service's address, as set.</param>
    /// <param name="builtInAllowed">Whether the built-in download is allowed.</param>
    /// <param name="builtInInstalled">Whether a built-in model is installed.</param>
    /// <returns>The services' ids (<see cref="SpeechToTextFactory.Local"/>, <see cref="SpeechToTextFactory.BuiltIn"/>), possibly none.</returns>
    public static IReadOnlyList<string> Chain(bool enabled, string chosen, string? localAddress, bool builtInAllowed, bool builtInInstalled)
    {
        var chain = new List<string>();
        if (!enabled)
        {
            return chain;
        }

        if (chosen != SpeechToTextFactory.Local && LocalAddressSet(localAddress))
        {
            chain.Add(SpeechToTextFactory.Local);
        }

        if (chosen != SpeechToTextFactory.BuiltIn && builtInAllowed && builtInInstalled)
        {
            chain.Add(SpeechToTextFactory.BuiltIn);
        }

        return chain;
    }

    /// <summary>
    /// Whether a service can be used now: a paid one with its key set and paid use allowed, the local one with its
    /// address set, the built-in one allowed and installed. "Rerun with …" is offered only for a service that can.
    /// </summary>
    /// <param name="provider">The service id.</param>
    /// <param name="keySet">Whether its key is set (paid services).</param>
    /// <param name="paidAllowed">Whether the spending limit allows paid services.</param>
    /// <param name="localAddress">The local service's address, as set.</param>
    /// <param name="builtInAllowed">Whether the built-in download is allowed.</param>
    /// <param name="builtInInstalled">Whether a built-in model is installed.</param>
    /// <returns><c>true</c> if it can be used.</returns>
    public static bool Usable(string provider, bool keySet, bool paidAllowed, string? localAddress, bool builtInAllowed, bool builtInInstalled) => provider switch
    {
        SpeechToTextFactory.Deepgram or SpeechToTextFactory.OpenAi => keySet && paidAllowed,
        SpeechToTextFactory.Local => LocalAddressSet(localAddress),
        SpeechToTextFactory.BuiltIn => builtInAllowed && builtInInstalled,
        _ => false,
    };

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
        var instead = SpeechHealth.NameOf(used);
        return What(chosen, ex) + "; used " + char.ToLowerInvariant(instead[0]) + instead[1..] + " instead.";
    }

    /// <summary>
    /// What went wrong with a service, in a few words ("Deepgram couldn't be reached").
    /// </summary>
    /// <param name="provider">The service id.</param>
    /// <param name="ex">Its failure.</param>
    /// <returns>The words.</returns>
    public static string What(string provider, SpeechToTextException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var name = SpeechHealth.NameOf(provider);
        return ex.Failure switch
        {
            _ when ex.ServiceBroken => name + " couldn't be started",
            FailureClass.Authentication => name + " refused the key",
            FailureClass.ProviderLimit => name + " was over a limit (its allowance, or the monthly spending limit)",
            _ when ex.StatusCode is { } status => name + " kept failing (HTTP " + ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture) + ")",
            _ when provider == SpeechToTextFactory.BuiltIn => name + " failed",
            _ => name + " couldn't be reached",
        };
    }

    private static bool LocalAddressSet(string? address)
        => Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

/// <summary>
/// The chosen speech-to-text service, with free services on this server standing in, in turn, when it fails (after its
/// retries) for any reason but bad input (see <see cref="SpeechFallback.Chain"/>). Each transcript made by a stand-in says
/// so (<see cref="Transcript.FallbackFrom"/>), so the result can say it and offer to run the check again with the chosen
/// service. A service that failed is passed over for <see cref="SpeechFallback.PassOverFor"/> (a refused key, a used-up
/// allowance or a built-in program that can't start: for the rest of the run), so a run doesn't retry a service that is
/// down for every file. When every service failed, the chosen service's failure is thrown, naming the others'.
/// </summary>
internal sealed class FallbackSpeechToText : ISpeechToText
{
    private readonly ISpeechToText _chosen;
    private readonly IReadOnlyList<(string Id, Func<ISpeechToText?> Make)> _standIns;
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, ISpeechToText?> _made = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset Until, SpeechToTextException Failure)> _passedOver = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="FallbackSpeechToText"/> class.
    /// </summary>
    /// <param name="chosen">The chosen service.</param>
    /// <param name="standIns">The free services to use instead, in order, each made only when first needed (<c>null</c> if it can't be made).</param>
    /// <param name="clock">Clock.</param>
    public FallbackSpeechToText(ISpeechToText chosen, IReadOnlyList<(string Id, Func<ISpeechToText?> Make)> standIns, TimeProvider? clock = null)
    {
        _chosen = chosen ?? throw new ArgumentNullException(nameof(chosen));
        _standIns = standIns ?? throw new ArgumentNullException(nameof(standIns));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FallbackSpeechToText"/> class with one stand-in.
    /// </summary>
    /// <param name="chosen">The chosen service.</param>
    /// <param name="standIn">Makes the free service to use instead; <c>null</c> if it can't be made.</param>
    /// <param name="clock">Clock.</param>
    public FallbackSpeechToText(ISpeechToText chosen, Func<ISpeechToText?> standIn, TimeProvider? clock = null)
        : this(chosen, [(string.Empty, standIn)], clock)
    {
    }

    /// <inheritdoc />
    public string Id => _chosen.Id;

    /// <inheritdoc />
    public async Task<Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken cancellationToken)
    {
        var chosenFailure = PassedOver(_chosen.Id);
        if (chosenFailure is null)
        {
            try
            {
                return await _chosen.TranscribeAsync(samples, language, cancellationToken).ConfigureAwait(false);
            }
            catch (SpeechToTextException ex) when (SpeechFallback.FallsBack(ex.Failure))
            {
                chosenFailure = ex;
                PassOver(_chosen.Id, ex);
            }
        }

        var failures = new List<string>();
        foreach (var (key, make) in _standIns)
        {
            var standIn = Make(key, make);
            if (standIn is null || standIn.Id == _chosen.Id)
            {
                continue;
            }

            if (PassedOver(standIn.Id) is { } earlier)
            {
                failures.Add(SpeechFallback.What(standIn.Id, earlier));
                continue;
            }

            try
            {
                var transcript = await standIn.TranscribeAsync(samples, language, cancellationToken).ConfigureAwait(false);
                return transcript with { FallbackFrom = _chosen.Id, FallbackReason = SpeechFallback.Reason(_chosen.Id, standIn.Id, chosenFailure) };
            }
            catch (SpeechToTextException ex)
            {
                PassOver(standIn.Id, ex);
                failures.Add(SpeechFallback.What(standIn.Id, ex) + ": " + ex.Message);
            }
        }

        if (failures.Count == 0)
        {
            throw chosenFailure;
        }

        throw new SpeechToTextException(chosenFailure.Message.TrimEnd() + " Tried instead, and failed too: " + string.Join("; ", failures), chosenFailure)
        {
            Failure = chosenFailure.Failure,
            StatusCode = chosenFailure.StatusCode,
            Attempts = chosenFailure.Attempts,
            ServiceBroken = chosenFailure.ServiceBroken,
        };
    }

    private SpeechToTextException? PassedOver(string id)
    {
        lock (_lock)
        {
            return _passedOver.TryGetValue(id, out var p) && _clock.GetUtcNow() < p.Until ? p.Failure : null;
        }
    }

    private void PassOver(string id, SpeechToTextException ex)
    {
        var until = ex.ServiceBroken || ex.Failure is FailureClass.Authentication or FailureClass.ProviderLimit ? DateTimeOffset.MaxValue : _clock.GetUtcNow() + SpeechFallback.PassOverFor;
        lock (_lock)
        {
            _passedOver[id] = (until, ex);
        }
    }

    private ISpeechToText? Make(string key, Func<ISpeechToText?> make)
    {
        lock (_lock)
        {
            if (!_made.TryGetValue(key, out var service))
            {
                _made[key] = service = make();
            }

            return service;
        }
    }
}
