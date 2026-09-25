using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.Subtitles.SpeechToText.BuiltIn;

/// <summary>
/// Reads whisper.cpp's full JSON output (<c>-ojf</c>) into timed words.
/// </summary>
public static class WhisperCppOutput
{
    /// <summary>
    /// How much later whisper.cpp's DTW token times are than the word starts the local-service calibration used
    /// (faster-whisper): 0.26–0.30 s on four clips from films and series, so this is taken off.
    /// </summary>
    public const double DtwLag = 0.28;

    /// <summary>
    /// Reads the output.
    /// </summary>
    /// <param name="json">The JSON file's contents.</param>
    /// <param name="maxWords">The most words kept.</param>
    /// <returns>The words, in order, and the language whisper.cpp used.</returns>
    public static (IReadOnlyList<TranscribedWord> Words, string? Language) Parse(string json, int maxWords = HttpSpeechToText.MaxWords)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string? language = root.TryGetProperty("result", out var result) && result.TryGetProperty("language", out var l) && l.ValueKind == JsonValueKind.String
            ? l.GetString()
            : null;

        var words = new List<TranscribedWord>();
        if (!root.TryGetProperty("transcription", out var segments) || segments.ValueKind != JsonValueKind.Array)
        {
            return (words, language);
        }

        // Tokens are pieces of words; a token starting with a space starts a new word
        string? text = null;
        double start = 0, end = 0, confidence = 0;
        var pieces = 0;
        var described = false;
        void Flush()
        {
            // Sound descriptions ([Music], (laughs), [BLANK_AUDIO], [door closes]) aren't speech
            var wasDescribed = described;
            if (text is not null && (text.StartsWith('[') || text.StartsWith('(')))
            {
                described = wasDescribed = true;
            }

            if (text is not null && (text.Contains(']', StringComparison.Ordinal) || text.Contains(')', StringComparison.Ordinal)))
            {
                described = false;
            }

            if (text is not null && !wasDescribed && text.Any(char.IsLetterOrDigit) && words.Count < maxWords)
            {
                var last = words.Count > 0 ? words[^1].Start : 0;
                var s = Math.Max(Math.Max(0, start), last);
                words.Add(new TranscribedWord(text, s, Math.Max(s, end), confidence / pieces));
            }

            text = null;
        }

        foreach (var segment in segments.EnumerateArray())
        {
            if (!segment.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var token in tokens.EnumerateArray())
            {
                var piece = token.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                if (piece.StartsWith("[_", StringComparison.Ordinal) || piece.Length == 0)
                {
                    // Special tokens: beginning, timestamps, end
                    continue;
                }

                var from = Milliseconds(token, "from") / 1000.0;
                var to = Milliseconds(token, "to") / 1000.0;
                var dtw = token.TryGetProperty("t_dtw", out var d) && d.TryGetInt64(out var cs) && cs >= 0 ? cs / 100.0 - DtwLag : (double?)null;
                var p = token.TryGetProperty("p", out var pr) && pr.TryGetDouble(out var pv) ? Math.Clamp(pv, 0, 1) : 0;
                if (text is null || piece.StartsWith(' '))
                {
                    Flush();
                    text = piece.Trim();
                    start = dtw ?? from;
                    end = to;
                    confidence = p;
                    pieces = 1;
                }
                else
                {
                    text += piece;
                    end = Math.Max(end, to);
                    confidence += p;
                    pieces++;
                }
            }
        }

        Flush();
        return (words, language);
    }

    private static long Milliseconds(JsonElement token, string which)
        => token.TryGetProperty("offsets", out var o) && o.TryGetProperty(which, out var v) && v.TryGetInt64(out var ms) ? ms : 0;
}
