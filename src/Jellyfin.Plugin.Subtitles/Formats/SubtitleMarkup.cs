using System;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Subtitles.Formats;

/// <summary>
/// Converts formatted cue text to plain text for matching and comparison.
/// </summary>
public static partial class SubtitleMarkup
{
    /// <summary>
    /// Removes HTML-style tags (<c>&lt;i&gt;</c>, <c>&lt;font&gt;</c>, WebVTT voice and class spans), ASS override
    /// blocks (<c>{\an8}</c>) and ASS hard spaces, and tidies whitespace. Line breaks are kept as <c>\n</c>.
    /// </summary>
    /// <param name="text">Cue text with markup.</param>
    /// <returns>The plain text.</returns>
    public static string ToPlainText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var s = HtmlTag().Replace(text, string.Empty);
        s = AssOverride().Replace(s, string.Empty);
        s = s.Replace("\\h", " ", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal)
            .Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&nbsp;", " ", StringComparison.Ordinal);
        var lines = s.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = Spaces().Replace(lines[i], " ").Trim();
        }

        return string.Join('\n', lines).Trim('\n');
    }

    [GeneratedRegex(@"</?[a-zA-Z][^<>]*>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"\{\\[^}]*\}")]
    private static partial Regex AssOverride();

    [GeneratedRegex(@"[ \t ]+")]
    private static partial Regex Spaces();
}
