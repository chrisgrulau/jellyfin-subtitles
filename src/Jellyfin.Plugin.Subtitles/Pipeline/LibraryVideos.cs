using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Subtitles.Audio;
using Jellyfin.Plugin.Subtitles.Candidates;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Subtitles.Pipeline;

/// <summary>
/// The library's films and episodes, walked once in one way for every task: the subtitle files to check, the embedded
/// tracks to check and the subtitles to find. Only videos whose file exists, with a running time and at least one audio
/// stream, are listed. "Has a subtitle in this language" is decided by <see cref="FindRules.Counts(bool, bool, bool, bool)"/>
/// alone: a subtitle this plugin generated doesn't count, so the search for a real one goes on.
/// </summary>
internal sealed class LibraryVideos
{
    private static readonly string[] TextExtensions = [".srt", ".vtt", ".ass", ".ssa"];

    private readonly ILibraryManager _library;
    private readonly IMediaSourceManager _media;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryVideos"/> class.
    /// </summary>
    /// <param name="library">Library manager.</param>
    /// <param name="media">Media source manager (streams, including external subtitles).</param>
    public LibraryVideos(ILibraryManager library, IMediaSourceManager media)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _media = media ?? throw new ArgumentNullException(nameof(media));
    }

    /// <summary>
    /// Text subtitle files beside the videos, in the chosen languages. Generated subtitles aren't listed: they are
    /// speech-to-text already, so checking their timing against speech-to-text would prove nothing.
    /// </summary>
    /// <param name="languages">The chosen languages, as two-letter codes.</param>
    /// <returns>The files to check.</returns>
    public IEnumerable<SubtitleJob> SubtitleFiles(IReadOnlySet<string> languages)
    {
        ArgumentNullException.ThrowIfNull(languages);
        foreach (var v in Walk())
        {
            foreach (var sub in v.Streams.Where(s => s.Type == MediaStreamType.Subtitle && s.IsExternal && !string.IsNullOrEmpty(s.Path)))
            {
                var ext = Path.GetExtension(sub.Path).ToUpperInvariant();
                if (!TextExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)) || Languages.ToTwoLetter(sub.Language) is not { } lang || !languages.Contains(lang)
                    || SubtitleGenerator.IsGenerated(sub.Path))
                {
                    continue;
                }

                yield return new SubtitleJob(v.Item.Id, v.Item.Name, v.Video.Path, sub.Path, sub.Language, v.Duration, AudioChoice.For(v.Audio, sub.Language));
            }
        }
    }

    /// <summary>
    /// Embedded text tracks in the chosen languages, for videos with no subtitle file of that language beside them (one
    /// that counts, see <see cref="FindRules.Counts(bool, bool, bool, bool)"/>).
    /// </summary>
    /// <param name="languages">The chosen languages, as two-letter codes.</param>
    /// <param name="countImages">Whether picture-based subtitles count as having one.</param>
    /// <returns>The tracks to check.</returns>
    public IEnumerable<EmbeddedJob> EmbeddedTracks(IReadOnlySet<string> languages, bool countImages)
    {
        ArgumentNullException.ThrowIfNull(languages);
        foreach (var v in Walk())
        {
            var beside = LanguagesWithSubtitles(v.Streams.Where(s => s.IsExternal), countImages);
            foreach (var sub in v.Streams.Where(s => s.Type == MediaStreamType.Subtitle && !s.IsExternal && !s.IsForced && s.IsTextSubtitleStream))
            {
                if (Languages.ToTwoLetter(sub.Language) is not { } lang || !languages.Contains(lang) || beside.Contains(lang)
                    || !FfmpegSubtitleExtractor.TextCodecs.Contains(sub.Codec ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                beside.Add(lang);
                yield return new EmbeddedJob(v.Item.Id, v.Item.Name, v.Video.Path, sub.Index, sub.Codec, sub.Language!, v.Duration, AudioChoice.For(v.Audio, sub.Language), EmbeddedChecker.FingerprintOf(v.Video.Path, sub.Index));
            }
        }
    }

    /// <summary>
    /// Videos with no subtitle (beside them or inside) that counts in a chosen language (a generated one doesn't), with the
    /// language of the audio stream that goes with it.
    /// </summary>
    /// <param name="languages">The chosen languages, as configured (three-letter codes).</param>
    /// <param name="countImages">Whether picture-based subtitles count as having one.</param>
    /// <returns>The searches to make.</returns>
    public IEnumerable<FindJob> Missing(IReadOnlyList<string> languages, bool countImages)
    {
        ArgumentNullException.ThrowIfNull(languages);
        foreach (var v in Walk())
        {
            var have = LanguagesWithSubtitles(v.Streams, countImages);
            foreach (var language in languages)
            {
                if (Languages.ToTwoLetter(language) is { } two && !have.Contains(two))
                {
                    var audio = AudioChoice.For(v.Audio, language);
                    yield return new FindJob(v.Item.Id, v.Item.Name, v.Video.Path, VideoFactsReader.Read(v.Video), language, v.Duration, audio, v.Audio[audio].Language);
                }
            }
        }
    }

    // The languages (two-letter) that have a subtitle among these streams that counts
    private static HashSet<string> LanguagesWithSubtitles(IEnumerable<MediaStream> streams, bool countImages)
        => streams.Where(s => s.Type == MediaStreamType.Subtitle && FindRules.Counts(s.IsForced, s.IsTextSubtitleStream, countImages, s.IsExternal && SubtitleGenerator.IsGenerated(s.Path)))
            .Select(s => Languages.ToTwoLetter(s.Language)).OfType<string>().ToHashSet(StringComparer.Ordinal);

    private IEnumerable<LibraryVideo> Walk()
    {
        var items = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            IsVirtualItem = false,
            Recursive = true,
        });
        foreach (var item in items)
        {
            if (item is not Video video || string.IsNullOrEmpty(video.Path) || video.RunTimeTicks is not > 0 || !File.Exists(video.Path))
            {
                continue;
            }

            var streams = _media.GetMediaStreams(item.Id);
            var audio = streams.Where(s => s.Type == MediaStreamType.Audio).OrderBy(s => s.Index).Select(s => ((string?)s.Language, s.IsDefault)).ToList();
            if (audio.Count == 0)
            {
                continue;
            }

            yield return new LibraryVideo(item, video, streams, audio, TimeSpan.FromTicks(video.RunTimeTicks!.Value));
        }
    }

    // One video, as walked
    private sealed record LibraryVideo(BaseItem Item, Video Video, IReadOnlyList<MediaStream> Streams, IReadOnlyList<(string? Language, bool IsDefault)> Audio, TimeSpan Duration);
}
