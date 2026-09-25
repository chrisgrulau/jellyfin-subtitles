using System.Linq;
using Jellyfin.Plugin.Subtitles.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// PluginConfiguration derives from a Jellyfin type that isn't loadable in unit tests; its plain parts are tested here.
public class ConfigurationTests
{
    [Fact]
    public void A_transcription_tier_is_off_and_built_in_by_default()
    {
        var tier = new TranscriptionTier();
        Assert.False(tier.Enabled);
        Assert.Equal(TranscriptionTier.BuiltIn, tier.Provider);
        Assert.Empty(tier.Model);
    }

    [Fact]
    public void Automatic_is_the_first_change_policy()
        => Assert.Equal(ChangePolicy.Automatic, default(ChangePolicy));

    [Fact]
    public void Zero_means_no_paid_usage_and_unlimited_is_explicit()
    {
        Assert.Equal(0m, SpendingLimit.Monthly(0m, noLimit: false));
        Assert.False(SpendingLimit.AllowsPaidUsage(SpendingLimit.Monthly(0m, noLimit: false)));
        Assert.Equal(5m, SpendingLimit.Monthly(5m, noLimit: false));
        Assert.True(SpendingLimit.AllowsPaidUsage(5m));
        Assert.Null(SpendingLimit.Monthly(5m, noLimit: true));
        Assert.True(SpendingLimit.AllowsPaidUsage(null));
        Assert.Equal(0m, SpendingLimit.Monthly(-3m, noLimit: false));
    }

    [Fact]
    public void The_default_limit_is_a_small_cap()
        => Assert.InRange(SpendingLimit.DefaultMonthly, 0.01m, 10m);

    [Fact]
    public void Built_in_speech_to_text_needs_consent_only_when_it_would_be_used()
    {
        var builtIn = new TranscriptionTier { Enabled = true };
        var off = new TranscriptionTier { Enabled = false };
        var cloud = new TranscriptionTier { Enabled = true, Provider = "deepgram" };

        Assert.True(SpendingLimit.NeedsBuiltInConsent([builtIn, off], allowed: false));
        Assert.False(SpendingLimit.NeedsBuiltInConsent([builtIn], allowed: true));
        Assert.False(SpendingLimit.NeedsBuiltInConsent([off, cloud], allowed: false));
    }

    [Theory]
    [InlineData("aud", "AUD")]
    [InlineData(" EUR ", "EUR")]
    [InlineData("XYZ", "USD")]
    [InlineData("A$", "USD")]
    [InlineData(null, "USD")]
    public void Currency_settings_are_made_safe(string? input, string expected)
        => Assert.Equal(expected, SpendingLimit.NormaliseCurrency(input));

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(10, 10)]
    [InlineData(250, 100)]
    public void Extra_charges_stay_between_0_and_100_percent(int input, int expected)
        => Assert.Equal(expected, SpendingLimit.NormaliseExtraPercent(input));

    [Fact]
    public void The_settings_page_offers_exactly_the_supported_currencies()
    {
        using var stream = typeof(SpendingLimit).Assembly.GetManifestResourceStream("Jellyfin.Plugin.Subtitles.Configuration.configPage.html")!;
        using var reader = new System.IO.StreamReader(stream);
        var page = reader.ReadToEnd();
        var list = System.Text.RegularExpressions.Regex.Match(page, @"var currencies = \[(?<list>[^\]]*)\]").Groups["list"].Value;
        var offered = System.Text.RegularExpressions.Regex.Matches(list, "'([A-Z]{3})'").Select(m => m.Groups[1].Value);

        Assert.Equal(Common.Costs.CurrencyCode.Supported, offered);
    }

    [Fact]
    public void Languages_are_deduplicated_and_default_to_english()
    {
        Assert.Equal(["eng"], SpendingLimit.EffectiveLanguages(null));
        Assert.Equal(["eng"], SpendingLimit.EffectiveLanguages(["eng", "ENG", " eng "]));
        Assert.Equal(["fre", "eng"], SpendingLimit.EffectiveLanguages(["fre", "eng", "bogus", "fre"]));
    }
}
