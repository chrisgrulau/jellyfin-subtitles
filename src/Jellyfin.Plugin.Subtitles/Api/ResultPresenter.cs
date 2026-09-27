using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Subtitles.Discrepancy;
using Jellyfin.Plugin.Subtitles.Pipeline;
using Jellyfin.Plugin.Subtitles.SpeechToText;

namespace Jellyfin.Plugin.Subtitles.Api;

/// <summary>
/// What a video is, for its headline: an episode's series, season, number and title, or a film's title and year.
/// From Jellyfin's item when the result can be matched to one, else read from the file name
/// (<see cref="ResultPresenter.FromFileName"/>).
/// </summary>
/// <param name="SeriesName">The series (episodes).</param>
/// <param name="Season">The season number (episodes).</param>
/// <param name="Episode">The episode number (episodes).</param>
/// <param name="EpisodeTitle">The episode's title (episodes).</param>
/// <param name="Title">The film's (or other video's) title.</param>
/// <param name="Year">The film's year.</param>
public sealed record VideoIdentity(string? SeriesName, int? Season, int? Episode, string? EpisodeTitle, string? Title, int? Year)
{
    /// <summary>Gets a value indicating whether this is an episode (a series and an episode number).</summary>
    public bool IsEpisode => !string.IsNullOrWhiteSpace(SeriesName) && Episode is not null;
}

/// <summary>
/// A small icon with a count, standing for one kind of change (the words are in its tooltip).
/// </summary>
/// <param name="Kind">A stable name for the kind (<c>timing</c>, <c>sounds</c> …), for styling and tests.</param>
/// <param name="Icon">The icon.</param>
/// <param name="Label">What is shown beside it (a count, or a shift such as <c>+1.2 s</c>).</param>
/// <param name="Tooltip">What it means, in words (also its accessible label).</param>
/// <param name="Pending">Whether it waits for review rather than being done.</param>
public sealed record ResultChip(string Kind, string Icon, string Label, string Tooltip, bool Pending);

/// <summary>
/// One line of a result's "Nerd stats".
/// </summary>
/// <param name="Name">What it is.</param>
/// <param name="Value">Its value, as text.</param>
public sealed record NerdStat(string Name, string Value);

/// <summary>
/// A result as the settings page shows it: the short identity of the video, a friendly sentence, change chips, and the
/// statistics behind it for the expandable detail. Built on the server (<see cref="ResultPresenter"/>), so it is tested.
/// </summary>
/// <param name="Headline">The video, short: <c>Series S01E05</c> or <c>Title (Year)</c>.</param>
/// <param name="Subline">The episode's title, if any.</param>
/// <param name="Language">The subtitle's language tag from its file name (<c>EN</c>), if any.</param>
/// <param name="StatusText">The outcome in a few words.</param>
/// <param name="Summary">One friendly sentence.</param>
/// <param name="Notice">Something worth seeing at once (the speech-to-text service fell back), if anything.</param>
/// <param name="Chips">The changes, as icon chips.</param>
/// <param name="NerdStats">Everything else, for the detail area.</param>
/// <param name="When">When, relative (<c>3 hours ago</c>).</param>
/// <param name="CanRerun">Whether "Rerun with …" applies.</param>
/// <param name="RerunLabel">The button's label (<c>Rerun with Deepgram</c>), when it applies or was asked for.</param>
/// <param name="RerunQueued">Whether a rerun is already queued.</param>
public sealed record ResultView(
    string Headline,
    string? Subline,
    string? Language,
    string StatusText,
    string Summary,
    string? Notice,
    IReadOnlyList<ResultChip> Chips,
    IReadOnlyList<NerdStat> NerdStats,
    string When,
    bool CanRerun,
    string? RerunLabel,
    bool RerunQueued);

