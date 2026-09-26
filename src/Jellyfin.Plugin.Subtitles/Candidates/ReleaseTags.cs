using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Subtitles.Candidates;

/// <summary>
/// Where a release came from, grouped by what matters for subtitle timing: releases from the same kind of source
/// usually share a cut and timing, releases from different kinds often don't.
/// </summary>
public enum ReleaseSource
{
    /// <summary>Not stated.</summary>
    Unknown = 0,

    /// <summary>Blu-ray / UHD Blu-ray (including remuxes).</summary>
    BluRay,

    /// <summary>Streaming service download or capture (WEB-DL, WEBRip).</summary>
    Web,

    /// <summary>Broadcast capture.</summary>
    Hdtv,

    /// <summary>DVD.</summary>
    Dvd,
}

/// <summary>
/// The parts of a release name that tell whether a subtitle was made for the same version of a video.
/// </summary>
public sealed partial record ReleaseTags
{
    /// <summary>Gets the release group (the name after the final hyphen), upper-cased.</summary>
    public string? Group { get; init; }

    /// <summary>Gets the kind of source.</summary>
    public ReleaseSource Source { get; init; }

    /// <summary>Gets the streaming service code (AMZN, NF, DSNP …), upper-cased.</summary>
    public string? Service { get; init; }

    /// <summary>Gets the vertical resolution (480, 720, 1080, 2160).</summary>
    public int? Resolution { get; init; }

    /// <summary>Gets the edition (Extended, Director's Cut, Unrated, Theatrical, IMAX …).</summary>
    public string? Edition { get; init; }

    /// <summary>Gets a value indicating whether this is a REPACK or PROPER (a corrected re-release).</summary>
    public bool IsRepack { get; init; }

    /// <summary>Gets the season number, when the name has an episode code.</summary>
    public int? Season { get; init; }

    /// <summary>Gets the episode number, when the name has an episode code.</summary>
    public int? Episode { get; init; }

    /// <summary>Gets the last episode of a multi-episode code (<c>S01E01E02</c>, <c>S01E01-E02</c>).</summary>
    public int? EndingEpisode { get; init; }

