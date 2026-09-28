using System;
using Jellyfin.Plugin.Subtitles.SpeechToText;

namespace Jellyfin.Plugin.Subtitles.Pipeline;

/// <summary>
/// Which language a video's audio is in, and whether a subtitle is in that language. Every stage that compares a subtitle
/// with what is said (the timing check by words, the wording audit, the whole-file check, the fix by section, generating)
/// works only on a subtitle in the audio's language; a subtitle in another language is never lined up by words.
/// </summary>
public static class SpokenLanguage
{
    /// <summary>
    /// Whether an audio stream's language tag says nothing about the language: missing, <c>und</c> (undetermined),
    /// <c>unk</c>, <c>mis</c> (uncoded), <c>zxx</c> (no linguistic content) or anything that isn't a known language.
    /// </summary>
    /// <param name="tag">The tag.</param>
    /// <returns><c>true</c> when the tag can't be used.</returns>
    public static bool IsUnknown(string? tag)
        => string.IsNullOrWhiteSpace(tag) || tag.Trim() is "und" or "unk" or "mis" or "zxx" || Languages.ToTwoLetter(tag) is null;

    /// <summary>
    /// The language the audio is in (two-letter): the audio stream's tag when it names a language, otherwise the
    /// library's first wanted language (the plugin's setting, the library's subtitle languages, the server's metadata
    /// language, or English; see <see cref="Configuration.LanguageSettings.Choose"/>).
    /// </summary>
    /// <param name="audioTag">The chosen audio stream's language tag.</param>
    /// <param name="libraryLanguage">The first language wanted for the video's library.</param>
    /// <returns>The two-letter code, or <c>null</c> when neither is known.</returns>
    public static string? Heard(string? audioTag, string? libraryLanguage)
        => IsUnknown(audioTag) ? Languages.ToTwoLetter(libraryLanguage) : Languages.ToTwoLetter(audioTag);

    /// <summary>
    /// Whether a subtitle is known to be in the audio's language: both languages are known and they are the same. Stages
    /// that compare the subtitle's words with what is said require this.
    /// </summary>
    /// <param name="subtitleLanguage">The subtitle's language.</param>
    /// <param name="audioTag">The chosen audio stream's language tag.</param>
    /// <param name="libraryLanguage">The first language wanted for the video's library.</param>
    /// <returns><c>true</c> when they are the same.</returns>
    public static bool Matches(string? subtitleLanguage, string? audioTag, string? libraryLanguage)
        => Languages.ToTwoLetter(subtitleLanguage) is { } subtitle
            && string.Equals(Heard(audioTag, libraryLanguage), subtitle, StringComparison.Ordinal);

    /// <summary>
    /// Whether a subtitle is known to be in another language than the audio: both are known and they differ. Such a
    /// subtitle is never lined up by words (speech-to-text would be asked for the wrong language, and nothing it hears
    /// could match); its timing is left alone, or checked by speech starts alone when that experimental setting is on.
    /// </summary>
    /// <param name="subtitleLanguage">The subtitle's language.</param>
    /// <param name="audioTag">The chosen audio stream's language tag.</param>
    /// <param name="libraryLanguage">The first language wanted for the video's library.</param>
    /// <returns><c>true</c> when they differ.</returns>
    public static bool Differs(string? subtitleLanguage, string? audioTag, string? libraryLanguage)
        => Languages.ToTwoLetter(subtitleLanguage) is { } subtitle
            && Heard(audioTag, libraryLanguage) is { } heard
            && !string.Equals(heard, subtitle, StringComparison.Ordinal);

    /// <summary>
    /// Whether a language is written without spaces between words (Chinese, Japanese, Thai, Lao, Khmer, Burmese, Tibetan):
    /// the timing check matches its text character by character, and the whole-file check, which compares words, skips it.
    /// </summary>
    /// <param name="language">The language (any form).</param>
    /// <returns><c>true</c> for such a language.</returns>
    public static bool WrittenWithoutSpaces(string? language) => Languages.ToTwoLetter(language) is "zh" or "ja" or "th" or "lo" or "km" or "my" or "bo";

    /// <summary>
    /// A language's English name for messages (<c>fr</c> → <c>French</c>), or the code itself when it isn't known.
    /// </summary>
    /// <param name="code">The code.</param>
    /// <returns>The name.</returns>
    public static string NameOf(string? code)
        => Common.Languages.IsoLanguages.EnglishName(code) ?? (string.IsNullOrWhiteSpace(code) ? "an unknown language" : code.Trim());
}
