using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Subtitles.SpeechToText;

/// <summary>
/// Whether any speech-to-text service can be expected to answer right now: the chosen one or a free stand-in, neither
/// passed over in this run (it failed, its key was refused, it can't start) nor in a systemic problem. Work that waits
/// only for speech-to-text (a deferred check or search) is skipped while none can, rather than repeating free work or
/// using up the day's downloads. A systemic problem stops counting a day after its last failure, so waiting work is
/// tried again at least daily and a recovered service is noticed.
/// </summary>
public static class SpeechReadiness
{
    /// <summary>How long after its last failure a systemic problem stops holding waiting work back.</summary>
    public static readonly TimeSpan HoldFor = TimeSpan.FromHours(24);

    /// <summary>
    /// Whether some service can be used now.
    /// </summary>
    /// <param name="speech">The run's service (with its stand-ins), or <c>null</c> when none is set up.</param>
    /// <param name="health">Each service's health (see <see cref="SpeechErrorLog.Health"/>).</param>
    /// <param name="now">The current time.</param>
    /// <returns><c>true</c> if a service may answer.</returns>
    public static bool Ready(ISpeechToText? speech, IEnumerable<ProviderHealth> health, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(health);
        if (speech is null)
        {
            return false;
        }

        var troubled = health.Where(h => h.Systemic && h.LastFailure is { } last && now - last < HoldFor).Select(h => h.Provider).ToHashSet(StringComparer.Ordinal);
        var candidates = speech is FallbackSpeechToText chain ? chain.Available() : [speech.Id];
        return candidates.Any(id => !troubled.Contains(id));
    }
}
