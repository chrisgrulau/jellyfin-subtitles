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
    /// Gets or sets the subtitle languages to find and check, as ISO 639-2 codes (e.g. <c>eng</c>); empty means each
    /// library's own subtitle download languages, then the server's preferred metadata language, then English (see
    /// <see cref="LanguageSettings.Choose"/>). The list starts empty on purpose: Jellyfin's XML loader adds
    /// saved items to whatever the list starts with, so a default item would be duplicated on every restart.
    /// </summary>
    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Jellyfin deserializes plugin configuration from JSON, which cannot populate a get-only collection.")]
    public Collection<string> Languages { get; set; } = [];

    /// <summary>
    /// Gets or sets the ids of the libraries the plugin leaves alone (none by default: every film and show library is
    /// worked on). Stored as left out rather than chosen, so a library added later is worked on until it is unticked
    /// (see <see cref="Pipeline.LibraryScope"/>). Starts empty for the same reason as <see cref="Languages"/>.
    /// </summary>
    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Jellyfin deserializes plugin configuration from JSON, which cannot populate a get-only collection.")]
    public Collection<string> ExcludedLibraries { get; set; } = [];

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
    /// Gets or sets speech-to-text for the slightly longer excerpt given to the AI plugin as context, when it is installed
    /// (including the couple of minutes Ingest may ask for, when <see cref="AllowIngest"/> is on).
    /// </summary>
    public TranscriptionTier AiContext { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether the Ingest plugin may ask for a short transcript (at most three minutes of
    /// a video it is filing), to tell which episode a file is. Uses the "Context for AI decisions" service.
    /// </summary>
    public bool AllowIngest { get; set; }

    /// <summary>
    /// Gets or sets speech-to-text of the whole video: used to generate subtitles when none can be found (see
    /// <see cref="GenerateMissing"/>), with its own service and model (the built-in one by default, which is free; a paid
    /// service is kept within the spending limits).
    /// </summary>
    public TranscriptionTier FullTranscript { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether a subtitle is generated from a full transcript for a video the search found
    /// nothing fitting for, in a wanted language that is also the audio's language (off by default). Generated subtitles
    /// are labelled as such, don't stop the search for a real one, are replaced when one is found, and can be undone.
    /// Needs the "Full transcript" speech-to-text switched on.
    /// </summary>
    public bool GenerateMissing { get; set; }

    /// <summary>
    /// Gets or sets how many videos are transcribed to generate subtitles per night at most (0 to 200; 20 by default).
    /// </summary>
    public int MaxGeneratedPerNight { get; set; } = 20;

    /// <summary>
    /// Gets or sets how many hours after the nightly full-transcript run starts no new video is started (0 to 24; 4 by
    /// default; 0 means no limit), shared by generating subtitles and checking whole files. A video already being
    /// transcribed finishes, within its own time limit.
    /// </summary>
    public int MaxGenerateHours { get; set; } = 4;

    /// <summary>
    /// Gets or sets a value indicating whether doubtful subtitles (an unclear timing, a timing settled by few agreeing
    /// words, or wording the audit flagged) are compared whole with a full transcript of their video, a few a night, and
    /// lines that differ (missing, extra, or differing in names, numbers, negations or words) wait for review. Off by
    /// default. Files picked in the results with <b>Check whole file</b> are compared either way. Needs the "Full
    /// transcript" speech-to-text switched on.
    /// </summary>
    public bool CheckWholeFile { get; set; }

    /// <summary>
    /// Gets or sets how many subtitle files are compared whole per night at most (0 to 200; 5 by default), those picked in
    /// the results first.
    /// </summary>
    public int MaxWholeFileChecksPerNight { get; set; } = 5;

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
    /// Gets or sets a value indicating whether text subtitle tracks inside videos are checked too (off by default:
    /// copying a track out reads the whole video file). Only tracks in a language with no subtitle file beside the video
    /// are checked; one that is out of time gets a corrected copy added beside the video.
    /// </summary>
    public bool CheckEmbeddedSubtitles { get; set; }

    /// <summary>Gets or sets how many embedded tracks one run checks at most.</summary>
    public int MaxEmbeddedPerRun { get; set; } = 10;

    /// <summary>
    /// Gets or sets which key reads Deepgram's credit balance for the settings page (off by default). Reading it needs an
    /// Admin or Owner key; a separate key keeps the transcription key limited.
    /// </summary>
    public BalanceSource DeepgramBalance { get; set; } = BalanceSource.Off;

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

    /// <summary>
    /// Gets or sets a value indicating whether videos are handled soon after they are added to the library (on by
    /// default): once nothing new has been added for <see cref="NewItemsDelayMinutes"/>, their subtitle files are checked
    /// and missing subtitles searched for, as the nightly tasks would, within the same limits. A new subtitle file beside
    /// a video is checked the same way. Generating subtitles stays nightly. Like everything else, nothing happens on a
    /// new install until the settings page has been saved once.
    /// </summary>
    public bool HandleNewItems { get; set; } = true;

    /// <summary>
    /// Gets or sets how many minutes after the last video was added new videos are handled (1 to 1440; 10 by default),
    /// so a season being filed is handled in one go.
    /// </summary>
    public int NewItemsDelayMinutes { get; set; } = 10;

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
    /// Gets or sets a value indicating whether the confidence below which the whole-file check doesn't trust what was
    /// heard is tuned automatically, per speech-to-text service and model, from subtitles already known to be good (off
    /// by default). A tuned threshold is only ever stricter than the documented starting point (see
    /// <see cref="Discrepancy.ConfidenceCalibration"/>).
    /// </summary>
    public bool TuneConfidence { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether agreement between independent sources decides a disagreement on its own;
    /// when off, such disagreements go to review. Not used yet (coming later); the settings page shows it disabled.
    /// </summary>
    public bool AgreementDecides { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the family's AI plugin is asked when the audio check can't decide because
    /// the subtitle's wording differs from what is said (a translation, a paraphrase, dense dialogue). It pairs heard
    /// phrases with subtitle lines by meaning; the pairs must still agree on one timing. Only if the AI plugin is
    /// installed and allows Subtitles; its spending limits apply.
    /// </summary>
    public bool UseAi { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the settings page has been saved at least once. Until then (on a new
    /// install) nothing is checked, changed or downloaded (SUB-19).
    /// </summary>
    public bool SetupSaved { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether subtitles waiting for review, folders that can't be written, stopped
    /// searches and added subtitles are also written to Jellyfin's Activity log (at most once a day each).
    /// </summary>
    public bool WriteToActivityLog { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether, with <see cref="UseAi"/>, the wording of a subtitle whose timing is settled
    /// is compared with what is said: lines whose meaning differs (names, numbers, negations, missing words) are flagged,
    /// with suggested wording that waits for review (never applied on its own).
    /// </summary>
    public bool AuditWording { get; set; } = true;

    /// <summary>
    /// Gets or sets how many subtitles checked before the wording audit existed are audited per run, with
    /// <see cref="AuditWording"/> (0 = none). They use what is left of the run's AI checks.
    /// </summary>
    public int MaxAuditsOfEarlierPerRun { get; set; } = 5;

    /// <summary>
    /// Gets or sets the most AI checks in one run of a task (matching lines and audits together).
    /// </summary>
    public int MaxAiChecksPerRun { get; set; } = 20;

    /// <summary>
    /// The policies these settings set for what happens to changes (the AI plugin's help is added per run, see
    /// <see cref="Pipeline.RunStart.PoliciesFor"/>).
    /// </summary>
    /// <returns>The policies.</returns>
    public Pipeline.Policies Policies() => new(TimingFixes, TextChanges, Cleanup ?? new CleanupSettings());
}
