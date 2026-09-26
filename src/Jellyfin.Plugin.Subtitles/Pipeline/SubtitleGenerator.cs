using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Resilience;
using Jellyfin.Plugin.Subtitles.Audio;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.Generation;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Jellyfin.Plugin.Subtitles.SpeechToText.BuiltIn;

namespace Jellyfin.Plugin.Subtitles.Pipeline;

/// <summary>
/// Last-resort subtitles: for a video the finder searched without finding anything that fits in a wanted language, the
/// whole video is transcribed (with the "Full transcript" speech-to-text service) and a subtitle generated from it is
/// added beside the video as <c>&lt;video&gt;.&lt;lang&gt;.generated.srt</c>, which Jellyfin shows with the title
/// "generated". Only in the language spoken (never a translation), a few videos a night. A generated subtitle doesn't
/// count as having one, so the finder keeps searching; a real subtitle found later replaces it. Undo removes it.
/// </summary>
public sealed class SubtitleGenerator
{
    /// <summary>The prefix of the results of generating subtitles.</summary>
    public const string IdPrefix = "gen-";

    /// <summary>The word in a generated subtitle's file name, which Jellyfin shows as the track's title.</summary>
    public const string Marker = "generated";

    /// <summary>The most videos transcribed in one night that can be set.</summary>
    public const int MaxPerNight = 200;

    /// <summary>How many words an hour a transcript needs to become a subtitle (fewer is music, noise or silence).</summary>
    public const double MinWordsPerHour = 20;

    /// <summary>How many words a transcript needs at least, however short the video.</summary>
    public const int MinWords = 3;

    /// <summary>How long a failure waits before the video is transcribed again.</summary>
    public static readonly TimeSpan RetryFailedAfter = TimeSpan.FromDays(3);

    /// <summary>How long a folder that couldn't be written waits before it is tried again.</summary>
    public static readonly TimeSpan RetryCantWriteAfter = TimeSpan.FromDays(30);

