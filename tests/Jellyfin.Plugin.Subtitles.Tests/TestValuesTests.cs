using Jellyfin.Plugin.Subtitles.Api;
using Jellyfin.Plugin.Subtitles.Pricing;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// SUB-29: Test uses the page's unsaved spending values, made safe as Save does
public sealed class TestValuesTests
{
    [Fact]
    public void The_page_values_are_used_before_Save()
    {
        var request = new TestRequest { Provider = "deepgram", Currency = "eur", MonthlyBudget = 12.5m, NoSpendingLimit = false, ExtraChargesPercent = 10m };
        var limits = request.Limits("USD", 0m, false, 0m);

        Assert.Equal("EUR", limits.Currency);
        Assert.Equal(12.5m, limits.Overall);
        Assert.Equal(10m, limits.ExtraPercent);
    }

    [Fact]
    public void Values_not_sent_come_from_the_saved_settings()
    {
        var limits = new TestRequest { Provider = "openai" }.Limits("AUD", 7m, false, 5m);

        Assert.Equal("AUD", limits.Currency);
        Assert.Equal(7m, limits.Overall);
        Assert.Equal(5m, limits.ExtraPercent);
    }

    [Fact]
    public void Values_are_made_safe_as_Save_does()
    {
        var limits = new TestRequest { Currency = "not a currency", MonthlyBudget = -3m, ExtraChargesPercent = 500m }.Limits("AUD", 7m, false, 0m);

        Assert.Equal("USD", limits.Currency);
        Assert.Equal(0m, limits.Overall);
        Assert.Equal(100m, limits.ExtraPercent);
    }

    [Fact]
    public void An_unsaved_limit_of_zero_stops_paid_tests_and_no_limit_allows_them()
    {
        Assert.False(Configuration.SpendingLimit.AllowsPaidUsage(new TestRequest { MonthlyBudget = 0m }.Limits("USD", 5m, false, 0m).Overall));
        Assert.Null(new TestRequest { NoSpendingLimit = true }.Limits("USD", 0m, false, 0m).Overall);
        Assert.Equal(Spending.LimitsOf("usd", 5m, false, 0m).Overall, new TestRequest().Limits("USD", 5m, false, 0m).Overall);
    }
}