/// <summary>
/// Relative times for lists: "just now", "5 minutes ago", "3 hours ago" (within a day), "yesterday" and "2 days ago" (by
/// the calendar in the given time zone), then a short date
/// ("12 Sep", with the year when it isn't this year). Never seconds.
/// </summary>
public static class RelativeTime
{
    /// <summary>
    /// Formats a time relative to now.
    /// </summary>
    /// <param name="time">The time.</param>
    /// <param name="now">The current time.</param>
    /// <param name="zone">The time zone that decides "yesterday" and the date shown.</param>
    /// <returns>The text.</returns>
    public static string Format(DateTimeOffset time, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var ago = now - time;
        if (ago < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (ago < TimeSpan.FromHours(1))
        {
            var m = (int)ago.TotalMinutes;
            return m == 1 ? "a minute ago" : m.ToString(CultureInfo.InvariantCulture) + " minutes ago";
        }

        var localTime = TimeZoneInfo.ConvertTime(time, zone);
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var days = (localNow.Date - localTime.Date).Days;
        if (ago < TimeSpan.FromHours(24))
        {
            var h = (int)ago.TotalHours;
            return h == 1 ? "an hour ago" : h.ToString(CultureInfo.InvariantCulture) + " hours ago";
        }

        if (days <= 1)
        {
            return "yesterday";
        }

        if (days < 7)
        {
            return days.ToString(CultureInfo.InvariantCulture) + " days ago";
        }

        return localTime.Year == localNow.Year
            ? localTime.ToString("d MMM", CultureInfo.InvariantCulture)
            : localTime.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Turns a <see cref="SubtitleResult"/> into what the settings page shows (<see cref="ResultView"/>).
/// </summary>
public static partial class ResultPresenter
{
    /// <summary>The icons, one per kind of change (their meaning is in each chip's tooltip).</summary>
    public static readonly IReadOnlyDictionary<string, string> Icons = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["timing"] = "⏱",
        ["tidied"] = "↔",
        ["sounds"] = "🔈",
        ["removed"] = "✂",
        ["wording"] = "💬",
        ["added"] = "➕",
        ["encoding"] = "🔤",
        ["queued"] = "⏳",
    };

    private static readonly Dictionary<ResultStatus, string> StatusNames = new()
    {
        [ResultStatus.InSync] = "In sync",
        [ResultStatus.Corrected] = "Corrected",
        [ResultStatus.Proposed] = "Waiting for review",
        [ResultStatus.Unreliable] = "Unclear",
        [ResultStatus.WrongLanguage] = "Doesn't match speech",
        [ResultStatus.Failed] = "Failed",
        [ResultStatus.Undone] = "Undone",
        [ResultStatus.Added] = "Added",
        [ResultStatus.NotFound] = "Nothing found",
        [ResultStatus.TooLarge] = "Too large",
        [ResultStatus.CantWrite] = "Can't write here",
        [ResultStatus.Declined] = "Declined",
        [ResultStatus.Generated] = "Generated",
        [ResultStatus.NoSpeech] = "No speech",
        [ResultStatus.Replaced] = "Replaced",
    };

    private static readonly string[] SubtitleFlags = ["forced", "foreign", "sdh", "cc", "hi", "default", "generated"];

    /// <summary>
    /// The outcome in a few words.
    /// </summary>
    /// <param name="status">The status.</param>
    /// <returns>The words.</returns>
    public static string StatusText(ResultStatus status) => StatusNames.TryGetValue(status, out var s) ? s : status.ToString();

    /// <summary>
    /// Builds the view of a result.
    /// </summary>
    /// <param name="r">The result.</param>
    /// <param name="identity">What the video is, from Jellyfin, or <c>null</c> to read it from the file name.</param>
    /// <param name="now">The current time.</param>
    /// <param name="zone">The time zone for relative times and dates.</param>
    /// <param name="canRerun">Whether "Rerun with …" applies (see <see cref="SubtitleProcessor.CanRerun"/>).</param>
    /// <returns>The view.</returns>
    public static ResultView Present(SubtitleResult r, VideoIdentity? identity, DateTimeOffset now, TimeZoneInfo zone, bool canRerun)
    {
        ArgumentNullException.ThrowIfNull(r);
        var (headline, subline) = Headline(r, identity);
        var rerunFrom = r.RerunWith ?? r.SpeechFallback?.From;
        return new ResultView(
            headline,
            subline,
            LanguageOf(r.SubtitlePath),
            StatusText(r.Status),
            Summary(r),
            r.SpeechFallback?.Reason is { Length: > 0 } reason ? reason : null,
            Chips(r),
            NerdStats(r, zone),
            RelativeTime.Format(r.Time, now, zone),
            canRerun,
            rerunFrom is null ? null : "Rerun with " + ServiceName(rerunFrom),
            r.RerunWith is not null);
    }

    /// <summary>
    /// The video's short identity and the line beneath it: <c>Series S01E05</c> over the episode's title, or
    /// <c>Title (Year)</c>. From Jellyfin when known, else from the video's (or subtitle's) file name, else the result's
    /// name.
    /// </summary>
    /// <param name="r">The result.</param>
    /// <param name="identity">What the video is, from Jellyfin, if known.</param>
    /// <returns>The headline and the line beneath it.</returns>
    public static (string Headline, string? Subline) Headline(SubtitleResult r, VideoIdentity? identity)
    {
        ArgumentNullException.ThrowIfNull(r);
        var id = identity ?? FromFileName(r.VideoPath ?? r.SubtitlePath);
        if (id.IsEpisode)
        {
            var code = string.Create(CultureInfo.InvariantCulture, $"S{id.Season ?? 1:00}E{id.Episode:00}");
            var title = id.EpisodeTitle;
            if (string.IsNullOrWhiteSpace(title) && identity is null && !string.IsNullOrWhiteSpace(r.Name)
                && !string.Equals(r.Name, id.SeriesName, StringComparison.OrdinalIgnoreCase) && !CodeLike().IsMatch(r.Name))
            {
                // Jellyfin names an episode by its title: the result's name says it when the file name doesn't
                title = r.Name;
            }

            return (id.SeriesName!.Trim() + " " + code, string.IsNullOrWhiteSpace(title) ? null : title.Trim());
        }

        var name = !string.IsNullOrWhiteSpace(id.Title) ? id.Title.Trim() : !string.IsNullOrWhiteSpace(r.Name) ? r.Name.Trim() : Path.GetFileNameWithoutExtension(r.SubtitlePath);
        return (id.Year is { } year ? string.Create(CultureInfo.InvariantCulture, $"{name} ({year})") : name, null);
    }

    /// <summary>
    /// Reads what a video is from its file name (and folders, for a series): <c>Example Show - S01E05 - Pilot.mkv</c>,
    /// <c>Example.Show.1x05.720p.mkv</c>, <c>Season 1/Example Show S01E05.mkv</c>, <c>Invented Film (2019).mkv</c> or
    /// <c>Invented.Film.2019.1080p.mkv</c>. A subtitle's language and flags (<c>.en.forced.srt</c>) are ignored.
    /// </summary>
    /// <param name="path">The video's (or a subtitle's) path.</param>
    /// <returns>What could be read; at least a title.</returns>
    public static VideoIdentity FromFileName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var stem = Stem(path);
        var ep = EpisodePattern().Match(stem);
        if (ep.Success)
        {
            var series = Tidy(ep.Groups["series"].Value);
            if (series.Length == 0)
            {
                series = SeriesFolder(path) ?? string.Empty;
            }

            var rest = Tidy(CutAtRelease(ep.Groups["rest"].Value));
            return new VideoIdentity(
                series.Length > 0 ? series : null,
                int.Parse(ep.Groups["s"].Value, CultureInfo.InvariantCulture),
                int.Parse(ep.Groups["e"].Value, CultureInfo.InvariantCulture),
                rest.Length > 0 ? rest : null,
                series.Length > 0 ? null : stem,
                null);
        }

        var film = FilmPattern().Match(stem);
        if (film.Success)
        {
            return new VideoIdentity(null, null, null, null, Tidy(film.Groups["title"].Value), int.Parse(film.Groups["year"].Value, CultureInfo.InvariantCulture));
        }

        return new VideoIdentity(null, null, null, null, Tidy(CutAtRelease(stem)), null);
    }

    /// <summary>
    /// The subtitle's language tag from its file name (<c>Film.en.forced.srt</c> → <c>EN</c>), if it has one.
    /// </summary>
    /// <param name="subtitlePath">The subtitle file.</param>
    /// <returns>The tag in capitals, or <c>null</c>.</returns>
    public static string? LanguageOf(string subtitlePath)
    {
        var parts = Path.GetFileNameWithoutExtension(subtitlePath ?? string.Empty).Split('.');
        for (var i = parts.Length - 1; i > 0; i--)
        {
            var p = parts[i];
            if (SubtitleFlags.Contains(p, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            return p.Length is 2 or 3 && p.All(char.IsAsciiLetter) ? p.ToUpperInvariant() : null;
        }

        return null;
    }

    /// <summary>
    /// The changes as icon chips (see <see cref="Icons"/>): the timing shift, lines whose timing was tidied, sound
    /// descriptions removed, lines removed (adverts, empty lines, repeats), wording changed, lines to add, text that
    /// didn't decode cleanly, and a whole-file check or rerun queued. Changes waiting for review are separate chips.
    /// </summary>
    /// <param name="r">The result.</param>
    /// <returns>The chips.</returns>
    public static IReadOnlyList<ResultChip> Chips(SubtitleResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var chips = new List<ResultChip>();
        var shifted = r.Status is ResultStatus.Corrected or ResultStatus.Proposed or ResultStatus.Added && (Math.Abs(r.Offset) >= 0.005 || Math.Abs(r.Scale - 1) > 1e-9);
        if (shifted)
        {
            var label = Math.Abs(r.Offset) >= 0.005 ? string.Create(CultureInfo.InvariantCulture, $"{r.Offset:+0.0;-0.0} s") : "fps";
            var words = Shift(r) + (r.Status == ResultStatus.Proposed ? " — waiting for review" : string.Empty);
            chips.Add(new ResultChip("timing", Icons["timing"], label, words, r.Status == ResultStatus.Proposed));
        }

        foreach (var (counts, pending) in new[] { (r.Cleaned, false), (r.CleanupPending, true) })
        {
            var tail = pending ? " — waiting for review" : string.Empty;
            Add(chips, "tidied", Count(counts, "FixedOverlap", "ExtendedShortCue"), Plural(Count(counts, "FixedOverlap", "ExtendedShortCue"), "line's timing tidied (overlaps, brief lines)", "lines' timing tidied (overlaps, brief lines)") + tail, pending);
            Add(chips, "sounds", Count(counts, "StrippedHearingImpaired"), Plural(Count(counts, "StrippedHearingImpaired"), "sound description removed", "sound descriptions removed") + tail, pending);
            var removed = Count(counts, "RemovedAdvert", "RemovedEmpty", "MergedDuplicate");
            Add(chips, "removed", removed, Removed(counts) + tail, pending);
            var worded = Count(counts, SubtitleProcessor.RewordedKind, SubtitleEditing.EditedKind, DiscrepancyReview.FixedKind);
            Add(chips, "wording", worded, Plural(worded, "line reworded or edited", "lines reworded or edited") + tail, pending);
        }

        var open = r.Findings.Where(f => f.Suggestion is not null || f.From is not null).ToList();
        var toAdd = open.Count(f => f.Kind == "missing-line");
        var toRemove = open.Count(f => f.Kind == "extra" && f.From is not null);
        var toWord = open.Count - toAdd - toRemove;
        Add(chips, "wording", toWord, Plural(toWord, "line differs from what is said — waiting for review", "lines differ from what is said — waiting for review"), true);
        Add(chips, "added", toAdd, Plural(toAdd, "line heard but missing — waiting for review", "lines heard but missing — waiting for review"), true);
        Add(chips, "removed", toRemove, Plural(toRemove, "line with nothing heard — waiting for review", "lines with nothing heard — waiting for review"), true);
        if (r.Explanation.Contains("didn't decode cleanly", StringComparison.Ordinal))
        {
            chips.Add(new ResultChip("encoding", Icons["encoding"], "!", "The text didn't decode cleanly (a guessed code page), so changes wait for review", true));
        }

        if (r.WholeFileRequested)
        {
            chips.Add(new ResultChip("queued", Icons["queued"], string.Empty, "Whole-file check queued for the next full-transcript run", true));
        }

        if (r.RerunWith is { } rerun)
        {
            chips.Add(new ResultChip("queued", Icons["queued"], string.Empty, "Queued to run again with " + ServiceName(rerun) + " on the next check", true));
        }

        return chips;
    }

    /// <summary>
    /// One friendly sentence about the result ("In sync — no timing change needed", "Shifted 1.2 s later to match the
    /// speech", "Nothing fitting was found", "Generated from a transcript (1,037 lines)").
    /// </summary>
    /// <param name="r">The result.</param>
    /// <returns>The sentence.</returns>
    public static string Summary(SubtitleResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var sentence = r.Status switch
        {
            ResultStatus.InSync => "In sync — no timing change needed",
            ResultStatus.Corrected => Shift(r) + " to match the speech",
            ResultStatus.Proposed => "Could be " + LowerFirst(Shift(r)) + " — waiting for your review",
            ResultStatus.Unreliable => "Couldn't tell whether the timing is right, so it was left alone",
            ResultStatus.WrongLanguage => "Doesn't match what is said (another language or version?) — left alone",
            ResultStatus.Failed => "Couldn't be checked: " + FirstSentence(r.Explanation),
            ResultStatus.Undone => "Undone — the original is back",
            ResultStatus.Added => "Added from " + SourceOf(r.Origin) + (Math.Abs(r.Offset) >= 0.005 || Math.Abs(r.Scale - 1) > 1e-9 ? " and " + LowerFirst(Shift(r)) : ", already in sync"),
            ResultStatus.NotFound => "Nothing fitting was found",
            ResultStatus.TooLarge => "Too large to be a subtitle file",
            ResultStatus.CantWrite => "Jellyfin can't write in this folder",
            ResultStatus.Declined => "You declined the suggested change",
            ResultStatus.Generated => Stat(r.Explanation, LinesPattern()) is { } lines
                ? "Generated from a transcript (" + FormatCount(lines.Groups[1].Value) + " lines)"
                : "Generated from a transcript",
            ResultStatus.NoSpeech => "No speech to transcribe, so nothing was generated",
            ResultStatus.Replaced => "Replaced by a subtitle found later",
            _ => StatusText(r.Status),
        };
        var open = r.Findings.Count(f => f.Suggestion is not null || f.From is not null);
        if (open > 0 && r.Status != ResultStatus.Proposed)
        {
            sentence += " — " + Plural(open, "line to review", "lines to review");
        }

        return sentence;
    }

    /// <summary>
    /// The statistics behind a result, for its detail area: when, how it was decided, confidence, shift and frame rate,
    /// the line-start stage's stretches, margin and z, the matched words, the speech-to-text service, any fallback, the
    /// whole-file check, counts, files, and the full explanation.
    /// </summary>
    /// <param name="r">The result.</param>
    /// <param name="zone">The time zone for the time shown.</param>
    /// <returns>The statistics, in order.</returns>
    public static IReadOnlyList<NerdStat> NerdStats(SubtitleResult r, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(zone);
        var stats = new List<NerdStat>
        {
            new("Checked", TimeZoneInfo.ConvertTime(r.Time, zone).ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture)),
            new("Outcome", StatusText(r.Status)),
        };
        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                stats.Add(new NerdStat(name, value));
            }
        }

        Add("Decided by", r.Stage);
        if (r.Confidence > 0)
        {
            Add("Confidence", r.Confidence.ToString("0.00", CultureInfo.InvariantCulture));
        }

        if (Math.Abs(r.Offset) >= 0.0005)
        {
            Add("Shift", string.Create(CultureInfo.InvariantCulture, $"{r.Offset:+0.00;-0.00} s"));
        }

        if (Math.Abs(r.Scale - 1) > 1e-9)
        {
            Add("Frame-rate ratio", string.Create(CultureInfo.InvariantCulture, $"×{r.Scale:0.00000}"));
        }

        var e = r.Explanation;
        Add("Stretches agreeing", Stat(e, StretchesPattern()) is { } st ? st.Groups[1].Value + " of " + st.Groups[2].Value : null);
        Add("Margin", Stat(e, MarginPattern())?.Groups[1].Value);
        Add("z", Stat(e, ZPattern())?.Groups[1].Value);
        Add("Matched words agreeing", Stat(e, AnchorsPattern()) is { } an ? an.Groups[1].Value + " of " + an.Groups[2].Value : null);
        Add("Speech-to-text", string.IsNullOrEmpty(r.SpeechSetup) ? null : ServiceName(r.SpeechSetup));
        if (r.SpeechFallback is { } fb)
        {
            Add("Fell back", fb.To is null ? "No: " + fb.Reason : fb.Reason);
        }

        if (Stat(e, LinesPattern()) is { } gen)
        {
            Add("Lines", FormatCount(gen.Groups[1].Value));
            Add("Words", FormatCount(gen.Groups[2].Value));
            Add("Parts", gen.Groups[3].Value);
        }

        if (r.WholeFile is { } whole)
        {
            Add("Whole-file check", TimeZoneInfo.ConvertTime(whole.Time, zone).ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture) + " by " + whole.Setup + (whole.Failed ? " (failed)" : string.Empty));
            Add("Whole-file findings", string.Join(", ", whole.Counts.Select(c => c.Value.ToString(CultureInfo.InvariantCulture) + " " + c.Key)));
        }

