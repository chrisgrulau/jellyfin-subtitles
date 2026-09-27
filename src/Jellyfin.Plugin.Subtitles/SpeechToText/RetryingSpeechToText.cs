using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Resilience;

namespace Jellyfin.Plugin.Subtitles.SpeechToText;

/// <summary>
/// When a failed speech-to-text call is tried again, and how long to wait first: transient failures (no connection,
/// timeouts, server errors, a 429 with a short or no <c>Retry-After</c>) are tried up to <see cref="MaxAttempts"/> times
/// in all, with the shared exponential back-off and jitter (<see cref="BackoffSchedule"/>: about 2, 4, 8 and 16 s),
/// each wait capped at <see cref="MaxDelay"/>. A wait the provider asks for is honoured up to that cap; one longer than
/// that isn't waited out in the middle of a run. Refused keys, rejected requests and used-up allowances are never retried.
/// </summary>
internal static class SpeechRetry
{
    /// <summary>The most attempts one call makes, the first included.</summary>
    public const int MaxAttempts = 5;

    /// <summary>The longest wait before one retry.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    /// <summary>
    /// No retry starts once this long has passed since the first attempt (with the wait before it): a call that timed out
    /// after minutes (a whole chunk of a full transcript) isn't sent again and again within a run's time budget.
    /// </summary>
    public static readonly TimeSpan MaxElapsed = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether a failure of this class is worth trying again soon.
    /// </summary>
    /// <param name="failure">The failure class.</param>
    /// <returns><c>true</c> for <see cref="FailureClass.Transient"/> and <see cref="FailureClass.NoConnection"/>.</returns>
    public static bool IsRetryable(FailureClass failure) => failure is FailureClass.Transient or FailureClass.NoConnection;

    /// <summary>
    /// How long to wait before the next attempt, or <c>null</c> to give up.
    /// </summary>
    /// <param name="attempts">Attempts made so far (1 after the first failure).</param>
    /// <param name="failure">What went wrong.</param>
    /// <param name="retryAfter">The wait the provider asked for, if any.</param>
    /// <param name="jitter">A value in [0, 1) that spreads retries.</param>
    /// <returns>The wait, or <c>null</c>.</returns>
    public static TimeSpan? DelayBefore(int attempts, FailureClass failure, TimeSpan? retryAfter, double jitter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        if (!IsRetryable(failure) || attempts >= MaxAttempts || retryAfter > MaxDelay)
        {
            return null;
        }

        // No connection is backed off like any other transient failure here: a run can't wait a minute per probe
        var wait = BackoffSchedule.Delay(FailureClass.Transient, attempts, retryAfter, Math.Clamp(jitter, 0, 0.999999));
        return wait is { } w && w > MaxDelay ? MaxDelay : wait;
    }
}

/// <summary>
/// A remote speech-to-text service (Deepgram, OpenAI or a local OpenAI-compatible service) whose transient failures are
/// tried again (see <see cref="SpeechRetry"/>). It sits inside <see cref="MeteredSpeechToText"/>, so a paid call is
/// reserved once however many attempts it takes. Every call is counted in the <see cref="SpeechErrorLog"/>, with its
/// failure if it had one and whether a retry recovered it.
/// </summary>
internal sealed class RetryingSpeechToText : ISpeechToText
{
    private readonly ISpeechToText _inner;
    private readonly SpeechErrorLog? _log;
    private readonly string? _run;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;
    private readonly Func<double> _jitter;
    private readonly TimeProvider _clock;
    private readonly int _maxAttempts;

    /// <summary>
    /// Initializes a new instance of the <see cref="RetryingSpeechToText"/> class.
    /// </summary>
    /// <param name="inner">The service.</param>
    /// <param name="log">Where calls and failures are counted, if anywhere.</param>
    /// <param name="run">The run the calls belong to (for failures on consecutive runs).</param>
    /// <param name="wait">Waits between attempts (tests pass one that doesn't).</param>
    /// <param name="jitter">A source of values in [0, 1).</param>
    /// <param name="clock">Clock (for <see cref="SpeechRetry.MaxElapsed"/>).</param>
    /// <param name="maxAttempts">The most attempts (1 to only count calls and failures: the built-in service, which runs
    /// here and has its own time limit).</param>
    public RetryingSpeechToText(ISpeechToText inner, SpeechErrorLog? log = null, string? run = null, Func<TimeSpan, CancellationToken, Task>? wait = null, Func<double>? jitter = null, TimeProvider? clock = null, int maxAttempts = SpeechRetry.MaxAttempts)
    {
        _maxAttempts = Math.Clamp(maxAttempts, 1, SpeechRetry.MaxAttempts);
        _clock = clock ?? TimeProvider.System;
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _log = log;
        _run = run;
        _wait = wait ?? ((delay, ct) => Task.Delay(delay, ct));
        _jitter = jitter ?? Random.Shared.NextDouble;
    }

    /// <inheritdoc />
    public string Id => _inner.Id;

    /// <inheritdoc />
    public async Task<Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken cancellationToken)
    {
        SpeechToTextException? last = null;
        var started = _clock.GetTimestamp();
        for (var attempts = 1; ; attempts++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var transcript = await _inner.TranscribeAsync(samples, language, cancellationToken).ConfigureAwait(false);
                _log?.RecordCall(Id, _run, last is null ? null : SpeechErrorLog.FailureOf(Id, last, recovered: true, attempts, _run));
                return transcript;
            }
            catch (SpeechToTextException ex)
            {
                last = ex;
                var delay = SpeechRetry.DelayBefore(attempts, ex.Failure, ex.RetryAfter, _jitter());
                if (attempts >= _maxAttempts || (delay is { } d && _clock.GetElapsedTime(started) + d > SpeechRetry.MaxElapsed))
                {
                    delay = null;
                }

                if (delay is null)
                {
                    _log?.RecordCall(Id, _run, SpeechErrorLog.FailureOf(Id, ex, recovered: false, attempts, _run));
                    if (attempts == 1)
                    {
                        throw;
                    }

                    throw new SpeechToTextException(ex.Message.TrimEnd() + " (tried " + attempts + " times)", ex)
                    {
                        Failure = ex.Failure,
                        StatusCode = ex.StatusCode,
                        RetryAfter = ex.RetryAfter,
                        Attempts = attempts,
                        ServiceBroken = ex.ServiceBroken,
                    };
                }

                await _wait(delay.Value, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
