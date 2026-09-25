using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.Audio;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.SpeechToText;

namespace Jellyfin.Plugin.Subtitles.Sync;

/// <summary>
/// Lines subtitles up using speech-to-text: a few one-minute snippets are transcribed, chosen where the subtitles have
/// the most dialogue and spread across the video, and their words are matched against the subtitle text
/// (<see cref="TranscriptAligner"/>).
/// </summary>
public sealed class TranscriptSynchroniser
{
    /// <summary>How many snippets are transcribed.</summary>
    public const int Snippets = 3;

    /// <summary>How long each snippet is.</summary>
    public static readonly TimeSpan SnippetLength = TimeSpan.FromMinutes(1);

    private readonly IAudioSource _audio;
    private readonly ISpeechToText _speech;
    private readonly double _wordLag;

    /// <summary>
    /// Initializes a new instance of the <see cref="TranscriptSynchroniser"/> class.
    /// </summary>
    /// <param name="audio">The video's audio.</param>
    /// <param name="speech">Speech-to-text.</param>
    /// <param name="wordLag">The lag to subtract (<see cref="TranscriptAligner.WordLag"/> for real transcripts).</param>
    public TranscriptSynchroniser(IAudioSource audio, ISpeechToText speech, double wordLag = TranscriptAligner.WordLag)
    {
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _speech = speech ?? throw new ArgumentNullException(nameof(speech));
        _wordLag = wordLag;
    }

    /// <summary>
    /// Picks where to transcribe: among one-minute slots between 8 % and 92 % of the video, the ones where the subtitles
    /// have the most words, at least a tenth of the video apart.
    /// </summary>
    /// <param name="subtitles">The subtitles.</param>
    /// <param name="duration">The video's length.</param>
    /// <returns>Snippet start times, in order.</returns>
    public static IReadOnlyList<TimeSpan> Plan(SubtitleDocument subtitles, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(subtitles);
        var length = SnippetLength.TotalSeconds;
        var first = duration.TotalSeconds * 0.08;
        var last = (duration.TotalSeconds * 0.92) - length;
        if (last <= first)
        {
            return duration.TotalSeconds >= 20 ? [TimeSpan.Zero] : [];
        }

        var slots = new List<(double Start, int Words)>();
        for (var s = first; s <= last; s += length / 2)
        {
            var words = subtitles.Cues.Where(c => c.Start.TotalSeconds >= s && c.Start.TotalSeconds < s + length && SpeechTimeline.IsSpeech(c.Text))
                .Sum(c => SubtitleMarkup.ToPlainText(c.Text).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
            slots.Add((s, words));
        }

        var chosen = new List<double>();
        foreach (var slot in slots.OrderByDescending(x => x.Words))
        {
            if (chosen.Count == Snippets)
            {
                break;
            }

            if (chosen.All(c => Math.Abs(c - slot.Start) >= duration.TotalSeconds * 0.1))
            {
                chosen.Add(slot.Start);
            }
        }

        return [.. chosen.Order().Select(TimeSpan.FromSeconds)];
    }

    /// <summary>
    /// Works out the timing correction.
    /// </summary>
    /// <param name="subtitles">The subtitles.</param>
    /// <param name="duration">The video's length.</param>
    /// <param name="language">Two-letter language of the audio, or <c>null</c> to detect.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The correction and the transcripts (to cache and reuse).</returns>
    public async Task<(SyncModel Model, IReadOnlyList<(double Start, Transcript Transcript)> Transcripts)> SolveAsync(SubtitleDocument subtitles, TimeSpan duration, string? language, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subtitles);
        var transcripts = new List<(double, Transcript)>();
        foreach (var start in Plan(subtitles, duration))
        {
            var samples = await _audio.ReadAsync(start, SnippetLength, cancellationToken).ConfigureAwait(false);
            transcripts.Add((start.TotalSeconds, await _speech.TranscribeAsync(samples, language, cancellationToken).ConfigureAwait(false)));
        }

        return (TranscriptAligner.Solve(TranscriptAligner.Anchors(subtitles, transcripts), _wordLag), transcripts);
    }
}
