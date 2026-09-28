using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using Jellyfin.Plugin.Common.Resilience;

namespace Jellyfin.Plugin.Subtitles.Pipeline;

/// <summary>
/// Decisions of the missing-subtitle search that don't need Jellyfin's types (kept apart so they can be tested).
/// </summary>
public static class FindRules
{
    /// <summary>
    /// Whether a provider failure means no further search can work today: it was classified as the provider's limit (its
    /// download allowance or quota is used up, or it answered "too many requests") or as an authentication failure (it
    /// can't sign in). Decided by the failure class, never by exception type names; sources translate foreign exceptions at
    /// their boundary (see <see cref="Candidates.JellyfinSubtitleSource"/>).
    /// </summary>
    /// <param name="ex">The exception.</param>
    /// <returns><c>true</c> to stop the run.</returns>
    public static bool StopsTheRun(Exception ex) => ClassOf(ex) is FailureClass.ProviderLimit or FailureClass.Authentication;

    /// <summary>
    /// The failure class a search or download failure counts as within a run, if it was classified: a provider's own
    /// class, except that "too many requests" counts as a limit, since asking again for each remaining video would be
    /// refused too.
    /// </summary>
    /// <param name="ex">The exception.</param>
    /// <returns>The class, or <c>null</c> for an unclassified failure (only that video fails).</returns>
    internal static FailureClass? ClassOf(Exception? ex) => ex switch
    {
        ProviderException { RateLimited: true } => FailureClass.ProviderLimit,
        ProviderException p => p.Failure,
        _ => null,
    };

    /// <summary>
    /// Whether an existing subtitle track means the video already has subtitles in its language: forced-only tracks never
    /// do; picture-based ones do unless the setting says otherwise.
    /// </summary>
    /// <param name="isForced">The track is forced-only.</param>
    /// <param name="isText">The track is text-based.</param>
    /// <param name="countImages">Whether picture-based tracks count.</param>
    /// <returns><c>true</c> if it counts.</returns>
    public static bool Counts(bool isForced, bool isText, bool countImages) => !isForced && (isText || countImages);

    /// <summary>
    /// Whether an existing subtitle track means the video already has subtitles in its language, as
    /// <see cref="Counts(bool, bool, bool)"/>; a subtitle this plugin generated from a full transcript never does, so the
    /// search for a real one goes on (and replaces it when one is found).
    /// </summary>
    /// <param name="isForced">The track is forced-only.</param>
    /// <param name="isText">The track is text-based.</param>
    /// <param name="countImages">Whether picture-based tracks count.</param>
    /// <param name="isGenerated">The track is a generated subtitle file (see <see cref="SubtitleGenerator.IsGenerated"/>).</param>
    /// <returns><c>true</c> if it counts.</returns>
    public static bool Counts(bool isForced, bool isText, bool countImages, bool isGenerated) => !isGenerated && Counts(isForced, isText, countImages);

    /// <summary>
    /// Whether a missing subtitle in a language is searched for: only when the video's chosen audio stream is in that
    /// language, or has no usable tag and the language is the library's first (see <see cref="SpokenLanguage"/>). A
    /// subtitle in another language than the audio couldn't be checked against it; translation is planned.
    /// </summary>
    /// <param name="language">The wanted language.</param>
    /// <param name="audioTag">The chosen audio stream's language tag.</param>
    /// <param name="libraryLanguage">The first language wanted for the video's library.</param>
    /// <returns><c>true</c> to search.</returns>
    public static bool AudioIsIn(string? language, string? audioTag, string? libraryLanguage) => SpokenLanguage.Matches(language, audioTag, libraryLanguage);

    /// <summary>
    /// How many searches there are per language, for the log: <c>en 12, fr 3</c> (most first), or <c>none</c>.
    /// </summary>
    /// <param name="languages">Each search's language.</param>
    /// <returns>The counts.</returns>
    public static string PerLanguage(IEnumerable<string?> languages)
    {
        ArgumentNullException.ThrowIfNull(languages);
        var counts = languages.Select(l => SpeechToText.Languages.ToTwoLetter(l) ?? l ?? "?")
            .GroupBy(l => l, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key + " " + g.Count().ToString(CultureInfo.InvariantCulture))
            .ToList();
        return counts.Count == 0 ? "none" : string.Join(", ", counts);
    }
}
