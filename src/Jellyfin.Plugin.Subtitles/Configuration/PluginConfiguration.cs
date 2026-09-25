using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Subtitles.Configuration;

/// <summary>
/// Plugin settings, persisted by Jellyfin as XML in the plugin configurations folder. Basic settings are the key
/// decisions in plain language; advanced settings give finer control and carry warnings on the settings page.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // ---- Basic ----

    /// <summary>
    /// Gets or sets a value indicating whether the plugin processes anything at all.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the subtitle languages to find and check, as ISO 639-2 codes (e.g. <c>eng</c>); empty means English
    /// (see <see cref="SpendingLimit.EffectiveLanguages"/>). The list starts empty on purpose: Jellyfin's XML loader adds
    /// saved items to whatever the list starts with, so a default item would be duplicated on every restart.
    /// </summary>
    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Jellyfin deserializes plugin configuration from JSON, which cannot populate a get-only collection.")]
    public Collection<string> Languages { get; set; } = [];

    /// <summary>
    /// Gets or sets what happens to timing corrections (shift, drift, cuts).
    /// </summary>
    public ChangePolicy TimingFixes { get; set; } = ChangePolicy.Automatic;

    /// <summary>
    /// Gets or sets what happens to changes of subtitle text (never automatic unless chosen here).
    /// </summary>
    public ChangePolicy TextChanges { get; set; } = ChangePolicy.Review;

    /// <summary>
    /// Gets or sets the clean-up settings (adverts, sound descriptions, repeated lines, overlaps, brief lines).
    /// </summary>
    public CleanupSettings Cleanup { get; set; } = new();

    /// <summary>
    /// Gets or sets the address of a local speech-to-text service with an OpenAI-compatible API (faster-whisper server,
    /// speaches, the whisper.cpp server …), e.g. <c>http://localhost:8000/v1</c>. Used by tiers set to "Local service".
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Stored as entered in the XML plugin configuration; parsed and checked where it is used.")]
    public string LocalServiceUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets speech-to-text for the short snippets used to check and synchronise subtitles.
    /// </summary>
    public TranscriptionTier SyncSnippets { get; set; } = new() { Enabled = true };

    /// <summary>
    /// Gets or sets speech-to-text for the slightly longer excerpt given to the AI plugin as context, when it is installed.
    /// </summary>
    public TranscriptionTier AiContext { get; set; } = new();

    /// <summary>
    /// Gets or sets speech-to-text of the whole video (last-resort subtitles, discrepancy checks, precise timing).
    /// </summary>
    public TranscriptionTier FullTranscript { get; set; } = new();

    /// <summary>
    /// Gets or sets the currency costs and limits are shown and set in (ISO 4217, e.g. <c>AUD</c>). Providers charge in
    /// their own currency (usually US dollars); charges are converted with the European Central Bank's daily rates.
    /// </summary>
    public string Currency { get; set; } = SpendingLimit.DefaultCurrency;

    /// <summary>
    /// Gets or sets the monthly spending limit for paid services, in <see cref="Currency"/>. 0 means no paid usage at all
    /// (cloud services are never called); "no limit" is a separate, explicit choice (<see cref="NoSpendingLimit"/>). A
    /// small cap by default, so entering an API key never means open-ended spending.
    /// </summary>
    public decimal MonthlyBudget { get; set; } = SpendingLimit.DefaultMonthly;

    /// <summary>
    /// Gets or sets a value indicating whether paid services may spend without a limit of our own (limits set with the
    /// provider still apply). Off by default; the settings page warns when it's on.
    /// </summary>
    public bool NoSpendingLimit { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the administrator has agreed to the built-in speech-to-text downloading
    /// and running its Whisper program and model on this server. Nothing is downloaded until they have, after being told
    /// what, how big and from where.
    /// </summary>
    public bool AllowBuiltInDownload { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether subtitles are searched for films and episodes that have none in a chosen
    /// language (and added once they fit the audio).
    /// </summary>
    public bool FindMissing { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a picture-based subtitle track (PGS, VobSub) counts as having subtitles in
    /// its language. Forced-only tracks (signs and foreign-language parts) never count.
    /// </summary>
    public bool CountImageSubtitles { get; set; } = true;

    // ---- Advanced ----

    /// <summary>Gets or sets how many videos one search run looks for subtitles for at most.</summary>
    public int MaxFindsPerRun { get; set; } = 20;

    /// <summary>
    /// Gets or sets how many subtitles may be downloaded per day, across runs. Providers such as OpenSubtitles count
    /// downloads against the account's daily allowance (a free OpenSubtitles account gets about 20), which manual
    /// downloads in Jellyfin share; keep this well below it.
    /// </summary>
    public int MaxDownloadsPerDay { get; set; } = 10;

    /// <summary>
    /// Gets or sets how many subtitle files one run checks at most (the rest are checked on following runs).
    /// </summary>
    public int MaxSubtitlesPerRun { get; set; } = 50;

    /// <summary>
    /// Gets or sets a percentage added to every provider charge, for taxes on overseas services (such as GST) or a card's
    /// foreign-transaction fee, so limits match what is actually paid. 0 to 100; 0 by default.
    /// </summary>
    public decimal ExtraChargesPercent { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether confidence thresholds are tuned automatically against subtitles already
    /// known to be good.
    /// </summary>
    public bool SelfCalibration { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether agreement between independent sources decides a disagreement on its own;
    /// when off, such disagreements go to review.
    /// </summary>
    public bool AgreementDecides { get; set; } = true;
}
