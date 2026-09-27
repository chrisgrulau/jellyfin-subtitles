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
/// <para>
/// One budget page: when Shoal AI is installed and allows it, paid calls are reserved and settled on Shoal AI's ledger,
/// within the currency and limits set there (see <see cref="Meter"/>), and this plugin's own ledger and settings are only
/// the fallback (Shoal AI not installed, a different contract version, or not allowing it). What was spent on the own
/// ledger this month is reported to Shoal AI once, so its month counts it.
/// </para>
/// </summary>
public sealed class Spending : IDisposable
{
    /// <summary>This plugin's name on Shoal AI's spending entry point.</summary>
    internal const string Caller = "subtitles";

    private readonly SpendingStore _store;
    private readonly SpendingBridgeClient _bridge;
    private readonly SpendCarry _carry;

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
    /// <param name="sharedBudget">A stand-in for Shoal AI's spending entry point (tests only); by default the loaded
    /// Shoal AI's, if any.</param>
    internal Spending(string dataFolder, PriceTable? prices, TimeProvider? clock, Func<string, CancellationToken, Task<string>>? sharedBudget = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataFolder);
        _store = new SpendingStore(dataFolder, prices ?? SpendingStore.ShippedPrices(typeof(Spending).Assembly, typeof(Spending).Namespace + ".prices.json"), clock);
        _bridge = new SpendingBridgeClient(Caller, sharedBudget);
        _carry = new SpendCarry(() => _store.Ledger.ThisMonthAsCharged(), SharedProviderId, clock);
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
        return LimitsOf(config.Currency, config.MonthlyBudget, config.NoSpendingLimit, config.ExtraChargesPercent);
    }

    /// <summary>
    /// The limits from spending settings, made safe by the same rules Save applies (a supported currency or USD, a
    /// monthly limit of at least 0, extra charges from 0 to 100 %): used for saved settings and for the settings page's
    /// unsaved values (Test, SUB-29).
    /// </summary>
    /// <param name="currency">The currency setting.</param>
    /// <param name="monthly">The monthly limit.</param>
    /// <param name="noLimit">Whether "no limit" is chosen.</param>
    /// <param name="extraPercent">The extra-charges percentage.</param>
    /// <returns>The limits.</returns>
    internal static SpendLimits LimitsOf(string? currency, decimal monthly, bool noLimit, decimal extraPercent)
        => new(
            SpendingLimit.NormaliseCurrency(currency),
            SpendingLimit.Monthly(monthly, noLimit),
            new Dictionary<string, decimal>(),
            SpendingLimit.NormaliseExtraPercent(extraPercent));

    /// <summary>
    /// Gets a value indicating whether Shoal AI, with its spending entry point, is installed: paid services may then be
    /// usable whatever this plugin's own limit says, since Shoal AI's limits apply when it allows this plugin.
    /// </summary>
    internal bool SharedBudgetInstalled => _bridge.IsInstalled;

    /// <summary>
    /// Shoal AI's name for one of this plugin's paid services (OpenAI speech-to-text is <c>openai-speech</c> there, apart
    /// from OpenAI's text models).
    /// </summary>
    /// <param name="provider">This plugin's service id.</param>
    /// <returns>Shoal AI's id.</returns>
    internal static string SharedProviderId(string provider)
        => provider == SpeechToText.SpeechToTextFactory.OpenAi ? SpendingBridgeClient.OpenAiSpeech : provider;

    /// <summary>
    /// Where paid calls are metered: Shoal AI's budget when it is installed and allows this plugin, else this plugin's own
    /// ledger with <paramref name="limits"/>. A call is reserved and settled in one of them only.
    /// </summary>
    /// <param name="limits">This plugin's own limits (the fallback).</param>
    /// <returns>The meter, for one or more calls.</returns>
    internal ISpendMeter Meter(SpendLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        return new BridgedSpendMeter(_bridge, () => new LocalSpendMeter(_store.Ledger, limits, () => _store.Rates.Current), SharedProviderId, _carry);
    }

    /// <summary>
    /// Whether paid services may be used at all: always while Shoal AI is installed (its limits decide, or this plugin's own
    /// when it doesn't allow this plugin, at each call), otherwise unless this plugin's own limit is 0.
    /// </summary>
    /// <param name="limits">This plugin's own limits.</param>
    /// <returns><c>false</c> when paid services are certainly not used.</returns>
    internal bool PaidMayBeUsed(SpendLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        return _bridge.IsInstalled || SpendingLimit.AllowsPaidUsage(limits.Overall);
    }

    /// <summary>
    /// This month's spending and the limits as Shoal AI keeps them, after reporting this plugin's own spending this month
    /// (so the figures include it). A failure that means this plugin keeps its own budget (see
    /// <see cref="SpendingBridgeClient.MeansOwnBudget"/>) comes back as such.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The summary, or why there is none.</returns>
    internal async Task<SpendingSummaryReply> SharedSummaryAsync(CancellationToken cancellationToken)
    {
        await _carry.SendAsync(_bridge, cancellationToken).ConfigureAwait(false);
        return await _bridge.SummaryAsync(cancellationToken).ConfigureAwait(false);
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
