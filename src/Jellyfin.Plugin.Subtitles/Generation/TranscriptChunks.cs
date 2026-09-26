using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Subtitles.SpeechToText;

namespace Jellyfin.Plugin.Subtitles.Generation;

/// <summary>
/// A stretch of a video's audio transcribed in one call.
/// </summary>
/// <param name="Start">Where it starts in the video.</param>
/// <param name="Length">How long it is.</param>
public sealed record AudioChunk(TimeSpan Start, TimeSpan Length)
{
    /// <summary>Gets where it ends in the video.</summary>
    public TimeSpan End => Start + Length;
}

/// <summary>
/// A whole video is transcribed in chunks (a pure plan and merge, tested on their own). Every service gets the same
/// chunks: 10 minutes of 16 kHz mono 16-bit WAV is about 19 MB, safely under the 25 MB upload limit OpenAI-compatible
/// services commonly have, and it keeps the audio held in memory small (a whole film would be hundreds of megabytes of
/// samples). Chunks overlap by a few seconds; words are kept from the chunk whose half of the overlap they fall in, so a
/// word cut off at the end of one chunk comes whole from the next, and a word both chunks heard at the seam is kept once.
/// </summary>
public static class TranscriptChunks
{
    /// <summary>The length of a chunk, before the overlap.</summary>
    public static readonly TimeSpan DefaultLength = TimeSpan.FromMinutes(10);

    /// <summary>How much each chunk runs into the next.</summary>
    public static readonly TimeSpan DefaultOverlap = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Plans the chunks for a video.
    /// </summary>
    /// <param name="duration">The video's length.</param>
    /// <param name="length">The chunk length, or <c>null</c> for <see cref="DefaultLength"/>.</param>
    /// <param name="overlap">The overlap, or <c>null</c> for <see cref="DefaultOverlap"/>.</param>
    /// <returns>The chunks, in order; each (but the last) runs <paramref name="overlap"/> into the next.</returns>
    public static IReadOnlyList<AudioChunk> Plan(TimeSpan duration, TimeSpan? length = null, TimeSpan? overlap = null)
    {
        var step = length ?? DefaultLength;
        var over = overlap ?? DefaultOverlap;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(step, TimeSpan.Zero, nameof(length));
        ArgumentOutOfRangeException.ThrowIfLessThan(over, TimeSpan.Zero, nameof(overlap));
        var chunks = new List<AudioChunk>();
        for (var start = TimeSpan.Zero; start < duration; start += step)
        {
            // A last bit shorter than the overlap was already heard by the chunk before
            if (chunks.Count > 0 && start >= duration - over)
            {
                break;
            }

            var end = start + step + over < duration ? start + step + over : duration;
            chunks.Add(new AudioChunk(start, end - start));
        }

        return chunks;
    }

    /// <summary>
    /// Merges the words heard in each chunk into one list on the video's clock: each chunk's times are moved by its start,
    /// a word is kept from the chunk on whose side of the middle of the overlap it falls (by its midpoint), and a word
    /// heard by both chunks right at the seam (same word, starting within a second) is kept once.
    /// </summary>
    /// <param name="chunks">Each chunk, as it was read (its length may be shorter than planned at the end of the audio), and
    /// the words heard in it, timed from the chunk's start.</param>
    /// <returns>The words, in time order.</returns>
    public static IReadOnlyList<TranscribedWord> Merge(IReadOnlyList<(AudioChunk Chunk, IReadOnlyList<TranscribedWord> Words)> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        var ordered = chunks.OrderBy(c => c.Chunk.Start).ToList();
        var result = new List<TranscribedWord>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var (chunk, words) = ordered[i];
            var offset = chunk.Start.TotalSeconds;
            var from = i > 0 ? Seam(ordered[i - 1].Chunk, chunk) : double.NegativeInfinity;
            var to = i + 1 < ordered.Count ? Seam(chunk, ordered[i + 1].Chunk) : double.PositiveInfinity;
            var first = true;
            foreach (var w in words)
            {
                var moved = w with { Start = w.Start + offset, End = w.End + offset };
                var middle = (moved.Start + moved.End) / 2;
                if (middle < from || middle >= to)
                {
                    continue;
                }

                if (first && result.Count > 0 && SameWord(result[^1], moved))
                {
                    first = false;
                    continue;
                }

                first = false;
                result.Add(moved);
            }
        }

        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }

    // The middle of the stretch two chunks share (or where one ends and the next starts, if they don't overlap)
    private static double Seam(AudioChunk before, AudioChunk after)
        => (before.End.TotalSeconds + after.Start.TotalSeconds) / 2;

    private static bool SameWord(TranscribedWord a, TranscribedWord b)
        => Math.Abs(a.Start - b.Start) < 1.0 && string.Equals(Normalise(a.Text), Normalise(b.Text), StringComparison.Ordinal);

    private static string Normalise(string text) => new([.. text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
}
