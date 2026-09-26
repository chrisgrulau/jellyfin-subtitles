using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Secrets;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.SpeechToText;

namespace Jellyfin.Plugin.Subtitles.Candidates;

/// <summary>
/// What a subtitle site needs to know to find a video.
/// </summary>
/// <param name="ImdbId">IMDb id of the film, or of the show for an episode (<c>tt…</c>).</param>
/// <param name="TmdbId">TMDb id of the film or show.</param>
/// <param name="Season">Season number, for an episode.</param>
/// <param name="Episode">Episode number, for an episode.</param>
public sealed record VideoIds(string? ImdbId, string? TmdbId, int? Season, int? Episode)
{
    /// <summary>Gets a value indicating whether this is an episode.</summary>
    public bool IsEpisode => Season is not null && Episode is not null;
}

/// <summary>
/// Subtitles from SubDL (subdl.com), with the user's free API key.
/// <list type="bullet">
/// <item>Searches by IMDb or TMDb id (with season and episode for TV); only the two fixed SubDL hosts are contacted.</item>
/// <item>Downloads are zips, often a whole season: only the one subtitle file for the wanted episode is taken, and
/// only if exactly one file matches. Sizes are capped before and while reading.</item>
/// <item>SubDL puts the key into every download link, so links are never logged, shown or stored: a candidate's id is
/// the link without its query.</item>
/// </list>
/// </summary>
public sealed partial class SubDlSource : ICandidateSource
{
    /// <summary>SubDL's search API.</summary>
    public static readonly Uri SearchApi = new("https://api.subdl.com/api/v1/subtitles");

    /// <summary>SubDL's download host.</summary>
    public static readonly Uri DownloadHost = new("https://dl.subdl.com/");

    /// <summary>The largest download (zip) accepted.</summary>
    public const int MaxZipBytes = 10 * 1024 * 1024;

    private static readonly string[] TextExtensions = [".srt", ".ass", ".ssa", ".vtt"];

