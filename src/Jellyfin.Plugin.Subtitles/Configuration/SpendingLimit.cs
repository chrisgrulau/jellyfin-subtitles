using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Subtitles.Configuration;

/// <summary>
/// What the spending settings mean. A limit of 0 is "no paid usage" (people read 0 as "spend nothing"); unlimited is a
/// separate, explicit choice.
/// </summary>
public static class SpendingLimit
{
    /// <summary>The default currency (most providers charge in US dollars).</summary>
    public const string DefaultCurrency = "USD";

    /// <summary>The default monthly limit for paid services, in the chosen currency.</summary>
    public const decimal DefaultMonthly = 5m;

    /// <summary>
    /// The effective monthly limit.
    /// </summary>
    /// <param name="monthly">The configured limit.</param>
    /// <param name="noLimit">Whether "no limit" was chosen.</param>
    /// <returns>The limit in the chosen currency (0 = no paid usage), or <c>null</c> for no limit of our own.</returns>
    public static decimal? Monthly(decimal monthly, bool noLimit) => noLimit ? null : Math.Max(0, monthly);

    /// <summary>
    /// A currency setting made safe: a supported ISO 4217 code, upper case; anything else becomes <see cref="DefaultCurrency"/>.
    /// </summary>
    /// <param name="currency">The setting.</param>
    /// <returns>The code.</returns>
    public static string NormaliseCurrency(string? currency)
        => Common.Costs.CurrencyCode.IsSupported(currency) ? Common.Costs.CurrencyCode.Normalise(currency)! : DefaultCurrency;

    /// <summary>
    /// The languages to check: the configured three-letter codes, lower case, each once; English when none are set.
    /// </summary>
    /// <param name="configured">The setting.</param>
    /// <returns>The codes.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "ISO 639-2 codes are lower case by convention, as Jellyfin stores them.")]
    public static IReadOnlyList<string> EffectiveLanguages(IEnumerable<string>? configured)
    {
        var codes = (configured ?? []).Select(c => (c ?? string.Empty).Trim().ToLowerInvariant())
            .Where(c => c.Length == 3 && c.All(char.IsAsciiLetterLower)).Distinct(StringComparer.Ordinal).ToList();
        return codes.Count > 0 ? codes : ["eng"];
    }

    /// <summary>
    /// The extra-charges percentage made safe (0 to 100).
    /// </summary>
    /// <param name="percent">The setting.</param>
    /// <returns>The percentage.</returns>
    public static decimal NormaliseExtraPercent(decimal percent) => Math.Clamp(percent, 0m, Common.Costs.CostConverter.MaxExtraPercent);

    /// <summary>
    /// Whether paid services may be used at all.
    /// </summary>
    /// <param name="limit">The effective limit (see <see cref="Monthly"/>).</param>
    /// <returns><c>false</c> when the limit is 0.</returns>
    public static bool AllowsPaidUsage(decimal? limit) => limit is null || limit > 0;

    /// <summary>
    /// Whether the built-in speech-to-text is chosen for a use that's switched on, but hasn't been allowed to download
    /// yet (the settings page then asks).
    /// </summary>
    /// <param name="tiers">The speech-to-text uses.</param>
    /// <param name="allowed">Whether the download has been allowed.</param>
    /// <returns><c>true</c> if consent is still needed.</returns>
    public static bool NeedsBuiltInConsent(IEnumerable<TranscriptionTier> tiers, bool allowed)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        return !allowed && tiers.Any(t => t is { Enabled: true } && string.Equals(t.Provider, TranscriptionTier.BuiltIn, StringComparison.OrdinalIgnoreCase));
    }
}
