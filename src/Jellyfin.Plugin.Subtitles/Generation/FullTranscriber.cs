using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.Audio;
using Jellyfin.Plugin.Subtitles.SpeechToText;

namespace Jellyfin.Plugin.Subtitles.Generation;

/// <summary>
/// A whole video's transcript.
/// </summary>
/// <param name="Words">The words heard, on the video's clock.</param>
/// <param name="Provider">The service that heard them.</param>
/// <param name="Model">The model it used.</param>
/// <param name="SecondsSent">How much audio was sent.</param>
/// <param name="Chunks">How many chunks were transcribed.</param>
public sealed record FullTranscript(IReadOnlyList<TranscribedWord> Words, string Provider, string Model, double SecondsSent, int Chunks);

/// <summary>
/// Transcribes a whole video: the audio is read with ffmpeg a chunk at a time (see <see cref="TranscriptChunks"/>), each
/// chunk is transcribed in the video's language, and the words are merged on the video's clock. A paid service is
/// metered for the whole video at once (see <see cref="MeteredSpeechToText.RunWholeAsync"/>): the cost of all its audio
/// is reserved before the first chunk, and what was actually sent is recorded, however the work ends.
/// </summary>
public static class FullTranscriber
{
    /// <summary>
    /// Transcribes a video.
    /// </summary>
    /// <param name="audio">The video's audio.</param>
    /// <param name="duration">The video's length.</param>
    /// <param name="speech">The speech-to-text service.</param>
    /// <param name="language">The two-letter language spoken.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The transcript.</returns>
    public static async Task<FullTranscript> TranscribeAsync(IAudioSource audio, TimeSpan duration, ISpeechToText speech, string? language, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(speech);
        var plan = TranscriptChunks.Plan(duration);
        var run = speech is MeteredSpeechToText metered
            ? await metered.RunWholeAsync(plan.Sum(c => c.Length.TotalSeconds), (inner, ct) => RunAsync(audio, plan, inner, language, ct), r => r.SecondsSent, cancellationToken).ConfigureAwait(false)
            : await RunAsync(audio, plan, speech, language, cancellationToken).ConfigureAwait(false);
        run.Error?.Throw();
        return new FullTranscript(TranscriptChunks.Merge(run.Heard), run.Provider ?? speech.Id, run.Model ?? string.Empty, run.SecondsSent, run.Heard.Count);
    }

    /// <summary>
    /// The words to build subtitles from: the service's words, with punctuation taken from its segments where the words
    /// have none (OpenAI's words come bare); or, from a service that gives segments without word times, words spread
    /// over each segment by length.
    /// </summary>
    /// <param name="transcript">What the service heard.</param>
    /// <returns>The words, in order.</returns>
    public static IReadOnlyList<TranscribedWord> WordsOf(Transcript transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        var segments = transcript.Segments;
        if (transcript.Words.Count == 0)
        {
            return FromSegments(segments);
        }

        return segments.Count == 0 || transcript.Words.Any(w => HasPunctuation(w.Text)) ? transcript.Words : Punctuate(transcript.Words, segments);
    }

    /// <summary>
    /// Words spread over each segment in proportion to their length (for services that give no word times).
    /// </summary>
    /// <param name="segments">The segments.</param>
    /// <returns>The words.</returns>
    public static IReadOnlyList<TranscribedWord> FromSegments(IReadOnlyList<TranscribedSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var words = new List<TranscribedWord>();
        foreach (var s in segments)
        {
            var tokens = s.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var total = tokens.Sum(t => t.Length + 1.0);
            var at = s.Start;
            foreach (var t in tokens)
            {
                var length = (s.End - s.Start) * (t.Length + 1.0) / total;
                words.Add(new TranscribedWord(t, at, at + (length * 0.9), null));
                at += length;
            }
        }

        return words;
    }

    /// <summary>
    /// Words given the punctuation and capitals of the segment text they fall in: each segment's words are matched in
    /// order with the segment's tokens (by letters and digits); words that don't match keep their own text.
    /// </summary>
    /// <param name="words">The bare words.</param>
    /// <param name="segments">The segments.</param>
    /// <returns>The words.</returns>
    public static IReadOnlyList<TranscribedWord> Punctuate(IReadOnlyList<TranscribedWord> words, IReadOnlyList<TranscribedSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(segments);
        var result = new List<TranscribedWord>(words);
        var next = 0;
        foreach (var s in segments)
        {
            var tokens = s.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var t = 0;
            while (next < result.Count && (result[next].Start + result[next].End) / 2 <= s.End + 0.5)
            {
                var bare = Normalise(result[next].Text);
                for (var look = t; look < Math.Min(tokens.Length, t + 3); look++)
                {
                    if (bare.Length > 0 && string.Equals(Normalise(tokens[look]), bare, StringComparison.Ordinal))
                    {
                        result[next] = result[next] with { Text = tokens[look] };
                        t = look + 1;
                        break;
                    }
                }

                next++;
            }
        }

        return result;
    }

    private static bool HasPunctuation(string text) => text.Any(c => c is '.' or ',' or '?' or '!' or ';' or ':' or '。' or '，');

    private static string Normalise(string text) => new([.. text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

    // Reads and transcribes the chunks in turn; a failure is kept in the result (with the audio sent until then) rather
    // than thrown, so a paid service is recorded for what it heard
    private static async Task<ChunkRun> RunAsync(IAudioSource audio, IReadOnlyList<AudioChunk> plan, ISpeechToText speech, string? language, CancellationToken cancellationToken)
    {
        var heard = new List<(AudioChunk Chunk, IReadOnlyList<TranscribedWord> Words)>();
        double sent = 0;
        string? provider = null, model = null;
        try
        {
            foreach (var chunk in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var samples = await audio.ReadAsync(chunk.Start, chunk.Length, cancellationToken).ConfigureAwait(false);
                var seconds = samples.Length / (double)AudioFormat.SampleRate;
                if (seconds < 0.5)
                {
                    // Past the end of the audio (the running time can be longer than the audio track)
                    continue;
                }

                var transcript = await speech.TranscribeAsync(samples, language, cancellationToken).ConfigureAwait(false);
                sent += seconds;
                provider ??= transcript.Provider;
                model ??= transcript.Model;
                heard.Add((chunk with { Length = TimeSpan.FromSeconds(Math.Min(seconds, chunk.Length.TotalSeconds)) }, WordsOf(transcript)));
            }
        }
#pragma warning disable CA1031 // Kept and rethrown by the caller once what was sent has been recorded
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new ChunkRun(heard, sent, provider, model, ExceptionDispatchInfo.Capture(ex));
        }

        return new ChunkRun(heard, sent, provider, model, null);
    }

    private sealed record ChunkRun(IReadOnlyList<(AudioChunk Chunk, IReadOnlyList<TranscribedWord> Words)> Heard, double SecondsSent, string? Provider, string? Model, ExceptionDispatchInfo? Error);
}
