using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Costs;
using Jellyfin.Plugin.Subtitles.Api;
using Jellyfin.Plugin.Subtitles.Audio;
using Jellyfin.Plugin.Subtitles.Pricing;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// One budget page: paid speech-to-text is metered on Shoal AI's ledger when it keeps the budget, on this plugin's own
// otherwise, and never on both
public sealed class SharedBudgetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "subs-shared-" + Guid.NewGuid().ToString("N"));

    public SharedBudgetTests()
    {
        Directory.CreateDirectory(_dir);
        var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(_dir, "rates.json"), "{\"Date\":\"" + today + "\",\"Source\":\"test\",\"Rates\":{\"EUR\":1,\"USD\":1.10,\"AUD\":1.65}}");
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
    }

    private static SpendLimits Usd(decimal? limit) => new("USD", limit, new Dictionary<string, decimal>(), 0m);

    private static float[] Minute() => new float[AudioFormat.SampleRate * 60];

    [Fact]
    public async Task With_Shoal_AI_keeping_the_budget_a_call_is_metered_there_only()
    {
        var ai = new FakeAi();
        using var spending = new Spending(_dir, null, null, ai.Handle);
        var inner = new Fake("openai");

        // This plugin's own limit of 0 doesn't apply: Shoal AI's does
        Assert.True(spending.PaidMayBeUsed(Usd(0m)));
        await new MeteredSpeechToText(inner, "whisper-1", spending, Usd(0m), "subtitles.sync").TranscribeAsync(Minute(), "en", TestContext.Current.CancellationToken);

        Assert.Equal(1, inner.Calls);
        Assert.Equal(["reserve:openai-speech:0.006", "settle:0.006"], ai.Ops);
        Assert.Equal(0m, spending.ThisMonth(Usd(5m)).Total);
    }

    [Fact]
    public async Task A_whole_video_is_reserved_once_there_and_settled_at_what_was_sent()
    {
        var ai = new FakeAi();
        using var spending = new Spending(_dir, null, null, ai.Handle);
        var metered = new MeteredSpeechToText(new Fake("deepgram"), "nova-3", spending, Usd(5m), "subtitles.generate");

        await metered.RunWholeAsync(600, (_, _) => Task.FromResult(300.0), sent => sent, TestContext.Current.CancellationToken);

        Assert.Equal(["reserve:deepgram:0.043", "settle:0.0215"], ai.Ops);
    }

    [Fact]
    public async Task A_refusal_by_Shoal_AIs_limits_stops_the_call()
    {
        var ai = new FakeAi { Refuse = "provider-limit" };
        using var spending = new Spending(_dir, null, null, ai.Handle);
        var inner = new Fake("deepgram");

        var ex = await Assert.ThrowsAsync<SpeechToTextException>(() => new MeteredSpeechToText(inner, "nova-3", spending, Usd(5m), "subtitles.sync").TranscribeAsync(Minute(), "en", TestContext.Current.CancellationToken));

        Assert.Equal(0, inner.Calls);
        Assert.Equal(Jellyfin.Plugin.Common.Resilience.FailureClass.ProviderLimit, ex.Failure);
        Assert.Contains("Shoal AI", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0m, spending.ThisMonth(Usd(5m)).Total);
    }

    [Theory]
    [InlineData("not-allowed")]
    [InlineData("unsupported-version")]
    public async Task When_Shoal_AI_doesnt_keep_the_budget_the_own_ledger_and_limit_apply(string failure)
    {
        var ai = new FakeAi { Refuse = failure };
        using var spending = new Spending(_dir, null, null, ai.Handle);
        var inner = new Fake("deepgram");

        await new MeteredSpeechToText(inner, "nova-3", spending, Usd(5m), "subtitles.sync").TranscribeAsync(Minute(), "en", TestContext.Current.CancellationToken);
        Assert.Equal(0.0043m, spending.ThisMonth(Usd(5m)).Total);

        // The own limit of 0 refuses, as without Shoal AI
        await Assert.ThrowsAsync<SpeechToTextException>(() => new MeteredSpeechToText(inner, "nova-3", spending, Usd(0m), "subtitles.sync").TranscribeAsync(Minute(), "en", TestContext.Current.CancellationToken));
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Without_Shoal_AI_everything_is_as_before()
    {
        using var spending = new Spending(_dir);
        Assert.False(spending.SharedBudgetInstalled);
        Assert.False(spending.PaidMayBeUsed(Usd(0m)));
        Assert.True(spending.PaidMayBeUsed(Usd(1m)));

        await new MeteredSpeechToText(new Fake("deepgram"), "nova-3", spending, Usd(5m), "subtitles.sync").TranscribeAsync(Minute(), "en", TestContext.Current.CancellationToken);
        Assert.Equal(0.0043m, spending.ThisMonth(Usd(5m)).Total);

        var summary = await spending.SharedSummaryAsync(TestContext.Current.CancellationToken);
        Assert.Equal("not-installed", summary.Failure);
        Assert.Null(SubtitlesController.SharedSummary(summary, "2026-09-01"));
    }

    [Fact]
    public async Task This_months_own_spending_is_carried_to_Shoal_AI_once()
    {
        // Spent on the own ledger before Shoal AI kept the budget
        using (var before = new Spending(_dir))
        {
            await new MeteredSpeechToText(new Fake("openai"), "whisper-1", before, Usd(5m), "subtitles.sync").TranscribeAsync(Minute(), "en", TestContext.Current.CancellationToken);
        }

        var ai = new FakeAi();
        using var spending = new Spending(_dir, null, null, ai.Handle);
        var metered = new MeteredSpeechToText(new Fake("deepgram"), "nova-3", spending, Usd(5m), "subtitles.sync");
        await metered.TranscribeAsync(Minute(), "en", TestContext.Current.CancellationToken);
        await metered.TranscribeAsync(Minute(), "en", TestContext.Current.CancellationToken);
        await spending.SharedSummaryAsync(TestContext.Current.CancellationToken);

        var month = DateTimeOffset.Now.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        Assert.Single(ai.Ops, o => o.StartsWith("carry:", StringComparison.Ordinal));
        Assert.Equal("carry:openai-speech:0.006:" + month, ai.Ops[0]);

        // The own ledger keeps it (for falling back) and gains nothing from calls metered on Shoal AI's
        Assert.Equal(0.006m, spending.ThisMonth(Usd(5m)).Total);
    }

    [Fact]
    public void The_page_shows_Shoal_AIs_figures_while_it_keeps_the_budget()
    {
        var per = new Dictionary<string, decimal> { ["deepgram"] = 0.5m, ["anthropic"] = 1m };
        var limits = new Dictionary<string, decimal> { ["deepgram"] = 2m };
        var ok = new SpendingSummaryReply(true, "AUD", 10m, 1.5m, per, limits, "2026-09-25", true, null, null);

        var shown = SubtitlesController.SharedSummary(ok, "2026-09-01")!;
        Assert.True(shown.SetInAi);
        Assert.Equal("AUD", shown.Currency);
        Assert.Equal(10m, shown.Limit);
        Assert.Equal(1.5m, shown.Spent);
        Assert.Equal(2m, shown.ProviderLimits!["deepgram"]);
        Assert.Null(shown.Problem);

        // Installed and allowing, but not answering: still Shoal AI's budget, figures unknown
        var down = new SpendingSummaryReply(false, null, null, null, new Dictionary<string, decimal>(), new Dictionary<string, decimal>(), null, false, "Not ready.", "transient");
        var unknown = SubtitlesController.SharedSummary(down, null)!;
        Assert.True(unknown.SetInAi);
        Assert.Null(unknown.Spent);
        Assert.Equal("Not ready.", unknown.Problem);

        var notAllowed = down with { Failure = "not-allowed" };
        Assert.Null(SubtitlesController.SharedSummary(notAllowed, null));
    }

    // Stands in for Shoal AI's spending entry point
    private sealed class FakeAi
    {
        public List<string> Ops { get; } = [];

        public string? Refuse { get; init; }

        public Task<string> Handle(string json, CancellationToken cancellationToken)
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            Assert.Equal(1, r.GetProperty("version").GetInt32());
            Assert.Equal("subtitles", r.GetProperty("caller").GetString());
            var op = r.GetProperty("op").GetString();
            static string Amount(JsonElement m) => m.GetProperty("amount").GetDecimal().ToString("0.########", CultureInfo.InvariantCulture);
            switch (op)
            {
                case "reserve":
                    Ops.Add("reserve:" + r.GetProperty("provider").GetString() + ":" + Amount(r.GetProperty("estimate")));
                    return Task.FromResult(Refuse is { } f
                        ? "{\"version\":1,\"ok\":false,\"error\":\"This would go over deepgram's monthly limit.\",\"failure\":\"" + f + "\"}"
                        : "{\"version\":1,\"ok\":true,\"reservationId\":\"" + Guid.NewGuid() + "\"}");
                case "settle":
                    Ops.Add("settle:" + Amount(r.GetProperty("actual")));
                    break;
                case "carry":
                    Ops.Add("carry:" + r.GetProperty("provider").GetString() + ":" + Amount(r.GetProperty("amount")) + ":" + r.GetProperty("month").GetString());
                    break;
                case "summary":
                    return Task.FromResult("{\"version\":1,\"ok\":true,\"currency\":\"USD\",\"limit\":5,\"spent\":0,\"perProvider\":{},\"providerLimits\":{},\"ratesDate\":null,\"ratesFresh\":false}");
                default:
                    Ops.Add(op!);
                    break;
            }

            return Task.FromResult(Refuse is { } g && op == "carry" ? "{\"version\":1,\"ok\":false,\"error\":\"No.\",\"failure\":\"" + g + "\"}" : "{\"version\":1,\"ok\":true}");
        }
    }

    private sealed class Fake(string id) : ISpeechToText
    {
        public string Id => id;

        public int Calls { get; private set; }

        public Task<Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new Transcript([], "en", id, "m", samples.Length / (double)AudioFormat.SampleRate));
        }
    }
}
