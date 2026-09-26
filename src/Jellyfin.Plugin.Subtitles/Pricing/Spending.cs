using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Costs;
using Jellyfin.Plugin.Subtitles.Configuration;

namespace Jellyfin.Plugin.Subtitles.Pricing;

/// <summary>
/// What paid services cost and how much has been spent this month: the shared <see cref="SpendingStore"/> (the published
/// prices shipped with the plugin, the spend ledger and the latest exchange rates, all kept in the plugin's data folder),
/// with this plugin's limits. One shared instance, so the scheduled tasks and the settings page see the same spending.
/// </summary>
public sealed class Spending : IDisposable
{
    private readonly SpendingStore _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="Spending"/> class.
    /// </summary>
    /// <param name="dataFolder">The plugin's data folder.</param>
    public Spending(string dataFolder)
        : this(dataFolder, null, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Spending"/> class.
    /// </summary>
    /// <param name="dataFolder">The plugin's data folder.</param>
    /// <param name="prices">The price table (for tests); by default the one shipped with the plugin.</param>
    /// <param name="clock">Clock.</param>
    internal Spending(string dataFolder, PriceTable? prices, TimeProvider? clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataFolder);
        _store = new SpendingStore(dataFolder, prices ?? SpendingStore.ShippedPrices(typeof(Spending).Assembly, typeof(Spending).Namespace + ".prices.json"), clock);
    }

    /// <summary>Gets the currencies the settings page offers.</summary>
    public static IReadOnlyList<string> Currencies => SpendingStore.Currencies;

    /// <summary>Gets the spend ledger.</summary>
    internal SpendLedger Ledger => _store.Ledger;

    /// <summary>Gets the exchange rates.</summary>
    internal ExchangeRateStore Rates => _store.Rates;

    /// <summary>Gets the prices, or <c>null</c> if the shipped table couldn't be read (paid calls then wait).</summary>
    internal PriceTable? Prices => _store.Prices;

    /// <summary>
    /// The limits from the settings.
    /// </summary>
    /// <param name="config">Plugin settings.</param>
    /// <returns>The limits.</returns>
    internal static SpendLimits LimitsOf(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new SpendLimits(
            SpendingLimit.NormaliseCurrency(config.Currency),
            SpendingLimit.Monthly(config.MonthlyBudget, config.NoSpendingLimit),
            new Dictionary<string, decimal>(),
            SpendingLimit.NormaliseExtraPercent(config.ExtraChargesPercent));
    }

    /// <summary>
    /// The exchange rates to check a paid call against, refreshed first when due (see
    /// <see cref="SpendingStore.CurrentRatesAsync"/>). Never throws for network or content problems.
    /// </summary>
    /// <param name="http">HTTP client.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The latest good rates, if any.</returns>
    internal Task<ExchangeRates?> CurrentRatesAsync(HttpClient http, CancellationToken cancellationToken) => _store.CurrentRatesAsync(http, cancellationToken);

    /// <summary>
    /// This month's spending in the user's currency, open reservations included, at the latest rates.
    /// </summary>
    /// <param name="limits">The limits.</param>
    /// <returns>The spending.</returns>
    internal MonthSpend ThisMonth(SpendLimits limits) => _store.ThisMonth(limits);

    /// <inheritdoc />
    public void Dispose() => _store.Dispose();
}
