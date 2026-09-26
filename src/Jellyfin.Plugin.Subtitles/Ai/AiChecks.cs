using System;
using System.Threading;

namespace Jellyfin.Plugin.Subtitles.Ai;

/// <summary>
/// How many AI questions one run of a task may still ask, shared by everything that asks during that run.
/// </summary>
public sealed class AiChecks
{
    private int _left;

    /// <summary>
    /// Initializes a new instance of the <see cref="AiChecks"/> class.
    /// </summary>
    /// <param name="limit">The most questions in the run.</param>
    public AiChecks(int limit)
    {
        Limit = Math.Max(0, limit);
        _left = Limit;
    }

    /// <summary>Gets the most questions in the run.</summary>
    public int Limit { get; }

    /// <summary>
    /// Takes one question from the run's allowance.
    /// </summary>
    /// <returns>Whether one was left.</returns>
    public bool TryTake() => Interlocked.Decrement(ref _left) >= 0;
}
