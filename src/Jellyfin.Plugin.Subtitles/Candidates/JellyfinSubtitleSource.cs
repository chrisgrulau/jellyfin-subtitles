using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.Subtitles.Candidates;

/// <summary>
/// Candidates from the subtitle providers installed in Jellyfin (for example the OpenSubtitles plugin, signed in with
/// the user's own account and quota). The plugin needs no credentials of its own for these.
/// </summary>
public sealed class JellyfinSubtitleSource : ICandidateSource
{
    private readonly ILibraryManager _library;
    private readonly ISubtitleManager _subtitles;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinSubtitleSource"/> class.
    /// </summary>
    /// <param name="library">Jellyfin's library manager.</param>
    /// <param name="subtitles">Jellyfin's subtitle manager.</param>
    public JellyfinSubtitleSource(ILibraryManager library, ISubtitleManager subtitles)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _subtitles = subtitles ?? throw new ArgumentNullException(nameof(subtitles));
    }

    /// <inheritdoc />
    public string Name => "Jellyfin providers";

    /// <inheritdoc />
    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(Guid itemId, string language, CancellationToken cancellationToken)
    {
        if (_library.GetItemById(itemId) is not Video video)
        {
            return [];
        }

        // Jellyfin answers an empty list when no provider is installed: that isn't "nothing offered"
        if (_subtitles.GetSupportedProviders(video).Length == 0)
        {
            throw new NoSourceAnsweredException("No subtitle provider (such as the OpenSubtitles plugin) is installed in Jellyfin for this kind of video.") { NoneAvailable = true };
        }

        // Not perfect-match only: fingerprint matches are scored highly, but other good releases are worth checking too
        try
        {
            var results = await _subtitles.SearchSubtitles(video, language, null, true, cancellationToken).ConfigureAwait(false);
            return [.. results.Select(ToCandidate)];
        }
        catch (Exception ex) when (ProviderFailures.Classify(ex) is { } failure)
        {
            throw failure;
        }
    }

    /// <inheritdoc />
    public async Task<FetchedSubtitle?> FetchAsync(SubtitleCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        SubtitleResponse response;
        try
        {
            response = await _subtitles.GetRemoteSubtitles(candidate.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ProviderFailures.Classify(ex) is { } failure)
        {
            // A provider's own limit or sign-in failure is classified here, where its foreign exception enters the plugin
            throw failure;
        }

        if (response?.Stream is null)
        {
            return null;
        }

        var stream = response.Stream;
        await using (stream.ConfigureAwait(false))
        {
            // Stops reading as soon as it's too big to be a subtitle
            var bytes = await Formats.SubtitleReader.ReadLimitedAsync(stream, cancellationToken).ConfigureAwait(false);
            return bytes is null ? null : new FetchedSubtitle(bytes, response.Format ?? candidate.Format ?? "srt", response.Language ?? candidate.Language);
        }
    }

    /// <summary>
    /// Maps a Jellyfin search result to a candidate.
    /// </summary>
    /// <param name="r">The result.</param>
    /// <returns>The candidate.</returns>
    public static SubtitleCandidate ToCandidate(RemoteSubtitleInfo r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new SubtitleCandidate
        {
            Source = r.ProviderName ?? "Jellyfin",
            Id = r.Id ?? string.Empty,
            ReleaseName = r.Name ?? string.Empty,
            Format = r.Format,
            Language = r.ThreeLetterISOLanguageName,
            FrameRate = r.FrameRate,
            DownloadCount = r.DownloadCount,
            Rating = r.CommunityRating,
            IsHashMatch = r.IsHashMatch,
            MachineTranslated = r.MachineTranslated,
            AiTranslated = r.AiTranslated,
            HearingImpaired = r.HearingImpaired,
            Forced = r.Forced,
            Uploader = r.Author,
        };
    }
}