        if (r.Audited)
        {
            Add("Wording audited", "yes");
        }

        Add("Changes made", CountsText(r.Cleaned));
        Add("Changes waiting", CountsText(r.CleanupPending));
        Add("Source", r.Origin);
        Add("Subtitle file", r.SubtitlePath);
        Add("Video file", r.VideoPath);
        Add("Explanation", r.Explanation);
        Add("Pipeline version", r.Version > 0 ? r.Version.ToString(CultureInfo.InvariantCulture) : null);
        return stats;
    }

    /// <summary>
    /// A speech-to-text service's name for people, in the middle of a sentence (<c>Deepgram</c>, <c>the local service</c>).
    /// </summary>
    /// <param name="id">The service id.</param>
    /// <returns>The name.</returns>
    public static string ServiceName(string id)
    {
        var name = SpeechHealth.NameOf(id ?? string.Empty);
        return name.StartsWith("The ", StringComparison.Ordinal) ? "the " + name[4..] : name;
    }

    private static void Add(List<ResultChip> chips, string kind, int count, string tooltip, bool pending)
    {
        if (count > 0)
        {
            chips.Add(new ResultChip(kind, Icons[kind], count.ToString(CultureInfo.InvariantCulture), char.ToUpperInvariant(tooltip[0]) + tooltip[1..], pending));
        }
    }

    private static int Count(IReadOnlyDictionary<string, int> counts, params string[] kinds) => kinds.Sum(k => counts.GetValueOrDefault(k));

    private static string Removed(IReadOnlyDictionary<string, int> counts)
    {
        var parts = new List<string>();
        void Part(string kind, string one, string many)
        {
            var n = counts.GetValueOrDefault(kind);
            if (n > 0)
            {
                parts.Add(Plural(n, one, many));
            }
        }

        Part("RemovedAdvert", "advert or credit line", "adverts or credit lines");
        Part("RemovedEmpty", "empty line", "empty lines");
        Part("MergedDuplicate", "repeated line", "repeated lines");
        return string.Join(", ", parts) + " removed";
    }

    private static string? CountsText(IReadOnlyDictionary<string, int> counts)
        => counts.Count == 0 ? null : string.Join(", ", counts.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => c.Key + " " + c.Value.ToString(CultureInfo.InvariantCulture)));

    private static string Plural(int n, string one, string many)
        => n.ToString("#,0", CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);

    // "Shifted 1.2 s later", "Shifted 0.8 s earlier", "Speed corrected for a frame-rate difference and shifted …"
    private static string Shift(SubtitleResult r)
    {
        var shift = Math.Abs(r.Offset) >= 0.005
            ? string.Create(CultureInfo.InvariantCulture, $"shifted {Math.Abs(r.Offset):0.0#} s {(r.Offset > 0 ? "later" : "earlier")}")
            : string.Empty;
        var rate = Math.Abs(r.Scale - 1) > 1e-9 ? "speed corrected for a frame-rate difference" : string.Empty;
        var text = rate.Length > 0 && shift.Length > 0 ? rate + " and " + shift : rate + shift;
        return text.Length == 0 ? "Timing unchanged" : char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static string LowerFirst(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    private static string SourceOf(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return "a subtitle provider";
        }

        var colon = origin.IndexOf(':', StringComparison.Ordinal);
        return (colon > 0 ? origin[..colon] : origin).Trim();
    }

    private static string FirstSentence(string text)
    {
        var s = (text ?? string.Empty).Trim();
        var end = s.IndexOf(". ", StringComparison.Ordinal);
        s = end > 0 ? s[..end] : s.TrimEnd('.');
        return s.Length > 90 ? s[..89].TrimEnd() + "…" : s.Length == 0 ? "unknown error" : LowerFirst(s);
    }

    private static string FormatCount(string digits)
        => int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n.ToString("#,0", CultureInfo.InvariantCulture) : digits;

    private static Match? Stat(string text, Regex pattern)
    {
        var m = pattern.Match(text ?? string.Empty);
        return m.Success ? m : null;
    }

    // The file name without extension, and without a subtitle's language and flags
    private static string Stem(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var parts = name.Split('.').ToList();
        while (parts.Count > 1 && (SubtitleFlags.Contains(parts[^1], StringComparer.OrdinalIgnoreCase) || (parts[^1].Length is 2 or 3 && parts[^1].All(char.IsAsciiLetter) && parts[^1].All(char.IsLower))))
        {
            parts.RemoveAt(parts.Count - 1);
        }

        return string.Join('.', parts);
    }

    // A series named only by its folder: the folder above "Season 1", or the folder holding the file
    private static string? SeriesFolder(string path)
    {
        var folder = Path.GetDirectoryName(path);
        if (folder is null)
        {
            return null;
        }

        var name = Path.GetFileName(folder);
        if (SeasonFolder().IsMatch(name))
        {
            name = Path.GetFileName(Path.GetDirectoryName(folder) ?? string.Empty);
        }

        var tidy = Tidy(name);
        return tidy.Length > 0 ? tidy : null;
    }

    private static string CutAtRelease(string s)
    {
        var m = ReleaseTag().Match(s);
        return m.Success ? s[..m.Index] : s;
    }

    private static string Tidy(string s)
    {
        var t = BracketGroup().Replace(s, " ");
        if (!t.Contains(' ', StringComparison.Ordinal))
        {
            t = t.Replace('.', ' ');
        }

        t = t.Replace('_', ' ');
        t = Spaces().Replace(t, " ");
        return t.Trim(' ', '-', '.', '_', '–');
    }

    [GeneratedRegex(@"^(?<series>.*?)[\s._\-\[(]*(?:[Ss](?<s>\d{1,2})[\s._-]?[Ee](?<e>\d{1,3})(?:[\s._-]?-?[Ee]\d{1,3})?|(?<s>\d{1,2})x(?<e>\d{2,3}))(?![0-9])(?<rest>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex EpisodePattern();

    [GeneratedRegex(@"^(?<title>.+?)[\s._\-]*[\[(]?(?<year>(?:19|20)\d{2})[\])]?(?:[\s._\-\[(]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex FilmPattern();

    [GeneratedRegex(@"(?i)[\s._\-\[(](?:2160p|1080p|720p|576p|480p|4k|uhd|web-?dl|webrip|web|bluray|blu-ray|bdrip|brrip|hdtv|dvdrip|dvd|x264|x265|h\.?264|h\.?265|hevc|aac|ddp?5\.1|atmos|proper|repack|remux|hdr|dv)(?:[\s._\-\])]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseTag();

    [GeneratedRegex(@"\[[^\]]*\]|\{[^}]*\}", RegexOptions.CultureInvariant)]
    private static partial Regex BracketGroup();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    [GeneratedRegex(@"(?i)^(?:season|series|staffel|saison|temporada)[\s._-]*\d+$|^[Ss]\d{1,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex SeasonFolder();

    [GeneratedRegex(@"(?i)^(?:s\d+e\d+|\d+x\d+|episode \d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex CodeLike();

    [GeneratedRegex(@"(\d+) of (\d+) stretches agree", RegexOptions.CultureInvariant)]
    private static partial Regex StretchesPattern();

    [GeneratedRegex(@"margin (\d+(?:\.\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex MarginPattern();

    [GeneratedRegex(@"\bz (-?\d+(?:\.\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex ZPattern();

    [GeneratedRegex(@"(\d+) of (\d+) matched words agree", RegexOptions.CultureInvariant)]
    private static partial Regex AnchorsPattern();

    [GeneratedRegex(@"(\d+) lines from (\d+) words, in (\d+) part", RegexOptions.CultureInvariant)]
    private static partial Regex LinesPattern();
}
