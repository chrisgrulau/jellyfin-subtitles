using System;
using System.Net.Http;
using System.Security.Authentication;
using Jellyfin.Plugin.Common.Resilience;

namespace Jellyfin.Plugin.Subtitles.Candidates;

/// <summary>
/// Classifies what Jellyfin's own subtitle providers throw, at the one boundary where those foreign exceptions enter the
/// plugin, so the rest of the search decides by failure class (see <see cref="Pipeline.FindRules.StopsTheRun"/>).
/// </summary>
internal static class ProviderFailures
{
    /// <summary>
    /// A classified failure for an exception from a Jellyfin subtitle provider:
    /// <list type="bullet">
    /// <item>the provider's download allowance is used up (the OpenSubtitles plugin's <c>RateLimitExceededException</c>,
    /// recognised by its name so this plugin needn't depend on that one) is a <see cref="FailureClass.ProviderLimit"/>;</item>
    /// <item>a failed sign-in is an <see cref="FailureClass.Authentication"/> failure;</item>
    /// <item>an HTTP error with a status is classified by <see cref="HttpFailure"/>.</item>
    /// </list>
    /// </summary>
    /// <param name="ex">What the provider threw.</param>
    /// <returns>The classified failure (keeping the message, with the original as its cause), or <c>null</c> to let the
    /// exception through unchanged (already classified, a cancellation, or unknown).</returns>
    public static ProviderException? Classify(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        FailureClass? failure = ex switch
        {
            ProviderException or OperationCanceledException => null,
            AuthenticationException => FailureClass.Authentication,
            _ when ex.GetType().Name.Contains("RateLimit", StringComparison.Ordinal) => FailureClass.ProviderLimit,
            HttpRequestException { StatusCode: { } status } => HttpFailure.Classify(status),
            _ => null,
        };
        return failure is { } f
            ? new ProviderException(ex.Message, ex) { Failure = f, StatusCode = (ex as HttpRequestException)?.StatusCode }
            : null;
    }
}