    private readonly ResultStore _results;
    private readonly SubtitleFiles _files;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleGenerator"/> class.
    /// </summary>
    /// <param name="results">Where results are kept (shared with the finder).</param>
    /// <param name="files">Safe file changes (a replaced generated subtitle is copied to the originals folder first).</param>
    /// <param name="clock">Clock.</param>
    public SubtitleGenerator(ResultStore results, SubtitleFiles files, TimeProvider? clock = null)
    {
        _results = results ?? throw new ArgumentNullException(nameof(results));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Gets whether results can be recorded now (the results file is readable).</summary>
    public bool ResultsReadable => _results.Readable;

    /// <summary>Gets what is wrong with the results file, if anything.</summary>
    public string? ResultsProblem => _results.Problem;

    /// <summary>Gets whether anything has been checked or searched yet (an install that has run before counts as set up).</summary>
    public bool HasResults => _results.All().Count > 0;

    /// <summary>
    /// The result id for generating a subtitle for a video and language.
    /// </summary>
    /// <param name="videoPath">The video.</param>
    /// <param name="language">The language.</param>
    /// <returns>The id.</returns>
    public static string IdFor(string videoPath, string language) => IdPrefix + ResultStore.IdFor(videoPath + "|" + language);

    /// <summary>
    /// The file name of a generated subtitle: the video's name, the two-letter language (as for found subtitles), the
    /// word <c>generated</c> and <c>.srt</c>. Jellyfin reads the language from it and takes <c>generated</c>, which is no
    /// flag it knows, as the track's title, so players list it as "generated - English - SRT".
    /// </summary>
    /// <param name="videoPath">The video.</param>
    /// <param name="language">The language (three-letter).</param>
    /// <returns>The path.</returns>
    public static string PathFor(string videoPath, string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        var stem = Path.Combine(Path.GetDirectoryName(videoPath)!, Path.GetFileNameWithoutExtension(videoPath));
        return stem + "." + (Languages.ToTwoLetter(language) ?? language) + "." + Marker + ".srt";
    }

    /// <summary>
    /// Whether a subtitle file is one generated from a transcript (its name ends in <c>.generated</c> before the
    /// extension), whoever made it.
    /// </summary>
    /// <param name="path">The subtitle file.</param>
    /// <returns><c>true</c> if generated.</returns>
    public static bool IsGenerated(string? path)
        => !string.IsNullOrEmpty(path)
            && Path.GetFileNameWithoutExtension(path).EndsWith("." + Marker, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the video's audio is in the language a subtitle is wanted in (generating never translates): the audio
    /// stream's language tag must be that language; an audio stream without a tag is taken to be in the first wanted
    /// language.
    /// </summary>
    /// <param name="job">The video and language.</param>
    /// <param name="wanted">The wanted languages, in order.</param>
    /// <returns><c>true</c> if a subtitle can be generated in that language.</returns>
    public static bool AudioMatches(FindJob job, IReadOnlyList<string> wanted)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(wanted);
        if (Languages.ToTwoLetter(job.Language) is not { } language)
        {
            return false;
        }

        var audio = string.IsNullOrWhiteSpace(job.AudioLanguage) || job.AudioLanguage.Trim() is "und" or "unk" or "mis" or "zxx" ? null : job.AudioLanguage;
        return audio is not null
            ? string.Equals(Languages.ToTwoLetter(audio), language, StringComparison.Ordinal)
            : wanted.Count > 0 && string.Equals(Languages.ToTwoLetter(wanted[0]), language, StringComparison.Ordinal);
    }

    /// <summary>
    /// The service and model a setting names, as recorded with a result (a transcript that heard no speech is tried again
    /// only when this changes).
    /// </summary>
    /// <param name="provider">The provider id.</param>
    /// <param name="model">The model setting.</param>
    /// <returns>For example <c>builtin/base</c>.</returns>
    public static string SetupOf(string? provider, string? model)
    {
        var p = string.IsNullOrWhiteSpace(provider) ? SpeechToTextFactory.BuiltIn : provider.Trim();
        var m = p == SpeechToTextFactory.BuiltIn ? BuiltInSource.ModelName(model) ?? model?.Trim() : SpeechSelection.ModelOf(p, model);
        return p + "/" + (string.IsNullOrEmpty(m) ? "default" : m);
    }

    /// <summary>
    /// The longest one video's transcription may take: 10 minutes plus five times its length (the built-in speech-to-text
    /// on a slow CPU is the slowest), at most a day.
    /// </summary>
    /// <param name="duration">The video's length.</param>
    /// <returns>The limit.</returns>
    public static TimeSpan TimeLimit(TimeSpan duration)
        => TimeSpan.FromMinutes(Math.Min(24 * 60, 10 + (5 * Math.Max(0, duration.TotalMinutes))));

    /// <summary>
    /// Whether to generate a subtitle for a video: the finder searched and nothing fitted (and it hasn't found one since),
    /// the audio is in that language, no generated file is there, and it hasn't been generated before, except after a
    /// failure a while ago or, for a transcript that heard no speech, with another service or model.
    /// </summary>
    /// <param name="job">The video and language.</param>
    /// <param name="wanted">The wanted languages, in order.</param>
    /// <param name="setup">The service and model now (see <see cref="SetupOf"/>).</param>
    /// <returns><c>true</c> to generate.</returns>
    public bool NeedsGeneration(FindJob job, IReadOnlyList<string> wanted, string setup)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!AudioMatches(job, wanted)
            || _results.Get(SubtitleFinder.IdFor(job.VideoPath, job.Language)) is not { Status: ResultStatus.NotFound }
            || File.Exists(PathFor(job.VideoPath, job.Language)))
        {
            return false;
        }

        if (_results.Get(IdFor(job.VideoPath, job.Language)) is not { } r)
        {
            return true;
        }

