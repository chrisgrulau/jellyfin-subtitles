using System;
using System.Collections.Generic;
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
public sealed record SyncOutcome(SyncModel Model, string Stage, bool WrongLanguageSuspected, string? Note)
{
    /// <summary>Gets examples of the lines that decided a timing matched by meaning (heard phrase and subtitle line).</summary>
    public IReadOnlyList<string> Pairs { get; init; } = [];

    /// <summary>Gets the stretches speech-to-text transcribed (empty when it didn't run), for an audit of the wording.</summary>
    public IReadOnlyList<(double Start, Transcript Transcript)> Transcripts { get; init; } = [];
}

/// <summary>
/// Checks one subtitle's timing: the free line-start stage first, then speech-to-text when that stage can't decide, or
/// to pin timing down more finely when speech-to-text is free (a local or built-in service).
/// </summary>
public sealed class SyncCheck
{
    /// <summary>Speech must contain at least this many words before a lack of matches means anything.</summary>
    public const int WordsHeardForLanguageCheck = 40;

    /// <summary>The stage name when lines were matched by meaning (the wording differs, so it isn't audited).</summary>
    public const string ByMeaningStage = "lines by meaning";

    private readonly IAudioSource _audio;
    private readonly ISpeechToText? _speech;
    private readonly bool _refine;
    private readonly double _detectorLag;
    private readonly double _wordLag;
    private readonly ILineMatcher? _matcher;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncCheck"/> class.
    /// </summary>
    /// <param name="audio">The video's audio.</param>
    /// <param name="speech">Speech-to-text, if available.</param>
    /// <param name="refine">Whether to use speech-to-text even when the first stage decided (free services only).</param>
    /// <param name="detectorLag">The line-start detector's lag (0 for synthetic test audio).</param>
    /// <param name="wordLag">The word-timing lag (0 for synthetic tests).</param>
    /// <param name="matcher">Pairs heard phrases with subtitle lines by meaning, when exact words can't settle it (optional).</param>
    public SyncCheck(IAudioSource audio, ISpeechToText? speech, bool refine, double detectorLag = SyncSolver.DetectorLag, double wordLag = TranscriptAligner.WordLag, ILineMatcher? matcher = null)
    {
        _matcher = matcher;
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
                return new SyncOutcome(second, "speech-to-text", false, null) { Transcripts = transcripts };
            }

            var heard = transcripts.Sum(t => t.Transcript.Words.Count);
            var byMeaning = string.Empty;
            if (_matcher is not null && heard >= WordsHeardForLanguageCheck)
            {
                var (outcome, note) = await ByMeaningAsync(subtitles, transcripts, first, language, cancellationToken).ConfigureAwait(false);
                if (outcome is not null)
                {
                    return outcome with { Transcripts = transcripts };
                }

                byMeaning = note;
            }

            var wrongLanguage = heard >= WordsHeardForLanguageCheck && anchors.Count < TranscriptAligner.MinimumAnchors;
            return new SyncOutcome(first, "line starts", wrongLanguage, (wrongLanguage
                ? "Speech was heard but almost none of it matches this subtitle's text: it may be in another language, for another version, or not dialogue at all (commentary, storyboard or trivia notes). Left unchanged."
                : "Speech-to-text couldn't settle it either: " + second.Explanation) + byMeaning) { Transcripts = transcripts };
        }
        catch (SpeechToTextException ex)
        {
            return new SyncOutcome(first, "line starts", false, "Speech-to-text failed: " + ex.Message);
        }
    }

    // Stage 3: exact words didn't line up, so the matcher pairs what was heard with the subtitle lines by meaning. Its
    // pairs must still agree on one timing; "different" confirms the subtitles are for something else.
    private async Task<(SyncOutcome? Outcome, string Note)> ByMeaningAsync(SubtitleDocument subtitles, IReadOnlyList<(double Start, Transcript Transcript)> transcripts, SyncModel first, string? language, CancellationToken ct)
    {
        var phrases = MeaningAligner.Phrases(transcripts);
        var cues = MeaningAligner.Cues(subtitles, transcripts.Select(t => (t.Start, TranscriptSynchroniser.SnippetLength.TotalSeconds)));
        if (phrases.Count < MeaningAligner.MinimumPairs || cues.Count == 0)
        {
            return (null, string.Empty);
        }

        var match = await _matcher!.MatchAsync(phrases, cues, language, ct).ConfigureAwait(false);
        var by = match.By ?? "the line matcher";
        switch (match.Verdict)
        {
            case LineVerdict.Different:
                return (new SyncOutcome(first, ByMeaningStage, true, $"Compared by meaning ({by}): these subtitles don't say what is said. {match.Note} Left unchanged."), string.Empty);
            case LineVerdict.SameContent:
                var model = MeaningAligner.Solve(MeaningAligner.Anchors(phrases, cues, match.Pairs), _wordLag);
                if (model.Status != SyncStatus.Unreliable)
                {
                    var shown = match.Pairs
                        .Where(p => p.Phrase >= 0 && p.Phrase < phrases.Count && p.Cue >= 0 && p.Cue < cues.Count)
                        .Take(5)
                        .Select(p => $"Matched by meaning: heard \u201c{phrases[p.Phrase].Text}\u201d = line at {TimeSpan.FromSeconds(cues[p.Cue].Start):h\\:mm\\:ss} \u201c{cues[p.Cue].Text}\u201d")
                        .ToList();
                    return (new SyncOutcome(model, ByMeaningStage, false, $"Lines matched by meaning ({by}), as the wording differs from what is said. {match.Note}".TrimEnd()) { Pairs = shown }, string.Empty);
                }

                return (null, $" Compared by meaning ({by}): the same content, but the matched lines don't agree on one timing.");
            default:
                return (null, match.Note.Length > 0 ? " " + match.Note : string.Empty);
        }
    }
}
