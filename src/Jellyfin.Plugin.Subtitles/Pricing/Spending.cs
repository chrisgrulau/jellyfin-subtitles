using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.Common.Costs;
using Jellyfin.Plugin.Subtitles.Configuration;

namespace Jellyfin.Plugin.Subtitles.Pricing;

/// <summary>
/// What paid services cost and how much has been spent this month: the published prices shipped with the plugin, the
/// spend ledger and the latest exchange rates, all kept in the plugin's data folder. One shared instance, so the
/// scheduled tasks and the settings page see the same spending.
/// </summary>
public sealed class Spending : IDisposable
{
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
        Ledger = new SpendLedger(Path.Combine(dataFolder, "spend.json"), clock);
        Rates = new ExchangeRateStore(Path.Combine(dataFolder, "rates.json"), clock);
        Prices = prices ?? Shipped();
    }

    /// <summary>Gets the spend ledger.</summary>
    internal SpendLedger Ledger { get; }

    /// <summary>Gets the exchange rates.</summary>
    internal ExchangeRateStore Rates { get; }

    /// <summary>Gets the prices, or <c>null</c> if the shipped table couldn't be read (paid calls then wait).</summary>
    internal PriceTable? Prices { get; }

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

    /// <inheritdoc />
    public void Dispose() => Rates.Dispose();

    private static PriceTable? Shipped()
    {
        using var stream = typeof(Spending).Assembly.GetManifestResourceStream(typeof(Spending).Namespace + ".prices.json");
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return PriceTable.Parse(reader.ReadToEnd());
    }
}