        var age = _clock.GetUtcNow() - r.Time;
        return r.Status switch
        {
            ResultStatus.NoSpeech => !string.Equals(r.SpeechSetup, setup, StringComparison.Ordinal),
            ResultStatus.Failed => age >= RetryFailedAfter,
            ResultStatus.CantWrite => age >= RetryCantWriteAfter,

            // Generated (its file removed by someone), undone, or replaced: never again on its own
            _ => false,
        };
    }

    /// <summary>
    /// The videos to generate subtitles for tonight: those that need it (see <see cref="NeedsGeneration"/>), the longest
    /// without a subtitle first (by when nothing was found for them, then by path), at most <paramref name="max"/>.
    /// </summary>
    /// <param name="jobs">Videos missing a subtitle in a wanted language.</param>
    /// <param name="wanted">The wanted languages, in order.</param>
    /// <param name="setup">The service and model now.</param>
    /// <param name="max">The most to take (0 to <see cref="MaxPerNight"/>).</param>
    /// <returns>The videos, in order.</returns>
    public IReadOnlyList<FindJob> Choose(IEnumerable<FindJob> jobs, IReadOnlyList<string> wanted, string setup, int max)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        var take = Math.Clamp(max, 0, MaxPerNight);
        return take == 0 ? [] : [.. jobs.Where(j => NeedsGeneration(j, wanted, setup))
            .OrderBy(j => _results.Get(SubtitleFinder.IdFor(j.VideoPath, j.Language))?.Time ?? DateTimeOffset.MaxValue)
            .ThenBy(j => j.VideoPath, StringComparer.Ordinal)
            .ThenBy(j => j.Language, StringComparer.Ordinal)
            .Take(take)];
    }

    /// <summary>
    /// Transcribes a video and adds the generated subtitle, or records why not: no speech to speak of, a folder that
    /// can't be written, a failure (tried again after a while). The service's limits (a paid service's spending limit, a
    /// provider refusing) are thrown, so the run can stop.
    /// </summary>
    /// <param name="job">The video and language.</param>
    /// <param name="audio">The video's audio.</param>
    /// <param name="speech">The "Full transcript" speech-to-text service.</param>
    /// <param name="setup">The service and model (see <see cref="SetupOf"/>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result, or <c>null</c> when a subtitle turned up meanwhile (nothing is recorded).</returns>
    /// <exception cref="SpeechToTextException">The service refused because of a limit or its sign-in.</exception>
    public async Task<SubtitleResult?> GenerateAsync(FindJob job, IAudioSource audio, ISpeechToText speech, string setup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(speech);
        var path = PathFor(job.VideoPath, job.Language);
        var result = new SubtitleResult
        {
            Id = IdFor(job.VideoPath, job.Language),
            ItemId = job.ItemId,
            Name = job.Name,
            SubtitlePath = path,
            VideoPath = job.VideoPath,
            Time = _clock.GetUtcNow(),
            Version = SubtitleProcessor.CurrentVersion,
            SpeechSetup = setup ?? string.Empty,
        };

        if (Path.GetDirectoryName(job.VideoPath) is { } folder && !SubtitleFiles.CanWrite(folder))
        {
            return Save(result with
            {
                Status = ResultStatus.CantWrite,
                Explanation = "Jellyfin's account can't write in this video's folder (a read-only mount, or folder permissions?), so nothing was transcribed. Tried again in 30 days.",
            });
        }

        var limit = TimeLimit(job.Duration);
        FullTranscript full;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(limit);
            try
            {
                full = await FullTranscriber.TranscribeAsync(audio, job.Duration, speech, Languages.ToTwoLetter(job.Language), timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Failed(result, string.Create(CultureInfo.InvariantCulture, $"Transcribing took longer than {limit.TotalMinutes:0} minutes and was stopped."));
            }
            catch (SpeechToTextException ex) when (ex.Failure is not (FailureClass.ProviderLimit or FailureClass.Authentication))
            {
                return Failed(result, ex.Message);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
            {
                return Failed(result, "The audio couldn't be read: " + ex.Message);
            }
        }

        var service = ServiceName(full.Provider) + (string.IsNullOrEmpty(full.Model) || full.Model == "default" ? string.Empty : " (" + full.Model + ")");
        var words = TranscriptCues.SpeechWords(full.Words);
        var cues = TranscriptCues.Build(words);
        if (words.Count < Math.Max(MinWords, MinWordsPerHour * job.Duration.TotalHours) || cues.Count == 0)
        {
            return Save(result with
            {
                Status = ResultStatus.NoSpeech,
                Origin = "Full transcript by " + service,
                Explanation = string.Create(CultureInfo.InvariantCulture, $"No speech to transcribe: {service} heard {words.Count} words in the whole video (music, sound effects or silence?), so no subtitle was generated. Not tried again unless the Full transcript service or model changes."),
            });
        }

        // Found meanwhile (the search runs on its own schedule), or a file of that name appeared: leave it
        if (_results.Get(SubtitleFinder.IdFor(job.VideoPath, job.Language)) is not { Status: ResultStatus.NotFound } || File.Exists(path))
        {
            return null;
        }

        var document = new SubtitleDocument { Format = SubtitleFormat.Srt, Cues = cues };
        string fingerprint;
        try
        {
            fingerprint = SubtitleFiles.Create(path, SubtitleWriter.ToBytes(document));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed(result, "The subtitle couldn't be written: " + ex.Message);
        }

        var confidences = words.Where(w => w.Confidence is not null).Select(w => w.Confidence!.Value).ToList();
        return Save(result with
        {
            Status = ResultStatus.Generated,
            Fingerprint = fingerprint,
            Changed = true,
            Stage = "full transcript",
            Confidence = confidences.Count > 0 ? confidences.Average() : 0,
            Origin = "Full transcript by " + service,
            Examples = [.. cues.Take(5).Select(c => c.Start.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) + " " + c.Text.Replace('\n', ' '))],
            Explanation = string.Create(CultureInfo.InvariantCulture, $"No subtitle was found, so one was generated from a full transcript by {service}: {cues.Count} lines from {words.Count} words, in {full.Chunks} part{(full.Chunks == 1 ? string.Empty : "s")}. Machine-generated: names, quiet or overlapping speech may be wrong. Replaced automatically if a subtitle is found later; Undo removes it."),
        });
    }

    /// <summary>The longest nightly time budget that can be set, in hours.</summary>
    public const int MaxBudgetHours = 24;

    /// <summary>
    /// The night's time budget from the setting: 0 means no limit; otherwise 1 to 24 hours.
    /// </summary>
    /// <param name="hours">The setting.</param>
    /// <returns>The budget, or <c>null</c> for none.</returns>
    public static TimeSpan? BudgetOf(int hours) => hours <= 0 ? null : TimeSpan.FromHours(Math.Min(hours, MaxBudgetHours));

    /// <summary>
    /// Generates subtitles for the chosen videos in turn. No new video is started once <paramref name="budget"/> has
    /// passed since the run began (a video already started finishes, within its own time limit); a service limit or
    /// sign-in failure stops the run; anything else fails only that video (recorded, tried again later).
    /// </summary>
    /// <param name="jobs">The videos (see <see cref="Choose"/>).</param>
    /// <param name="audioFor">Each video's audio.</param>
    /// <param name="speech">The "Full transcript" speech-to-text service.</param>
    /// <param name="setup">The service and model (see <see cref="SetupOf"/>).</param>
    /// <param name="budget">The night's time budget, or <c>null</c> for none.</param>
    /// <param name="recorded">Told about each result recorded.</param>
    /// <param name="progress">Progress, 0 to 100.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the run did.</returns>
    public async Task<GenerationRun> RunAsync(IReadOnlyList<FindJob> jobs, Func<FindJob, IAudioSource> audioFor, ISpeechToText speech, string setup, TimeSpan? budget, Action<FindJob, SubtitleResult>? recorded, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(audioFor);
        var began = _clock.GetUtcNow();
        int generated = 0, noSpeech = 0, failed = 0;
        for (var i = 0; i < jobs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (budget is { } b && _clock.GetUtcNow() - began >= b)
            {
                return new GenerationRun(generated, noSpeech, failed, jobs.Count - i, b, null);
            }

            var job = jobs[i];
            SubtitleResult? result;
            try
            {
                result = await GenerateAsync(job, audioFor(job), speech, setup, cancellationToken).ConfigureAwait(false);
            }
            catch (SpeechToTextException ex)
            {
                // The spending limit, the provider's own limit or its sign-in: every further video would be refused too
                return new GenerationRun(generated, noSpeech, failed, jobs.Count - i, null, ex.Message);
            }
#pragma warning disable CA1031 // One odd video mustn't stop the nightly run: it is recorded as failed and tried again later
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                result = RecordFailure(job, setup, ex.GetType().Name + ": " + ex.Message);
            }

            if (result is not null)
            {
                switch (result.Status)
                {
                    case ResultStatus.Generated:
                        generated++;
                        break;
                    case ResultStatus.NoSpeech:
                        noSpeech++;
                        break;
                    default:
                        failed++;
                        break;
                }

                recorded?.Invoke(job, result);
            }

            progress?.Report(100.0 * (i + 1) / jobs.Count);
        }

        return new GenerationRun(generated, noSpeech, failed, 0, null, null);
    }

    /// <summary>
    /// Records that generating failed for a reason the run caught (it is tried again after a while).
    /// </summary>
    /// <param name="job">The video and language.</param>
    /// <param name="setup">The service and model.</param>
    /// <param name="error">What went wrong.</param>
    /// <returns>The result.</returns>
    public SubtitleResult RecordFailure(FindJob job, string setup, string error)
    {
        ArgumentNullException.ThrowIfNull(job);
        return Failed(
            new SubtitleResult
            {
                Id = IdFor(job.VideoPath, job.Language),
                ItemId = job.ItemId,
                Name = job.Name,
                SubtitlePath = PathFor(job.VideoPath, job.Language),
                VideoPath = job.VideoPath,
                Time = _clock.GetUtcNow(),
                Version = SubtitleProcessor.CurrentVersion,
                SpeechSetup = setup ?? string.Empty,
            },
            error);
    }

    /// <summary>
    /// A real subtitle was added for a video that has a generated one in the same language: the generated file is removed
    /// (a copy is kept in the originals folder) and its result says so. A generated file someone has changed since is
    /// left in place.
    /// </summary>
    /// <param name="videoPath">The video.</param>
    /// <param name="language">The language.</param>
    /// <param name="realPath">The subtitle that was added.</param>
    /// <returns>The updated result, or <c>null</c> when there was no generated subtitle.</returns>
    public SubtitleResult? ReplaceGenerated(string videoPath, string language, string realPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        if (_results.Get(IdFor(videoPath, language)) is not { Status: ResultStatus.Generated } generated)
        {
            return null;
        }

        var found = Path.GetFileName(realPath);
        string? backup = null;
        string explanation;
        try
        {
            if (!File.Exists(generated.SubtitlePath))
            {
                explanation = "Replaced by a subtitle found later (" + found + "); the generated file was already gone.";
            }
            else
            {
                var bytes = File.ReadAllBytes(generated.SubtitlePath);
                if (!string.Equals(SubtitleFiles.Fingerprint(bytes), generated.Fingerprint, StringComparison.Ordinal))
                {
                    explanation = "A subtitle was found later (" + found + "). The generated one was changed after it was added, so it was left in place; delete it if it's no longer wanted.";
                }
                else
                {
                    backup = _files.Keep(generated.SubtitlePath, bytes);
                    File.Delete(generated.SubtitlePath);
                    explanation = "Replaced by a subtitle found later (" + found + "); the generated file was removed (a copy is kept in the plugin's originals folder).";
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            explanation = "A subtitle was found later (" + found + "), but the generated one couldn't be removed: " + ex.Message;
        }

        return Save(generated with
        {
            Status = ResultStatus.Replaced,
            Changed = false,
            Backup = backup ?? generated.Backup,
            Time = _clock.GetUtcNow(),
            Examples = [],
            Explanation = explanation,
        });
    }

    /// <summary>
    /// Writes results still waiting to be saved (at the end of a run).
    /// </summary>
    public void FlushResults() => _results.Flush();

    private static string ServiceName(string provider) => provider switch
    {
        SpeechToTextFactory.BuiltIn => "the built-in speech-to-text",
        SpeechToTextFactory.Local => "the local service",
        _ => ProviderWording.NameOf(provider),
    };

    private SubtitleResult Failed(SubtitleResult result, string why)
        => Save(result with { Status = ResultStatus.Failed, Explanation = "No subtitle was generated: " + why + " Tried again in 3 days." });

    private SubtitleResult Save(SubtitleResult result)
    {
        _results.Put(result);
        return result;
    }
}

/// <summary>
/// What one night's generation did.
/// </summary>
/// <param name="Generated">Subtitles generated.</param>
/// <param name="NoSpeech">Videos with no speech to transcribe.</param>
/// <param name="Failed">Videos that failed.</param>
/// <param name="Left">Videos chosen but not started (left for the next night).</param>
/// <param name="OutOfTime">The time budget, when the run stopped because it was used up.</param>
/// <param name="StoppedBy">Why the service stopped the run (a limit or sign-in), if it did.</param>
public sealed record GenerationRun(int Generated, int NoSpeech, int Failed, int Left, TimeSpan? OutOfTime, string? StoppedBy)
{
    /// <summary>
    /// The run's summary line, for example "generated 3 subtitles; 1 video had no speech to transcribe; 0 failed; stopped
    /// after 4 h; 12 left for tomorrow".
    /// </summary>
    /// <returns>The line.</returns>
    public string Summary()
    {
        static string Videos(int n) => n == 1 ? "1 video" : n.ToString(CultureInfo.InvariantCulture) + " videos";
        var line = string.Create(CultureInfo.InvariantCulture, $"generated {Generated} subtitle{(Generated == 1 ? string.Empty : "s")}; {Videos(NoSpeech)} had no speech to transcribe; {Failed} failed");
        if (OutOfTime is { } budget)
        {
            line += string.Create(CultureInfo.InvariantCulture, $"; stopped after {budget.TotalHours:0.#} h; {Left} left for tomorrow");
        }
        else if (StoppedBy is not null)
        {
            line += string.Create(CultureInfo.InvariantCulture, $"; stopped by the service; {Left} left for tomorrow");
        }

        return line;
    }
}
