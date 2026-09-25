using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Subtitles.SpeechToText;

namespace Jellyfin.Plugin.Subtitles.Pipeline;

/// <summary>
/// Which of a video's audio streams to listen to when checking a subtitle.
/// </summary>
public static class AudioChoice
{
    /// <summary>
    /// Picks the audio stream to listen to: the first in the subtitle's language, else the default, else the first.
    /// </summary>
    /// <param name="audio">Audio streams, in order.</param>
    /// <param name="language">The subtitle's language.</param>
    /// <returns>The position among audio streams.</returns>
    public static int For(IReadOnlyList<(string? Language, bool IsDefault)> audio, string? language)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var two = Languages.ToTwoLetter(language);
        for (var i = 0; i < audio.Count; i++)
        {
            if (two is not null && string.Equals(Languages.ToTwoLetter(audio[i].Language), two, StringComparison.Ordinal))
            {
                return i;
            }
        }

        for (var i = 0; i < audio.Count; i++)
        {
            if (audio[i].IsDefault)
            {
                return i;
            }
        }

        return 0;
    }
}
