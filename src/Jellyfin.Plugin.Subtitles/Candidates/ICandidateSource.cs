using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Subtitles.Candidates;

/// <summary>
/// A downloaded subtitle, not yet saved anywhere.
/// </summary>
/// <param name="Content">The file bytes.</param>
/// <param name="Format">Its format as the source reports it (<c>srt</c>, <c>ass</c> …).</param>
/// <param name="Language">Its language as the source reports it, if any.</param>
public sealed record FetchedSubtitle(ReadOnlyMemory<byte> Content, string Format, string? Language);

/// <summary>
/// Somewhere subtitles can come from. Sources are tried in order; new ones (extra providers, embedded tracks) plug in
/// here.
/// </summary>
public interface ICandidateSource
{
    /// <summary>Gets a short name shown in results and provenance records.</summary>
    string Name { get; }

    /// <summary>
    /// Lists the subtitles this source offers for a video.
    /// </summary>
    /// <param name="itemId">The Jellyfin item id of the video.</param>
    /// <param name="language">Language wanted (ISO 639-2).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The candidates, unranked.</returns>
    Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(Guid itemId, string language, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads a candidate without saving it (it is only filed after passing the checks).
    /// </summary>
    /// <param name="candidate">A candidate this source returned.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The subtitle, or <c>null</c> if it could not be fetched.</returns>
    Task<FetchedSubtitle?> FetchAsync(SubtitleCandidate candidate, CancellationToken cancellationToken);
}
