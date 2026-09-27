using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Costs;
using Jellyfin.Plugin.Common.Resilience;
using Jellyfin.Plugin.Subtitles.Audio;
using Jellyfin.Plugin.Subtitles.Configuration;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.Pipeline;
using Jellyfin.Plugin.Subtitles.Pricing;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// Retries, falling back to a free service, "Rerun with …" and telling systemic speech-to-text problems from passing ones.
public sealed class SpeechResilienceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "subs-resil-" + Guid.NewGuid().ToString("N"));

    public SpeechResilienceTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 26, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;

        public override long GetTimestamp() => Now.UtcTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    // Takes as long as a timed-out whole chunk before failing
    private sealed class TimesOut(Clock clock) : ISpeechToText
    {
        public int Calls { get; private set; }

        public string Id => "local";

        public Task<Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken cancellationToken)
        {
            Calls++;
            clock.Now += TimeSpan.FromMinutes(6);
            throw new SpeechToTextException("The local service took too long.") { Failure = FailureClass.Transient };
        }
    }

    [Fact]
    public async Task A_call_that_timed_out_after_minutes_is_not_sent_again()
    {
        var clock = new Clock();
        var inner = new TimesOut(clock);
        var service = new RetryingSpeechToText(inner, wait: (_, _) => Task.CompletedTask, clock: clock);

        await Assert.ThrowsAsync<SpeechToTextException>(() => service.TranscribeAsync(Second(), "en", TestContext.Current.CancellationToken));
        Assert.Equal(1, inner.Calls);
    }

    // Fails with the given failures in turn, then answers
    private sealed class Flaky(string id, params SpeechToTextException[] failures) : ISpeechToText
    {
        public int Calls { get; private set; }

        public string Id => id;

        public Task<Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls <= failures.Length)
            {
                throw failures[Calls - 1];
            }

            return Task.FromResult(new Transcript([new TranscribedWord("hello", 0, 0.5, 0.9)], "en", id, "m", samples.Length / (double)AudioFormat.SampleRate));
        }
    }

    private static SpeechToTextException Fail(FailureClass failure, HttpStatusCode? status = null, TimeSpan? retryAfter = null)
        => new("api.example.com couldn't be reached: An error occurred while sending the request.") { Failure = failure, StatusCode = status, RetryAfter = retryAfter };

    private static SpeechToTextException[] Many(int n, FailureClass failure) => [.. Enumerable.Range(0, n).Select(_ => Fail(failure))];

    private static (RetryingSpeechToText Service, List<TimeSpan> Waits) Retrying(ISpeechToText inner, SpeechErrorLog? log = null, string? run = "run1")
    {
        var waits = new List<TimeSpan>();
        return (new RetryingSpeechToText(inner, log, run, (d, _) => { waits.Add(d); return Task.CompletedTask; }, () => 0.5), waits);
    }

    private static float[] Second() => new float[AudioFormat.SampleRate];

    [Fact]
    public void The_schedule_doubles_from_about_two_seconds_with_jitter_and_stops_after_five_attempts()
    {
        for (var attempts = 1; attempts <= 4; attempts++)
        {
            var nominal = Math.Pow(2, attempts);
            var low = SpeechRetry.DelayBefore(attempts, FailureClass.Transient, null, 0)!.Value.TotalSeconds;
            var high = SpeechRetry.DelayBefore(attempts, FailureClass.Transient, null, 0.999)!.Value.TotalSeconds;
            Assert.InRange(low, (nominal / 2) - 0.01, nominal);
            Assert.InRange(high, nominal * 0.99, nominal);
        }

        Assert.Null(SpeechRetry.DelayBefore(5, FailureClass.Transient, null, 0.5));
        Assert.NotNull(SpeechRetry.DelayBefore(1, FailureClass.NoConnection, null, 0.5));
        Assert.True(SpeechRetry.DelayBefore(2, FailureClass.NoConnection, null, 0.5) <= TimeSpan.FromSeconds(4));
    }

    [Fact]
    public void A_short_retry_after_is_honoured_and_a_long_one_is_not_waited_out()
    {
        var wait = SpeechRetry.DelayBefore(1, FailureClass.Transient, TimeSpan.FromSeconds(30), 0.5)!.Value;
        Assert.InRange(wait.TotalSeconds, 30, 33);
        Assert.Equal(SpeechRetry.MaxDelay, SpeechRetry.DelayBefore(1, FailureClass.Transient, SpeechRetry.MaxDelay, 0.999));
        Assert.Null(SpeechRetry.DelayBefore(1, FailureClass.Transient, TimeSpan.FromMinutes(5), 0.5));
    }

    [Theory]
    [InlineData("Authentication")]
    [InlineData("BadRequest")]
    [InlineData("ProviderLimit")]
    public async Task Refused_keys_bad_requests_and_used_up_allowances_are_not_retried(string name)
    {
        var failure = Enum.Parse<FailureClass>(name);
        var inner = new Flaky("deepgram", Fail(failure));
        var (service, waits) = Retrying(inner);

        var ex = await Assert.ThrowsAsync<SpeechToTextException>(() => service.TranscribeAsync(Second(), "en", TestContext.Current.CancellationToken));

        Assert.Equal(1, inner.Calls);
        Assert.Empty(waits);
        Assert.Equal(failure, ex.Failure);
        Assert.False(SpeechRetry.IsRetryable(failure));
    }

    [Fact]
    public async Task A_passing_failure_is_retried_and_recorded_as_recovered()
    {
        var log = new SpeechErrorLog(_dir);
        var inner = new Flaky("deepgram", Fail(FailureClass.NoConnection), Fail(FailureClass.Transient, HttpStatusCode.ServiceUnavailable));
        var (service, waits) = Retrying(inner, log);

        var transcript = await service.TranscribeAsync(Second(), "en", TestContext.Current.CancellationToken);

        Assert.Equal("deepgram", transcript.Provider);
        Assert.Equal(3, inner.Calls);
        Assert.Equal(2, waits.Count);
        var failure = Assert.Single(log.Recent(0, 10).Items);
        Assert.True(failure.Recovered);
        Assert.Equal(3, failure.Attempts);
        Assert.Equal("Transient", failure.Failure);
        var health = Assert.Single(log.Health());
        Assert.False(health.Systemic);
        Assert.Equal(1, health.Recovered);
        Assert.Equal(0, health.Failures);
    }

    [Fact]
    public async Task Retries_stop_after_five_attempts_and_say_so()
    {
        var log = new SpeechErrorLog(_dir);
        var inner = new Flaky("openai", Many(9, FailureClass.Transient));
        var (service, waits) = Retrying(inner, log);

        var ex = await Assert.ThrowsAsync<SpeechToTextException>(() => service.TranscribeAsync(Second(), "en", TestContext.Current.CancellationToken));

        Assert.Equal(SpeechRetry.MaxAttempts, inner.Calls);
        Assert.Equal(SpeechRetry.MaxAttempts - 1, waits.Count);
        Assert.All(waits, w => Assert.True(w <= SpeechRetry.MaxDelay));
        Assert.Equal(5, ex.Attempts);
        Assert.Contains("tried 5 times", ex.Message, StringComparison.Ordinal);
        Assert.False(Assert.Single(log.Recent(0, 10).Items).Recovered);
    }

    [Fact]
    public async Task Cancellation_stops_the_retries()
    {
        using var cts = new CancellationTokenSource();
        var inner = new Flaky("deepgram", Many(9, FailureClass.Transient));
        var service = new RetryingSpeechToText(inner, wait: (_, _) => { cts.Cancel(); return Task.FromCanceled(cts.Token); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TranscribeAsync(Second(), "en", cts.Token));
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task A_retried_paid_call_is_reserved_and_charged_once()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(_dir, "rates.json"), "{\"Date\":\"" + today + "\",\"Source\":\"test\",\"Rates\":{\"EUR\":1,\"USD\":1.10,\"AUD\":1.65}}");
        using var spending = new Spending(_dir);
        var limits = new SpendLimits("AUD", 5m, new Dictionary<string, decimal>(), 0m);
        var inner = new Flaky("deepgram", Fail(FailureClass.Transient), Fail(FailureClass.Transient));
        var (retrying, _) = Retrying(inner);
        var metered = new MeteredSpeechToText(retrying, "nova-3", spending, limits, "subtitles.sync");

        await metered.TranscribeAsync(new float[AudioFormat.SampleRate * 60], "en", TestContext.Current.CancellationToken);

        // One minute of Nova-3 (USD 0.0043 = AUD 0.00645), however many attempts it took
        Assert.Equal(3, inner.Calls);
        Assert.Equal(0.00645m, spending.Ledger.ThisMonth(limits, spending.Rates.Current).Total);
    }

    [Fact]
    public void The_stand_in_is_free_local_first_and_needs_consent_and_an_install_for_built_in()
    {
        const string Local = "http://127.0.0.1:8000/v1";
        Assert.Equal("local", SpeechFallback.Choose(true, "deepgram", Local, builtInAllowed: true, builtInInstalled: true));
        Assert.Equal("builtin", SpeechFallback.Choose(true, "deepgram", string.Empty, builtInAllowed: true, builtInInstalled: true));
        Assert.Null(SpeechFallback.Choose(true, "deepgram", string.Empty, builtInAllowed: false, builtInInstalled: true));
        Assert.Null(SpeechFallback.Choose(true, "deepgram", string.Empty, builtInAllowed: true, builtInInstalled: false));
        Assert.Null(SpeechFallback.Choose(false, "deepgram", Local, builtInAllowed: true, builtInInstalled: true));
        Assert.Equal("builtin", SpeechFallback.Choose(true, "local", Local, builtInAllowed: true, builtInInstalled: true));
        Assert.Equal("local", SpeechFallback.Choose(true, "builtin", Local, builtInAllowed: true, builtInInstalled: true));
        Assert.Null(SpeechFallback.Choose(true, "local", Local, builtInAllowed: false, builtInInstalled: false));
        Assert.Null(SpeechFallback.Choose(true, "openai", "not an address", builtInAllowed: false, builtInInstalled: false));

        // Never to a paid service, whatever is chosen
        foreach (var chosen in new[] { "deepgram", "openai", "local", "builtin" })
        {
            foreach (var local in new[] { Local, string.Empty })
            {
                var standIn = SpeechFallback.Choose(true, chosen, local, true, true);
                Assert.True(standIn is null || !SpeechToTextFactory.IsPaid(standIn));
                Assert.NotEqual(chosen, standIn);
            }
        }
    }

    [Fact]
    public async Task A_failing_service_falls_back_and_the_transcript_says_so()
    {
        var clock = new Clock();
        var chosen = new Flaky("deepgram", Many(9, FailureClass.NoConnection));
        var standIn = new Flaky("local");
        var service = new FallbackSpeechToText(chosen, () => standIn, clock);

        var first = await service.TranscribeAsync(Second(), "en", TestContext.Current.CancellationToken);
        var second = await service.TranscribeAsync(Second(), "en", TestContext.Current.CancellationToken);

        Assert.Equal("deepgram", service.Id);
        Assert.Equal("local", first.Provider);
        Assert.Equal("deepgram", first.FallbackFrom);
        Assert.Equal("Deepgram couldn't be reached; used the local service instead.", first.FallbackReason);
        Assert.Equal("deepgram", second.FallbackFrom);

        // The failed service is passed over for a while rather than tried (and retried) for every file
        Assert.Equal(1, chosen.Calls);
        clock.Now += SpeechFallback.PassOverFor;
        await service.TranscribeAsync(Second(), "en", TestContext.Current.CancellationToken);
        Assert.Equal(2, chosen.Calls);
    }

    [Fact]
    public async Task Bad_input_does_not_fall_back_and_no_stand_in_means_the_failure_stands()
    {
        var standIn = new Flaky("local");
        var bad = new FallbackSpeechToText(new Flaky("deepgram", Fail(FailureClass.BadRequest)), () => standIn);
        await Assert.ThrowsAsync<SpeechToTextException>(() => bad.TranscribeAsync(Second(), "en", TestContext.Current.CancellationToken));
        Assert.Equal(0, standIn.Calls);

        var none = new FallbackSpeechToText(new Flaky("deepgram", Fail(FailureClass.Authentication)), () => null);
        var ex = await Assert.ThrowsAsync<SpeechToTextException>(() => none.TranscribeAsync(Second(), "en", TestContext.Current.CancellationToken));
        Assert.Equal(FailureClass.Authentication, ex.Failure);
        Assert.Contains("refused the key", SpeechFallback.Reason("deepgram", "builtin", ex), StringComparison.Ordinal);
        Assert.EndsWith("used the built-in speech-to-text instead.", SpeechFallback.Reason("deepgram", "builtin", ex), StringComparison.Ordinal);
    }

    // The stand-in hears what the fake audio says, under its own id
    private sealed class StandIn(PipelineTests.Shifted inner) : ISpeechToText
    {
        public string Id => "local";

        public async Task<Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken cancellationToken)
            => await inner.TranscribeAsync(samples, language, cancellationToken) with { Provider = "local" };
    }

    [Fact]
    public async Task A_check_that_fell_back_records_it_and_can_be_rerun_with_the_chosen_service()
    {
        var path = Path.Combine(_dir, "Example Show S01E05.en.srt");
        File.WriteAllBytes(path, SubtitleWriter.ToBytes(PipelineTests.Story()));
        var store = new ResultStore(Path.Combine(_dir, "results.json"));
        var processor = new SubtitleProcessor(store, new SubtitleFiles(Path.Combine(_dir, "originals")));
        var job = new SubtitleJob(Guid.NewGuid(), "Pilot", Path.Combine(_dir, "Example Show S01E05.mkv"), path, "eng", TimeSpan.FromMinutes(25), 0);
        var audio = new PipelineTests.Shifted(PipelineTests.Story(), 0);
        var speech = new FallbackSpeechToText(new Flaky("deepgram", Many(99, FailureClass.Transient)), () => new StandIn(audio));
        var review = new Policies(ChangePolicy.Review, ChangePolicy.Review, new CleanupSettings());

        var result = await processor.ProcessAsync(job, audio, speech, review, TestContext.Current.CancellationToken);

        Assert.NotNull(result.SpeechFallback);
        Assert.Equal("deepgram", result.SpeechFallback!.From);
        Assert.Equal("local", result.SpeechFallback.To);
        Assert.Equal("local", result.SpeechSetup);
        Assert.Contains("used the local service instead", result.Explanation, StringComparison.Ordinal);
        Assert.True(SubtitleProcessor.CanRerun(result));
        var fingerprint = SubtitleFiles.Fingerprint(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));

        var queued = processor.RequestRerun(result.Id);
        Assert.Equal("deepgram", queued.RerunWith);
        Assert.Equal("deepgram", processor.RerunWith(path));
        Assert.True(processor.NeedsCheck(path, fingerprint, "deepgram"));
        Assert.True(ResultStore.MustKeep(queued, DateTimeOffset.UtcNow));

        // The next run checks it with the chosen service, and the flag goes
        var again = await processor.ProcessAsync(job, audio, audio, review, TestContext.Current.CancellationToken);
        Assert.Null(again.RerunWith);
        Assert.Null(again.SpeechFallback);
        Assert.False(SubtitleProcessor.CanRerun(again));
        Assert.Throws<InvalidOperationException>(() => processor.RequestRerun(again.Id));
    }

    [Fact]
    public async Task A_check_whose_speech_to_text_failed_outright_can_be_rerun()
    {
        var path = Path.Combine(_dir, "Invented Film (2019).en.srt");
        File.WriteAllBytes(path, SubtitleWriter.ToBytes(PipelineTests.Story()));
        var processor = new SubtitleProcessor(new ResultStore(Path.Combine(_dir, "results.json")), new SubtitleFiles(Path.Combine(_dir, "originals")));
        var job = new SubtitleJob(Guid.NewGuid(), "Invented Film", Path.Combine(_dir, "Invented Film (2019).mkv"), path, "eng", TimeSpan.FromMinutes(25), 0);
        var audio = new PipelineTests.Shifted(PipelineTests.Story(), 0);

        var result = await processor.ProcessAsync(job, audio, new Flaky("openai", Fail(FailureClass.Authentication)), new Policies(ChangePolicy.Review, ChangePolicy.Review, new CleanupSettings()), TestContext.Current.CancellationToken);

        Assert.Equal("openai", result.SpeechFallback!.From);
        Assert.Null(result.SpeechFallback.To);
        Assert.StartsWith("OpenAI failed", result.SpeechFallback.Reason, StringComparison.Ordinal);
        Assert.Equal("openai", processor.RequestRerun(result.Id).RerunWith);
    }

    private static SpeechFailure F(DateTimeOffset at, bool recovered = false, string failure = "Transient", string provider = "deepgram")
        => new(at, provider, failure, "api.example.com couldn't be reached", recovered, recovered ? 2 : 5, null);

    [Fact]
    public void Five_failures_a_day_are_systemic_and_four_are_not()
    {
        var now = new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero);
        var four = Enumerable.Range(0, 4).Select(i => F(now.AddHours(-i))).ToList();
        var health = SpeechHealth.Classify("deepgram", four, 100, [], now.AddMinutes(-5), now);
        Assert.False(health.Systemic);
        Assert.Null(health.Problem);

        var five = four.Append(F(now.AddHours(-5))).ToList();
        health = SpeechHealth.Classify("deepgram", five, 100, [], now.AddMinutes(-5), now);
        Assert.True(health.Systemic);
        Assert.Equal("Deepgram has failed 5 times since yesterday — check the key, the network or the provider's status page.", health.Problem);

        // Older than a day, recovered, or another service's: transitory
        var old = Enumerable.Range(0, 9).Select(i => F(now.AddDays(-2).AddHours(-i))).ToList();
        Assert.False(SpeechHealth.Classify("deepgram", old, 100, [], null, now).Systemic);
        var recovered = Enumerable.Range(0, 9).Select(i => F(now.AddHours(-i), recovered: true)).ToList();
        Assert.False(SpeechHealth.Classify("deepgram", recovered, 100, [], null, now).Systemic);
        Assert.False(SpeechHealth.Classify("openai", five, 100, [], null, now).Systemic);
    }

    [Fact]
    public void Half_the_calls_failing_or_three_failed_runs_in_a_row_are_systemic()
    {
        var now = new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero);
        var three = Enumerable.Range(0, 3).Select(i => F(now.AddHours(-i))).ToList();
        Assert.True(SpeechHealth.Classify("deepgram", three, 6, [], null, now).Systemic);
        Assert.False(SpeechHealth.Classify("deepgram", three, 7, [], null, now).Systemic);
        Assert.False(SpeechHealth.Classify("deepgram", three.Take(2), 4, [], null, now).Systemic);

        var one = three.Take(1).ToList();
        Assert.True(SpeechHealth.Classify("deepgram", one, 100, [(10, 1), (12, 1), (9, 2), (10, 0)], null, now).Systemic);
        Assert.False(SpeechHealth.Classify("deepgram", one, 100, [(10, 1), (12, 0), (9, 2)], null, now).Systemic);
    }

    [Fact]
    public void A_refused_key_is_systemic_until_a_call_succeeds()
    {
        var now = new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero);
        var refused = new[] { F(now.AddHours(-1), failure: "Authentication") };
        Assert.Equal("Deepgram refused the key — check it under Services.", SpeechHealth.Classify("deepgram", refused, 50, [], now.AddHours(-2), now).Problem);
        Assert.False(SpeechHealth.Classify("deepgram", refused, 50, [], now.AddMinutes(-10), now).Systemic);
    }

    [Fact]
    public void The_log_counts_calls_rotates_and_tells_once_a_problem_turns_systemic()
    {
        var clock = new Clock();
        var told = new List<ProviderHealth>();
        var log = new SpeechErrorLog(_dir, clock) { Systemic = told.Add };
        var failure = SpeechErrorLog.FailureOf("deepgram", Fail(FailureClass.Transient), recovered: false, 5, "r1");

        for (var i = 0; i < 4; i++)
        {
            log.RecordCall("deepgram", "r1", failure);
            log.RecordCall("deepgram", "r1", null);
            log.RecordCall("deepgram", "r1", null);
        }

        Assert.Empty(told);
        log.RecordCall("deepgram", "r1", failure);
        var systemic = Assert.Single(told);
        Assert.Equal("deepgram", systemic.Provider);
        Assert.Equal(13, systemic.Calls);

        // Read back from disk, newest first, a page at a time
        var reread = new SpeechErrorLog(_dir, clock);
        var (page, total) = reread.Recent(0, 3);
        Assert.Equal(5, total);
        Assert.Equal(3, page.Count);
        Assert.Equal(2, reread.Recent(3, 3).Items.Count);
        Assert.True(reread.Health()[0].Systemic);

        // A month on, the failures are gone and the service is healthy again
        clock.Now += TimeSpan.FromDays(31);
        log.RecordCall("deepgram", "r2", null);
        Assert.Equal(0, log.Recent(0, 10).Total);
        Assert.False(log.Health()[0].Systemic);
    }

    [Fact]
    public void The_log_keeps_at_most_its_limit()
    {
        var clock = new Clock();
        var log = new SpeechErrorLog(_dir, clock);
        var failure = SpeechErrorLog.FailureOf("local", Fail(FailureClass.Transient), recovered: true, 2, null);
        for (var i = 0; i < SpeechErrorLog.MaxEntries + 60; i++)
        {
            clock.Now += TimeSpan.FromSeconds(1);
            log.RecordCall("local", null, failure);
        }

        Assert.InRange(log.Recent(0, 1).Total, SpeechErrorLog.MaxEntries, SpeechErrorLog.MaxEntries + (SpeechErrorLog.MaxEntries / 10));
        Assert.InRange(new SpeechErrorLog(_dir, clock).Recent(0, 1).Total, SpeechErrorLog.MaxEntries, SpeechErrorLog.MaxEntries + (SpeechErrorLog.MaxEntries / 10));
    }
}
