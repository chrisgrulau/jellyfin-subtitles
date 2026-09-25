using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.Audio;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Jellyfin.Plugin.Subtitles.Sync;

namespace Jellyfin.Plugin.Subtitles.Pipeline;

/// <summary>
/// What checking one subtitle's timing found.
/// </summary>
/// <param name="Model">The correction to use (or <see cref="SyncStatus.InSync"/> / <see cref="SyncStatus.Unreliable"/>).</param>
/// <param name="Stage">Which stage decided: <c>line starts</c> or <c>speech-to-text</c>.</param>
/// <param name="WrongLanguageSuspected">Speech was heard but almost none of it matches the subtitle text: the subtitle may
/// be in another language, or for another version.</param>
/// <param name="Note">Anything worth saying beyond the model's own explanation (e.g. why speech-to-text wasn't used).</param>
public sealed record SyncOutcome(SyncModel Model, string Stage, bool WrongLanguageSuspected, string? Note);

/// <summary>
/// Checks one subtitle's timing: the free line-start stage first, then speech-to-text when that stage can't decide, or
/// to pin timing down more finely when speech-to-text is free (a local or built-in service).
/// </summary>
public sealed class SyncCheck
{
    /// <summary>Speech must contain at least this many words before a lack of matches means anything.</summary>
    public const int WordsHeardForLanguageCheck = 40;

    private readonly IAudioSource _audio;
    private readonly ISpeechToText? _speech;
    private readonly bool _refine;
    private readonly double _detectorLag;
    private readonly double _wordLag;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncCheck"/> class.
    /// </summary>
    /// <param name="audio">The video's audio.</param>
    /// <param name="speech">Speech-to-text, if available.</param>
    /// <param name="refine">Whether to use speech-to-text even when the first stage decided (free services only).</param>
    /// <param name="detectorLag">The line-start detector's lag (0 for synthetic test audio).</param>
    /// <param name="wordLag">The word-timing lag (0 for synthetic tests).</param>
    public SyncCheck(IAudioSource audio, ISpeechToText? speech, bool refine, double detectorLag = SyncSolver.DetectorLag, double wordLag = TranscriptAligner.WordLag)
    {
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _speech = speech;
        _refine = refine;
        _detectorLag = detectorLag;
        _wordLag = wordLag;
    }

    /// <summary>
    /// Checks a subtitle.
    /// </summary>
    /// <param name="subtitles">The subtitle.</param>
    /// <param name="duration">The video's length.</param>
    /// <param name="language">Two-letter language of the subtitle (and the audio), or <c>null</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    public async Task<SyncOutcome> RunAsync(SubtitleDocument subtitles, TimeSpan duration, string? language, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subtitles);
        var first = await new Synchroniser(_audio, _detectorLag).SolveAsync(subtitles, duration, cancellationToken).ConfigureAwait(false);
        if (_speech is null || (first.Status != SyncStatus.Unreliable && !_refine))
        {
            var why = _speech is null && first.Status == SyncStatus.Unreliable ? "No speech-to-text service is set up for a closer check." : null;
            return new SyncOutcome(first, "line starts", false, why);
        }

        try
        {
            var (_, transcripts) = await new TranscriptSynchroniser(_audio, _speech, _wordLag).SolveAsync(subtitles, duration, language, cancellationToken).ConfigureAwait(false);
            var anchors = TranscriptAligner.Anchors(subtitles, transcripts);
            var second = TranscriptAligner.Solve(anchors, _wordLag);
            if (second.Status != SyncStatus.Unreliable)
            {
                return new SyncOutcome(second, "speech-to-text", false, null);
            }

            var heard = transcripts.Sum(t => t.Transcript.Words.Count);
            var wrongLanguage = heard >= WordsHeardForLanguageCheck && anchors.Count < TranscriptAligner.MinimumAnchors;
            return new SyncOutcome(first, "line starts", wrongLanguage, wrongLanguage
                ? "Speech was heard but almost none of it matches this subtitle's text: it may be in another language, or for another version."
                : "Speech-to-text couldn't settle it either: " + second.Explanation);
        }
        catch (SpeechToTextException ex)
        {
            return new SyncOutcome(first, "line starts", false, "Speech-to-text failed: " + ex.Message);
        }
    }
}