    /// <summary>
    /// Reads the tags from a release or file name.
    /// </summary>
    /// <param name="name">Release or file name (an extension is ignored).</param>
    /// <returns>The tags found.</returns>
    public static ReleaseTags Parse(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var stem = Path.HasExtension(name) && Path.GetExtension(name).Length <= 5 ? Path.GetFileNameWithoutExtension(name) : name;
        var text = stem.Replace('_', '.');

        var source = ReleaseSource.Unknown;
        if (BluRayPattern().IsMatch(text))
        {
            source = ReleaseSource.BluRay;
        }
        else if (WebPattern().IsMatch(text))
        {
            source = ReleaseSource.Web;
        }
        else if (HdtvPattern().IsMatch(text))
        {
            source = ReleaseSource.Hdtv;
        }
        else if (DvdPattern().IsMatch(text))
        {
            source = ReleaseSource.Dvd;
        }

        var service = ServicePattern().Match(text);
        var resolution = ResolutionPattern().Match(text);
        var edition = EditionPattern().Match(text);
        var episode = EpisodeCodePattern().Match(text);
        var group = GroupPattern().Match(stem);

        return new ReleaseTags
        {
            Source = source,
            Service = service.Success ? service.Groups[1].Value.ToUpperInvariant() : null,
            Resolution = resolution.Success ? int.Parse(resolution.Groups[1].Value, CultureInfo.InvariantCulture) : null,
            Edition = edition.Success ? NormaliseEdition(edition.Value) : null,
            IsRepack = RepackPattern().IsMatch(text),
            Group = group.Success && !IsTechnicalToken(group.Groups[1].Value) ? group.Groups[1].Value.ToUpperInvariant() : null,
            Season = episode.Success ? int.Parse(episode.Groups["s"].Value, CultureInfo.InvariantCulture) : null,
            Episode = episode.Success ? int.Parse(episode.Groups["e"].Value, CultureInfo.InvariantCulture) : null,
            EndingEpisode = episode.Success && episode.Groups["e2"].Success ? int.Parse(episode.Groups["e2"].Value, CultureInfo.InvariantCulture) : null,
        };
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "Lower-casing is only for title-casing a display label, never for comparison.")]
    private static string NormaliseEdition(string raw)
    {
        var s = raw.Replace('.', ' ').Replace('_', ' ').Replace('-', ' ').Trim().ToUpperInvariant();
        return s switch
        {
            _ when s.StartsWith("DIRECTOR", StringComparison.Ordinal) => "Director's Cut",
            _ when s.StartsWith("EXTENDED", StringComparison.Ordinal) => "Extended",
            _ when s.StartsWith("UNRATED", StringComparison.Ordinal) => "Unrated",
            _ when s.StartsWith("THEATRICAL", StringComparison.Ordinal) => "Theatrical",
            _ when s.StartsWith("UNCUT", StringComparison.Ordinal) => "Uncut",
            _ when s.StartsWith("REMASTERED", StringComparison.Ordinal) => "Remastered",
            _ when s.StartsWith("IMAX", StringComparison.Ordinal) => "IMAX",
            _ when s.StartsWith("SPECIAL", StringComparison.Ordinal) => "Special Edition",
            _ when s.StartsWith("ULTIMATE", StringComparison.Ordinal) => "Ultimate",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant()),
        };
    }

    // "-x264", "-DDP5" and the like are codec tags that happen to follow a hyphen, not a group
    private static bool IsTechnicalToken(string token)
        => TechnicalPattern().IsMatch(token) || token.All(char.IsAsciiDigit);

    [GeneratedRegex(@"\b(?:blu-?ray|bdrip|brrip|bdremux|remux|bd25|bd50|uhd\.?bd)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BluRayPattern();

    [GeneratedRegex(@"\b(?:web-?dl|webrip|web)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WebPattern();

    [GeneratedRegex(@"\b(?:hdtv|pdtv|dsr|tvrip|hdtvrip)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HdtvPattern();

    [GeneratedRegex(@"\b(?:dvdrip|dvd|dvd5|dvd9|dvdscr)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DvdPattern();

    [GeneratedRegex(@"\b(AMZN|NF|DSNP|HMAX|ATVP|HULU|PCOK|PMTP|STAN|iT|CRAV|BCORE|ROKU|STZ|VUDU|iP)\b")]
    private static partial Regex ServicePattern();

    [GeneratedRegex(@"\b(480|576|720|1080|2160)[pi]\b", RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionPattern();

    [GeneratedRegex(@"\b(?:director'?s[\s._-]?cut|extended(?:[\s._-]?(?:cut|edition))?|unrated|theatrical(?:[\s._-]?cut)?|uncut|remastered|imax|special[\s._-]?edition|ultimate(?:[\s._-]?(?:cut|edition))?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex EditionPattern();

    [GeneratedRegex(@"\b(?:repack[0-9]?|proper|rerip)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RepackPattern();

    [GeneratedRegex(@"\bS(?<s>[0-9]{1,2})[\s._-]?E(?<e>[0-9]{1,3})(?:[\s._]?(?:-E?|E)(?<e2>[0-9]{1,3}))?\b|\b(?<s>[0-9]{1,2})x(?<e>[0-9]{2,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeCodePattern();

    [GeneratedRegex(@"-([A-Za-z0-9]{2,20})(?:\[[^\]]*\])?\s*$")]
    private static partial Regex GroupPattern();

    [GeneratedRegex(@"^(?:x26[45]|h\.?26[45]|hevc|avc|xvid|divx|aac[0-9]?|ac3|dts|ddp?[0-9]?|truehd|atmos|dl|rip|hdr[0-9]*|dv|sdr|10bit|8bit)$", RegexOptions.IgnoreCase)]
    private static partial Regex TechnicalPattern();
}
