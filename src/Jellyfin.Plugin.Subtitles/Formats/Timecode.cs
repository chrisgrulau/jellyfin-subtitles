using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Subtitles.Formats;

/// <summary>
/// Reads and writes the timestamp notations of the supported formats.
/// </summary>
public static partial class Timecode
{
    /// <summary>
    /// Parses <c>hh:mm:ss,mmm</c> (SubRip), <c>[hh:]mm:ss.mmm</c> (WebVTT) or <c>h:mm:ss.cc</c> (ASS); separators and
    /// fraction length are accepted loosely, as real-world files vary.
    /// </summary>
    /// <param name="text">The timestamp.</param>
    /// <param name="time">The parsed time.</param>
    /// <returns><c>true</c> if it could be read.</returns>
    public static bool TryParse(string text, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var m = Pattern().Match(text.Trim());
        if (!m.Success)
        {
            return false;
        }

        var hours = m.Groups["h"].Success ? int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture) : 0;
        var minutes = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture);
        var fraction = m.Groups["f"].Success ? m.Groups["f"].Value : "0";

        // "5" = 500 ms, "05" = 50 ms, "050" = 50 ms, "0500" = 50 ms (extra digits beyond milliseconds are ignored)
        var ms = int.Parse(fraction.PadRight(3, '0')[..3], CultureInfo.InvariantCulture);
        if (minutes > 59 || seconds > 59)
        {
            return false;
        }

        time = new TimeSpan(0, hours, minutes, seconds, ms);
        return true;
    }

    /// <summary>
    /// Formats a time for the given format.
    /// </summary>
    /// <param name="time">The time (negative values are written as zero).</param>
    /// <param name="format">The subtitle format.</param>
    /// <returns>The timestamp text.</returns>
    public static string Format(TimeSpan time, SubtitleFormat format)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        var h = (int)time.TotalHours;
        return format switch
        {
            SubtitleFormat.Srt => string.Create(CultureInfo.InvariantCulture, $"{h:00}:{time.Minutes:00}:{time.Seconds:00},{time.Milliseconds:000}"),
            SubtitleFormat.WebVtt => string.Create(CultureInfo.InvariantCulture, $"{h:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}"),

            // ASS has centisecond precision; round rather than truncate
            _ => FormatAss(time),
        };
    }

    private static string FormatAss(TimeSpan time)
    {
        var cs = (long)Math.Round(time.TotalMilliseconds / 10, MidpointRounding.AwayFromZero);
        var t = TimeSpan.FromMilliseconds(cs * 10);
        return string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}");
    }

    [GeneratedRegex(@"^(?:(?<h>\d{1,3})[:.])?(?<m>\d{1,2})[:.](?<s>\d{1,2})(?:[,.:](?<f>\d{1,4}))?$")]
    private static partial Regex Pattern();
}
