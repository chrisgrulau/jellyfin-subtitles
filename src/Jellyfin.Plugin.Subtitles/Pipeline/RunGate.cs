using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Subtitles.Pipeline;

/// <summary>
/// Keeps the nightly tasks and the plugin's other work on subtitle files apart, so two never change the same file at
/// once. The nightly tasks share it (they already avoid each other's files, as they always have); the handling of new
/// videos and "Restore all originals" each need it alone. A nightly task that starts while one of those holds it waits
/// for it to finish; they, in turn, don't start while a nightly task runs (new videos wait and try again later, a
/// restore is refused with the reason).
/// </summary>
public sealed class RunGate
{
    private readonly Lock _lock = new();
    private int _shared;
    private string? _alone;
    private TaskCompletionSource? _released;

    /// <summary>
    /// Gets what holds the gate now, in words for the settings page, or <c>null</c> when nothing does.
    /// </summary>
    public string? Busy
    {
        get
        {
            lock (_lock)
            {
                return _alone ?? (_shared > 0 ? "a Shoal Subtitles scheduled task is running" : null);
            }
        }
    }

    /// <summary>
    /// Takes the gate alone, if nothing holds it.
    /// </summary>
    /// <param name="what">What takes it, in words (for example "handling new videos").</param>
    /// <returns>A hold to dispose when done, or <c>null</c> when something else holds the gate.</returns>
    public IDisposable? TryEnterAlone(string what)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        lock (_lock)
        {
            if (_shared > 0 || _alone is not null)
            {
                return null;
            }

            _alone = what;
            _released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return new Hold(this, alone: true);
        }
    }

    /// <summary>
    /// Takes the gate alongside the other nightly tasks, once nothing holds it alone.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A hold to dispose when done.</returns>
    public async Task<IDisposable> EnterSharedAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (_lock)
            {
                if (_alone is null)
                {
                    _shared++;
                    return new Hold(this, alone: false);
                }

                wait = _released!.Task;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void Release(bool alone)
    {
        TaskCompletionSource? released = null;
        lock (_lock)
        {
            if (alone)
            {
                _alone = null;
                released = _released;
                _released = null;
            }
            else
            {
                _shared--;
            }
        }

        released?.TrySetResult();
    }

    private sealed class Hold(RunGate gate, bool alone) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                gate.Release(alone);
            }
        }
    }
}
