using System;
using Jellyfin.Plugin.Subtitles.Configuration;

namespace Jellyfin.Plugin.Subtitles.Cleaning;

/// <summary>
/// Turns the settings into clean-up options, and says whether each kind of clean-up change is applied automatically or
/// held for review, so clean-up keeps the promise that wording never changes without review unless the user chooses:
/// <list type="bullet">
/// <item>adverts and credits: <see cref="CleanupSettings.AdvertRemoval"/> (automatic by default);</item>
/// <item>empty lines: always automatic (nothing a viewer could read is removed);</item>
/// <item>merged repeats and removed sound descriptions: <see cref="PluginConfiguration.TextChanges"/>;</item>
/// <item>overlaps and brief lines: <see cref="PluginConfiguration.TimingFixes"/>.</item>
/// </list>
/// </summary>
public static class CleanupPolicy
{
    /// <summary>
    /// Clean-up options from the settings.
    /// </summary>
    /// <param name="config">Clean-up settings.</param>
    /// <returns>The options.</returns>
    public static CleanOptions Options(CleanupSettings config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var threshold = TimeSpan.FromMilliseconds(Math.Clamp(config.FlashThresholdMs, 0, 5000));
        var target = TimeSpan.FromMilliseconds(Math.Clamp(config.FlashTargetMs, 0, 5000));
        return new CleanOptions
        {
            RemoveAdverts = config.RemoveAdverts,
            StripHearingImpaired = config.RemoveSoundDescriptions,
            MergeDuplicates = config.MergeDuplicateLines,
            FixOverlaps = config.FixOverlaps,
            ExtendShortCues = config.LengthenFlashLines,
            FlashThreshold = threshold,
            MinimumDuration = target < threshold ? threshold : target,
            FixTypesetTiming = config.FixTypesetTiming,
        };
    }

    /// <summary>
    /// Whether a kind of clean-up change is applied automatically or held for review.
    /// </summary>
    /// <param name="kind">The kind of change.</param>
    /// <param name="cleanup">Clean-up settings.</param>
    /// <param name="textChanges">The wording policy (<see cref="PluginConfiguration.TextChanges"/>).</param>
    /// <param name="timingFixes">The timing policy (<see cref="PluginConfiguration.TimingFixes"/>).</param>
    /// <returns>The policy that applies.</returns>
    public static ChangePolicy For(CleanChangeKind kind, CleanupSettings cleanup, ChangePolicy textChanges, ChangePolicy timingFixes)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        return kind switch
        {
            CleanChangeKind.RemovedAdvert => cleanup.AdvertRemoval,
            CleanChangeKind.RemovedEmpty => ChangePolicy.Automatic,
            CleanChangeKind.MergedDuplicate or CleanChangeKind.StrippedHearingImpaired => textChanges,
            _ => timingFixes,
        };
    }
}
