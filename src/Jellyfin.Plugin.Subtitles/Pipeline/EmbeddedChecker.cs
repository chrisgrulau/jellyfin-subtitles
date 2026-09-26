using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.Audio;
using Jellyfin.Plugin.Subtitles.Cleaning;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Jellyfin.Plugin.Subtitles.Sync;

namespace Jellyfin.Plugin.Subtitles.Pipeline;

/// <summary>
/// A text subtitle track inside a video, to check.
/// </summary>
/// <param name="ItemId">The library item.</param>
/// <param name="Name">The item's name, for display.</param>
/// <param name="VideoPath">The video file.</param>
/// <param name="StreamIndex">The track's stream index in the file.</param>
/// <param name="Codec">The track's codec.</param>
/// <param name="Language">The track's language (three-letter code).</param>
/// <param name="Duration">The video's length.</param>
/// <param name="AudioStream">Which audio stream to listen to.</param>
/// <param name="Fingerprint">What identifies this version of the video (size, time, track) without reading it.</param>
public sealed record EmbeddedJob(Guid ItemId, string Name, string VideoPath, int StreamIndex, string? Codec, string Language, TimeSpan Duration, int AudioStream, string Fingerprint);

/// <summary>
/// Checks text subtitle tracks inside videos (opt-in: copying a track out reads the whole file). The video is never
/// changed: a track that is out of time gets a corrected copy added beside the video, like a found subtitle, which Undo
/// removes. A track that is in time, unclear or doesn't match the speech is only recorded, so it isn't read again until
/// the video changes.
/// </summary>
public sealed class EmbeddedChecker
{
    private readonly ResultStore _results;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Initializes a new instance of the <see cref="EmbeddedChecker"/> class.
    /// </summary>
    /// <param name="results">Where results are kept.</param>
    /// <param name="clock">Clock.</param>
    public EmbeddedChecker(ResultStore results, TimeProvider? clock = null)
    {
        _results = results ?? throw new ArgumentNullException(nameof(results));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// The fingerprint of a video's track without reading the video: its size, write time and the track's index.
    /// </summary>
    /// <param name="videoPath">The video.</param>
    /// <param name="streamIndex">The track.</param>
    /// <returns>The fingerprint.</returns>
    public static string FingerprintOf(string videoPath, int streamIndex)
    {
        var info = new FileInfo(videoPath);
        return string.Create(CultureInfo.InvariantCulture, $"embedded-{info.Length}-{info.LastWriteTimeUtc.Ticks}-{streamIndex}");
    }

    /// <summary>
    /// The result id for a track.
    /// </summary>
    /// <param name="videoPath">The video.</param>
    /// <param name="streamIndex">The track.</param>
    /// <returns>The id.</returns>
    public static string IdFor(string videoPath, int streamIndex)
        => "emb-" + ResultStore.IdFor(videoPath + "#" + streamIndex.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Whether a track needs checking: never checked, the video changed, the check failed, or an older pipeline did it.
    /// </summary>
    /// <param name="job">The track.</param>
    /// <returns><c>true</c> to check it.</returns>
    public bool NeedsCheck(EmbeddedJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        // A failure is tried again only after a while: each try reads the whole video
        return _results.Get(IdFor(job.VideoPath, job.StreamIndex)) is not { } r
            || (r.Status != ResultStatus.Added && r.Status != ResultStatus.Undone
                && (!string.Equals(r.Fingerprint, job.Fingerprint, StringComparison.Ordinal)
                    || (r.Status == ResultStatus.Failed ? _clock.GetUtcNow() - r.Time >= SubtitleFinder.SearchAgainAfter : r.Version < SubtitleProcessor.CurrentVersion)));
    }

    /// <summary>
    /// Checks a track copied out of the video and, if it is out of time, adds a corrected copy beside the video.
    /// </summary>
    /// <param name="job">The track.</param>
    /// <param name="extracted">The track's content, as copied out.</param>
    /// <param name="audio">The video's audio.</param>
    /// <param name="speech">Speech-to-text, if available.</param>
    /// <param name="policies">The settings (automatic clean-up is applied to an added copy).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result.</returns>
    public async Task<SubtitleResult> CheckAsync(EmbeddedJob job, byte[]? extracted, IAudioSource audio, ISpeechToText? speech, Policies policies, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(policies);
        var track = string.Create(CultureInfo.InvariantCulture, $"embedded track {job.StreamIndex}");
        var result = new SubtitleResult
        {
            Id = IdFor(job.VideoPath, job.StreamIndex),
            ItemId = job.ItemId,
            Name = job.Name,

            // The video itself stands in for the track (it exists for as long as the track does)
            SubtitlePath = job.VideoPath,
            Time = _clock.GetUtcNow(),
            Fingerprint = job.Fingerprint,
            Version = SubtitleProcessor.CurrentVersion,
            Origin = track + (job.Codec is null ? string.Empty : " (" + job.Codec + ")"),
        };

        var document = extracted is null ? null : SubtitleReader.Read(extracted, "track." + FfmpegSubtitleExtractor.OutputFormat(job.Codec));
        if (document is null || document.Cues.Count == 0)
        {
            return Save(result with { Status = ResultStatus.Failed, Explanation = "The " + track + " couldn't be read as text subtitles." });
        }

        var outcome = await new SyncCheck(audio, speech, refine: speech is not null, matcher: policies.Matcher)
            .RunAsync(document, job.Duration, Languages.ToTwoLetter(job.Language), cancellationToken).ConfigureAwait(false);
        var model = outcome.Model;
        var explanation = outcome.Note is null ? model.Explanation : model.Explanation + " " + outcome.Note;
        if (outcome.WrongLanguageSuspected || model.Status != SyncStatus.Corrected)
        {
            var status = outcome.WrongLanguageSuspected ? ResultStatus.WrongLanguage : model.Status == SyncStatus.InSync ? ResultStatus.InSync : ResultStatus.Unreliable;
            return Save(result with { Status = status, Stage = outcome.Stage, Confidence = model.Confidence, Explanation = "The " + track + ": " + explanation });
        }

        // Out of time: a corrected copy beside the video (the video is never changed); nothing existing is replaced
        var (cleaned, applied) = SubtitleCleaner.Clean(document.Retime(model.Map), SubtitleProcessor.AutomaticOptions(policies));
        var path = SubtitleFinder.PathFor(job.VideoPath, job.Language, false, cleaned.Format);
        if (File.Exists(path))
        {
            return Save(result with { Status = ResultStatus.Unreliable, Explanation = "The " + track + " is out of time, but a subtitle file named " + Path.GetFileName(path) + " already exists; nothing was added." });
        }

        var written = SubtitleFiles.Create(path, SubtitleWriter.ToBytes(cleaned));
        return Save(result with
        {
            Status = ResultStatus.Added,
            SubtitlePath = path,
            Fingerprint = written,
            Changed = true,
            Scale = model.Scale,
            Offset = model.Offset,
            Stage = outcome.Stage,
            Confidence = model.Confidence,
            Cleaned = applied.GroupBy(c => c.Kind.ToString()).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            Explanation = string.Create(CultureInfo.InvariantCulture, $"The {track} was out of time by {model.Offset:+0.00;-0.00} s")
                + (Math.Abs(model.Scale - 1) > 1e-9 ? string.Create(CultureInfo.InvariantCulture, $" with a frame-rate change (×{model.Scale:0.00000})") : string.Empty)
                + "; a corrected copy was added beside the video (the video is unchanged). " + model.Explanation,
        });
    }

    /// <summary>
    /// Records that a track couldn't be copied out.
    /// </summary>
    /// <param name="job">The track.</param>
    /// <param name="why">Why.</param>
    public void RecordFailure(EmbeddedJob job, string why)
    {
        ArgumentNullException.ThrowIfNull(job);
        Save(new SubtitleResult
        {
            Id = IdFor(job.VideoPath, job.StreamIndex),
            ItemId = job.ItemId,
            Name = job.Name,
            SubtitlePath = job.VideoPath,
            Time = _clock.GetUtcNow(),
            Fingerprint = job.Fingerprint,
            Version = SubtitleProcessor.CurrentVersion,
            Status = ResultStatus.Failed,
            Explanation = why,
        });
    }

    private SubtitleResult Save(SubtitleResult result)
    {
        _results.Put(result);
        return result;
    }
}
