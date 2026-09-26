using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Costs;
using Jellyfin.Plugin.Subtitles.Audio;
using Jellyfin.Plugin.Subtitles.Pricing;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

public sealed class SpendingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "subs-spend-" + Guid.NewGuid().ToString("N"));

    public SpendingTests()
    {
        Directory.CreateDirectory(_dir);

        // Saved ECB rates for today: 1 EUR = 1.10 USD = 1.65 AUD, so 1 USD = 1.5 AUD
        var today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(_dir, "rates.json"), "{\"Date\":\"" + today + "\",\"Source\":\"test\",\"Rates\":{\"EUR\":1,\"USD\":1.10,\"AUD\":1.65}}");
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
    }

    private static SpendLimits Aud(decimal? limit) => new("AUD", limit, new Dictionary<string, decimal>(), 0m);

    // One minute of audio
    private static float[] Minute() => new float[AudioFormat.SampleRate * 60];

    [Fact]
    public void The_shipped_prices_load()
    {
        using var spending = new Spending(_dir);

        Assert.NotNull(spending.Prices);
        Assert.Equal(Money.Of(0.0043m, "USD"), spending.Prices!.PriceOf("deepgram", "nova-3", PriceTable.AudioMinute));
        Assert.Equal(Money.Of(0.006m, "USD"), spending.Prices.PriceOf("openai", "whisper-1", PriceTable.AudioMinute));
    }

    // FAM-06: the page offers the server's currency list, not a copy
    [Fact]
    public void The_currencies_offered_are_the_supported_ones()
    {
        Assert.Equal(CurrencyCode.Supported, Spending.Currencies);
        Assert.Contains("AUD", Spending.Currencies);
    }

    [Fact]
    public async Task Paid_calls_are_priced_recorded_and_stopped_at_the_limit()
    {
        using var spending = new Spending(_dir);
        var inner = new Fake("deepgram");

        // A minute of Nova-3 is USD 0.0043 = AUD 0.00645; a limit of AUD 0.02 allows three
        var metered = new MeteredSpeechToText(inner, "nova-3", spending, Aud(0.02m), "subtitles.sync");
        for (var i = 0; i < 3; i++)
        {
            await metered.TranscribeAsync(Minute(), "en", TestContext.Current.CancellationToken);
        }

        var ex = await Assert.ThrowsAsync<SpeechToTextException>(() => metered.TranscribeAsync(Minute(), "en", TestContext.Current.CancellationToken));

        Assert.Equal(3, inner.Calls);
        Assert.Contains("monthly limit", ex.Message, StringComparison.Ordinal);
        Assert.Equal(Jellyfin.Plugin.Common.Resilience.FailureClass.ProviderLimit, ex.Failure);
        Assert.Equal(0.01935m, spending.Ledger.ThisMonth(Aud(0.02m), spending.Rates.Current).Total);
    }

    [Fact]
    public async Task A_failed_call_is_not_counted()
    {
        using var spending = new Spending(_dir);
        var metered = new MeteredSpeechToText(new Fake("deepgram") { Fail = true }, "nova-3", spending, Aud(5m), "subtitles.sync");

        await Assert.ThrowsAsync<SpeechToTextException>(() => metered.TranscribeAsync(Minute(), "en", TestContext.Current.CancellationToken));

        Assert.Equal(0m, spending.Ledger.ThisMonth(Aud(5m), spending.Rates.Current).Total);
    }

    // FAM-06: metering goes through the shared MeteredCall; an unexpected failure may have been billed, so it counts
    [Fact]
    public async Task An_unexpected_failure_is_recorded_at_the_estimate_and_a_cancellation_is_not()
    {
        using var spending = new Spending(_dir);
        var metered = new MeteredSpeechToText(new Fake("deepgram") { Throw = new InvalidOperationException("odd") }, "nova-3", spending, Aud(5m), "subtitles.sync");
        await Assert.ThrowsAsync<InvalidOperationException>(() => metered.TranscribeAsync(Minute(), "en", TestContext.Current.CancellationToken));
        Assert.Equal(0.00645m, spending.Ledger.ThisMonth(Aud(5m), spending.Rates.Current).Total);

        var cancelled = new MeteredSpeechToText(new Fake("deepgram") { Throw = new OperationCanceledException() }, "nova-3", spending, Aud(5m), "subtitles.sync");
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled.TranscribeAsync(Minute(), "en", TestContext.Current.CancellationToken));
        Assert.Equal(0.00645m, spending.Ledger.ThisMonth(Aud(5m), spending.Rates.Current).Total);
    }

    [Fact]
    public async Task Unknown_prices_zero_limits_and_missing_rates_mean_no_call()
    {
        using var spending = new Spending(_dir);
        var inner = new Fake("openai");

        await Assert.ThrowsAsync<SpeechToTextException>(() => new MeteredSpeechToText(inner, "some-new-model", spending, Aud(5m), "p").TranscribeAsync(Minute(), "en", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<SpeechToTextException>(() => new MeteredSpeechToText(inner, "whisper-1", spending, Aud(0m), "p").TranscribeAsync(Minute(), "en", TestContext.Current.CancellationToken));

        File.Delete(Path.Combine(_dir, "rates.json"));
        using var noRates = new Spending(_dir);
        var ex = await Assert.ThrowsAsync<SpeechToTextException>(() => new MeteredSpeechToText(inner, "whisper-1", noRates, Aud(5m), "p").TranscribeAsync(Minute(), "en", TestContext.Current.CancellationToken));

        Assert.Contains("exchange rates", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, inner.Calls);
    }

    private sealed class Fake(string id) : ISpeechToText
    {
        public string Id => id;

        public bool Fail { get; init; }

        public Exception? Throw { get; init; }

        public int Calls { get; private set; }

        public Task<Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken cancellationToken)
        {
            Calls++;
            if (Throw is not null)
            {
                return Task.FromException<Transcript>(Throw);
            }

            return Fail
                ? throw new SpeechToTextException("service down")
                : Task.FromResult(new Transcript([], "en", id, "m", samples.Length / (double)AudioFormat.SampleRate));
        }
    }
}
