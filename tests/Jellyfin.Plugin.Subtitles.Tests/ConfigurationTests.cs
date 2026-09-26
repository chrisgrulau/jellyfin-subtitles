using System;
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

    // FAM-06: the page offers the currencies the server sends with the spending, not a copy of its own
    [Fact]
    public void The_settings_page_offers_the_servers_currencies()
    {
        var page = Page();

        Assert.Contains("s.Currencies", page, StringComparison.Ordinal);
        Assert.DoesNotMatch("'AUD'|'EUR'|'JPY'", page);
    }

    private static string Page()
    {
        using var stream = typeof(SpendingLimit).Assembly.GetManifestResourceStream("Jellyfin.Plugin.Subtitles.Configuration.configPage.html")!;
        using var reader = new System.IO.StreamReader(stream);
        return reader.ReadToEnd();
    }

    // FAM-07: environment and accessibility
    [Fact]
    public void The_settings_page_works_without_a_clipboard_and_announces_status()
    {
        var page = Page();
        Assert.Contains("role=\"status\" aria-live=\"polite\"", page, StringComparison.Ordinal);
        Assert.Contains("split(/[\\\\/]/)", page, StringComparison.Ordinal);
        Assert.Contains("navigator.clipboard.writeText(text).then(done, manual); } else { manual(); }", page, StringComparison.Ordinal);
        Assert.Contains("<div class=\"subs-scroll\"><table id=\"Results\"", page, StringComparison.Ordinal);

        // Key-save failures show the server's own message, not a guess
        Assert.DoesNotContain("Dashboard.alert(\"That doesn't look like an API key.\")", page, StringComparison.Ordinal);
    }

    // FAM-08: settings that do nothing yet are shown disabled and never saved from the page
    [Fact]
    public void Settings_that_do_nothing_yet_are_disabled_and_not_saved()
    {
        var page = Page();
        Assert.Contains("id=\"SelfCalibration\" type=\"checkbox\" is=\"emby-checkbox\" disabled", page, StringComparison.Ordinal);
        Assert.Contains("id=\"AgreementDecides\" type=\"checkbox\" is=\"emby-checkbox\" disabled", page, StringComparison.Ordinal);
        Assert.DoesNotContain("config.SelfCalibration =", page, StringComparison.Ordinal);
        Assert.DoesNotContain("config.AgreementDecides =", page, StringComparison.Ordinal);
        Assert.Contains("['FullTranscript', 'Full transcript (coming later)'", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Languages_are_deduplicated_and_default_to_english()
    {
        Assert.Equal(["eng"], LanguageSettings.EffectiveLanguages(null));
        Assert.Equal(["eng"], LanguageSettings.EffectiveLanguages(["eng", "ENG", " eng "]));
        Assert.Equal(["fre", "eng"], LanguageSettings.EffectiveLanguages(["fre", "eng", "bogus", "fre"]));
    }

    // FAM-01: codes in any form and names are accepted without the server's culture data; unknown entries are reported
    [Fact]
    public void Languages_may_be_written_as_codes_or_names()
    {
        Assert.Equal(["fre", "deu", "swe"], LanguageSettings.EffectiveLanguages(["fre", "German", "sv", "fre"]));
        Assert.Equal(["eng"], LanguageSettings.EffectiveLanguages(["Elvish"]));
        Assert.Equal(["Elvish"], LanguageSettings.UnknownLanguages(["eng", "Elvish", " "]));
    }
}
