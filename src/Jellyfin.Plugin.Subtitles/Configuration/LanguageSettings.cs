using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Subtitles.Configuration;

/// <summary>
/// What the subtitle-languages setting means.
/// </summary>
public static class LanguageSettings
{
    /// <summary>
    /// The languages to check: the configured three-letter codes, lower case, each once; English when none are set.
    /// </summary>
    /// <param name="configured">The setting.</param>
    /// <returns>The codes.</returns>
    public static IReadOnlyList<string> EffectiveLanguages(IEnumerable<string>? configured)
    {
        var codes = Recognised(configured);
        return codes.Count > 0 ? codes : [Fallback];
    }

    /// <summary>The language used when nothing else names one.</summary>
    public const string Fallback = "eng";

    /// <summary>
    /// The languages a list names that are recognised, as three-letter codes, lower case, each once (possibly none).
    /// </summary>
    /// <param name="configured">The list (codes or names in any form).</param>
    /// <returns>The codes.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "ISO 639-2 codes are lower case by convention, as Jellyfin stores them.")]
    public static IReadOnlyList<string> Recognised(IEnumerable<string?>? configured)
    {
        // Any form is accepted (eng, en, English, fre, fra, en-GB), through a table rather than the server's culture data
        // A three-letter code is kept as written (Jellyfin uses the bibliographic ones, such as fre and ger)
        return [.. (configured ?? []).Select(c => (c ?? string.Empty).Trim())
            .Select(c => c.Length == 3 && c.All(char.IsAsciiLetter) && Common.Languages.IsoLanguages.TwoLetter(c) is not null
                ? c.ToLowerInvariant()
                : Common.Languages.IsoLanguages.ThreeLetter(c))
            .OfType<string>().Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The languages for a library's videos: the plugin's own setting when it names any; otherwise the library's subtitle
    /// download languages (Jellyfin's library settings); otherwise the server's preferred metadata language (Jellyfin has
    /// no server-wide subtitle language); otherwise English.
    /// </summary>
    /// <param name="configured">The plugin's setting.</param>
    /// <param name="library">The library's subtitle download languages, if the video is in a known library.</param>
    /// <param name="server">The server's preferred metadata language, if set.</param>
    /// <returns>The languages and where they came from.</returns>
    public static LanguageChoice Choose(IEnumerable<string>? configured, IEnumerable<string>? library, string? server)
    {
        if (Recognised(configured) is { Count: > 0 } own)
        {
            return new(own, LanguageSource.Plugin);
        }

        if (Recognised(library) is { Count: > 0 } fromLibrary)
        {
            return new(fromLibrary, LanguageSource.Library);
        }

        return Recognised([server]) is { Count: > 0 } fromServer ? new(fromServer, LanguageSource.Server) : new([Fallback], LanguageSource.Default);
    }

    /// <summary>
    /// Configured languages that aren't recognised (they are ignored; worth a line in the log).
    /// </summary>
    /// <param name="configured">The setting.</param>
    /// <returns>The entries that aren't languages.</returns>
    public static IReadOnlyList<string> UnknownLanguages(IEnumerable<string>? configured)
    {
        return [.. (configured ?? []).Where(c => !string.IsNullOrWhiteSpace(c) && Common.Languages.IsoLanguages.ThreeLetter(c) is null).Select(c => c.Trim())];
    }
}

/// <summary>
/// Where the languages for a library's videos came from.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<LanguageSource>))]
public enum LanguageSource
{
    /// <summary>The plugin's own <b>Subtitle languages</b> setting.</summary>
    Plugin = 0,

    /// <summary>The library's subtitle download languages, in Jellyfin's library settings.</summary>
    Library,

    /// <summary>The server's preferred metadata language.</summary>
    Server,

    /// <summary>Nothing named one: English.</summary>
    Default,
}

/// <summary>
/// The languages for a library's videos.
/// </summary>
/// <param name="Codes">Three-letter codes, in order (never empty).</param>
/// <param name="Source">Where they came from.</param>
public sealed record LanguageChoice(IReadOnlyList<string> Codes, LanguageSource Source);
