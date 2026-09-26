using System;

namespace Jellyfin.Plugin.Subtitles.Candidates;

/// <summary>
/// No subtitle source answered a search: none is installed or enabled, or every one failed (an outage). The video is
/// then not recorded as "nothing fitting found", so it's searched again on the next run.
/// </summary>
public sealed class NoSourceAnsweredException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="NoSourceAnsweredException"/> class.</summary>
    public NoSourceAnsweredException()
        : base("No subtitle provider answered.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="NoSourceAnsweredException"/> class.</summary>
    /// <param name="message">Message.</param>
    public NoSourceAnsweredException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="NoSourceAnsweredException"/> class.</summary>
    /// <param name="message">Message.</param>
    /// <param name="innerException">Cause.</param>
    public NoSourceAnsweredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Gets a value indicating whether no source is available at all (none installed or enabled), rather than every one
    /// failing this time: every further search in the run would find nothing too.
    /// </summary>
    public bool NoneAvailable { get; init; }
}
