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
    /// Gets or sets the subtitle languages to find and check, as ISO 639-2 codes (e.g. <c>eng</c>).
    /// </summary>
    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Jellyfin deserializes plugin configuration from JSON, which cannot populate a get-only collection.")]
    public Collection<string> Languages { get; set; } = ["eng"];

    /// <summary>
    /// Gets or sets what happens to timing corrections (shift, drift, cuts).
    /// </summary>
    public ChangePolicy TimingFixes { get; set; } = ChangePolicy.Automatic;

    /// <summary>
    /// Gets or sets what happens to changes of subtitle text (never automatic unless chosen here).
    /// </summary>
    public ChangePolicy TextChanges { get; set; } = ChangePolicy.Review;

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
    /// Gets or sets the monthly spending limit for paid services in US dollars; 0 means no limit of our own (limits set
    /// with the provider still apply).
    /// </summary>
    public decimal MonthlyBudgetUsd { get; set; }

    // ---- Advanced ----

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
