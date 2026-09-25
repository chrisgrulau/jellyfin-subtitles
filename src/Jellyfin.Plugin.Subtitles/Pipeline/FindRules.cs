using System;

namespace Jellyfin.Plugin.Subtitles.Pipeline;

/// <summary>
/// Decisions of the missing-subtitle search that don't need Jellyfin's types (kept apart so they can be tested).
/// </summary>
public static class FindRules
{
    /// <summary>
    /// Whether a provider exception means no further search can work today: the provider's download allowance is used up
    /// (the OpenSubtitles plugin's <c>RateLimitExceededException</c>, matched by name to avoid depending on that plugin) or
    /// it can't sign in.
    /// </summary>
    /// <param name="ex">The exception.</param>
    /// <returns><c>true</c> to stop the run.</returns>
    public static bool StopsTheRun(Exception ex)
        => ex is System.Security.Authentication.AuthenticationException
            || (ex?.GetType().Name is { } name && name.Contains("RateLimit", StringComparison.Ordinal));

    /// <summary>
    /// Whether an existing subtitle track means the video already has subtitles in its language: forced-only tracks never
    /// do; picture-based ones do unless the setting says otherwise.
    /// </summary>
    /// <param name="isForced">The track is forced-only.</param>
    /// <param name="isText">The track is text-based.</param>
    /// <param name="countImages">Whether picture-based tracks count.</param>
    /// <returns><c>true</c> if it counts.</returns>
    public static bool Counts(bool isForced, bool isText, bool countImages) => !isForced && (isText || countImages);

}
