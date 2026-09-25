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
}