    private readonly HttpClient _http;
    private readonly string _key;
    private readonly Func<Guid, VideoIds?> _ids;
    private readonly ConcurrentDictionary<string, VideoIds> _wanted = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="SubDlSource"/> class.
    /// </summary>
    /// <param name="http">HTTP client.</param>
    /// <param name="key">The SubDL API key.</param>
    /// <param name="ids">Finds a library item's ids, season and episode.</param>
    public SubDlSource(HttpClient http, string key, Func<Guid, VideoIds?> ids)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _key = key;
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
    }

    /// <inheritdoc />
    public string Name => "SubDL";

    /// <inheritdoc />
    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(Guid itemId, string language, CancellationToken cancellationToken)
    {
        if (_ids(itemId) is not { } ids || Languages.ToTwoLetter(language) is not { } lang)
        {
            return [];
        }

        var query = new List<string> { "api_key=" + Uri.EscapeDataString(_key), "languages=" + lang.ToUpperInvariant(), "subs_per_page=30" };
        if (ids.ImdbId is { } imdb && ImdbPattern().IsMatch(imdb))
        {
            query.Add("imdb_id=" + imdb);
        }
        else if (ids.TmdbId is { } tmdb && tmdb.All(char.IsAsciiDigit) && tmdb.Length is > 0 and < 12)
        {
            query.Add("tmdb_id=" + tmdb);
        }
        else
        {
            return [];
        }

        query.Add("type=" + (ids.IsEpisode ? "tv" : "movie"));
        if (ids.IsEpisode)
        {
            query.Add(string.Create(CultureInfo.InvariantCulture, $"season_number={ids.Season}&episode_number={ids.Episode}"));
        }

        using var doc = await GetJsonAsync(new Uri(SearchApi.AbsoluteUri + "?" + string.Join('&', query)), cancellationToken).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("subtitles", out var subs) || subs.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<SubtitleCandidate>();
        foreach (var s in subs.EnumerateArray().Take(30))
        {
            // The id is the download path without its query (which holds the key)
            var url = Text(s, "url");
            var path = url?.Split('?', 2)[0];
            if (path is null || !DownloadPath().IsMatch(path))
            {
                continue;
            }

            // A subtitle made for another episode is left out; a whole-season pack is kept (the episode is picked later)
            if (ids.IsEpisode && s.TryGetProperty("episode", out var ep) && ep.ValueKind == JsonValueKind.Number && ep.GetInt32() != ids.Episode)
            {
                continue;
            }

            _wanted[path] = ids;
            list.Add(new SubtitleCandidate
            {
                Source = Name,
                Id = path,
                ReleaseName = Text(s, "release_name") ?? Text(s, "name") ?? string.Empty,
                Language = language,
                Format = "srt",
                HearingImpaired = s.TryGetProperty("hi", out var hi) && hi.ValueKind == JsonValueKind.True,
            });
        }

        return list;
    }

    /// <inheritdoc />
    public async Task<FetchedSubtitle?> FetchAsync(SubtitleCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!DownloadPath().IsMatch(candidate.Id))
        {
            return null;
        }

        var zip = await GetBytesAsync(new Uri(DownloadHost, candidate.Id.TrimStart('/') + "?api_key=" + Uri.EscapeDataString(_key)), cancellationToken).ConfigureAwait(false);
        if (zip is null)
        {
            return null;
        }

        _wanted.TryGetValue(candidate.Id, out var ids);
        return Pick(zip, ids, candidate.Language);
    }

    /// <summary>
    /// Takes the subtitle for a video out of a SubDL zip: the only text subtitle in it, or, from a season pack, the one
    /// file named for the wanted episode. Anything unclear gives nothing.
    /// </summary>
    /// <param name="zip">The zip.</param>
    /// <param name="ids">The video (season and episode for TV).</param>
    /// <param name="language">The language, for the result.</param>
    /// <returns>The subtitle, or <c>null</c>.</returns>
    public static FetchedSubtitle? Pick(byte[] zip, VideoIds? ids, string? language)
    {
        ArgumentNullException.ThrowIfNull(zip);
        try
        {
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            var subs = archive.Entries
                .Where(e => e.Length is > 0 and <= SubtitleReader.MaxBytes && TextExtensions.Contains(Path.GetExtension(e.Name), StringComparer.OrdinalIgnoreCase))
                .ToList();
            ZipArchiveEntry? chosen = subs.Count == 1 ? subs[0] : null;
            if (subs.Count > 1 && ids is { IsEpisode: true })
            {
                var matches = subs.Where(e => IsEpisode(e.Name, ids.Season!.Value, ids.Episode!.Value)).ToList();

                // Some packs only number their files ("02 Title.en.srt"): used only when no file has a season/episode code
                if (matches.Count == 0 && !subs.Any(e => EpisodeCode().IsMatch(e.Name.ToUpperInvariant())))
                {
                    matches = [.. subs.Where(e => LeadingNumber().Match(e.Name) is { Success: true } m && int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) == ids.Episode)];
                }

                chosen = matches.Count == 1 ? matches[0] : null;
            }

            if (chosen is null)
            {
                return null;
            }

            // Read with a limit (the length in the zip's directory can't be trusted)
            using var stream = chosen.Open();
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > SubtitleReader.MaxBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            var ext = Path.GetExtension(chosen.Name).TrimStart('.');
            return new FetchedSubtitle(buffer.ToArray(), string.Equals(ext, "ssa", StringComparison.OrdinalIgnoreCase) ? "ass" : ext, language);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a file name is for an episode: <c>S01E02</c>, <c>1x02</c> or <c>.E02.</c> style, not <c>S01E12</c>.
    /// </summary>
    /// <param name="name">The file name.</param>
    /// <param name="season">Season.</param>
    /// <param name="episode">Episode.</param>
    /// <returns><c>true</c> if it names that episode.</returns>
    public static bool IsEpisode(string name, int season, int episode)
    {
        ArgumentNullException.ThrowIfNull(name);
        var n = name.ToUpperInvariant();
        foreach (Match m in EpisodeCode().Matches(n))
        {
            var s = m.Groups["s"].Success ? int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture) : (int?)null;
            var e = int.Parse(m.Groups["e"].Value, CultureInfo.InvariantCulture);
            if (e == episode && (s is null || s == season))
            {
                return true;
            }
        }

        return false;
    }

    private static string? Text(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private async Task<JsonDocument> GetJsonAsync(Uri address, CancellationToken cancellationToken)
    {
        var bytes = await GetBytesAsync(address, cancellationToken).ConfigureAwait(false)
            ?? throw new HttpRequestException("SubDL's reply was too large or empty.");
        try
        {
            return JsonDocument.Parse(bytes);
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException("SubDL's reply wasn't valid JSON.", ex);
        }
    }

    // Only SubDL's two hosts, over HTTPS, with a size cap; errors never carry the key (it is in the address)
    private async Task<byte[]?> GetBytesAsync(Uri address, CancellationToken cancellationToken)
    {
        if (address.Scheme != Uri.UriSchemeHttps || (address.Host != SearchApi.Host && address.Host != DownloadHost.Host))
        {
            throw new HttpRequestException("Refused: SubDL requests only go to SubDL.");
        }

        try
        {
            using var response = await _http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException("SubDL answered " + (int)response.StatusCode + ".", null, response.StatusCode);
            }

            if (response.Content.Headers.ContentLength > MaxZipBytes)
            {
                return null;
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > MaxZipBytes)
                    {
                        return null;
                    }

                    await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                return buffer.ToArray();
            }
        }
        catch (HttpRequestException ex) when (ex.Message.Contains(_key, StringComparison.Ordinal))
        {
            throw new HttpRequestException(Redaction.Redact(ex.Message, [_key]), null, ex.StatusCode);
        }
    }

    [GeneratedRegex(@"^(\d{1,3})[ ._-]+\D")]
    private static partial Regex LeadingNumber();

    [GeneratedRegex(@"^tt\d{5,10}$")]
    private static partial Regex ImdbPattern();

    [GeneratedRegex(@"^/subtitle/[0-9A-Za-z_-]{1,80}\.zip$")]
    private static partial Regex DownloadPath();

    [GeneratedRegex(@"(?:S(?<s>\d{1,2})[ ._-]?E(?<e>\d{1,3})|(?<s>\d{1,2})X(?<e>\d{2,3})|(?:^|[ ._-])E(?:P)?(?<e>\d{2,3}))(?!\d)")]
    private static partial Regex EpisodeCode();
}
