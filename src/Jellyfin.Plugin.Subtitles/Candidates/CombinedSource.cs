using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Subtitles.Candidates;

/// <summary>
/// Several sources as one: every source is searched (one that fails is skipped, so a site being down never stops the
/// others; one whose daily allowance is used up is left out for the rest of the run) and a candidate is fetched from the
/// source that offered it.
/// </summary>
public sealed class CombinedSource : ICandidateSource
{
    private readonly IReadOnlyList<ICandidateSource> _sources;
    private readonly HashSet<string> _outForToday = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="CombinedSource"/> class.
    /// </summary>
    /// <param name="sources">The sources, in order of preference.</param>
    public CombinedSource(IReadOnlyList<ICandidateSource> sources)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
    }

    /// <inheritdoc />
    public string Name => string.Join(", ", _sources.Select(s => s.Name));

    /// <summary>Gets why a source couldn't be searched, from the last search (for the log).</summary>
    public IReadOnlyList<string> Problems { get; private set; } = [];

    /// <inheritdoc />
    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(Guid itemId, string language, CancellationToken cancellationToken)
    {
        var all = new List<SubtitleCandidate>();
        var problems = new List<string>();
        Exception? stopped = null;
        foreach (var source in _sources.Where(s => !_outForToday.Contains(s.Name)))
        {
            try
            {
                all.AddRange((await source.SearchAsync(itemId, language, cancellationToken).ConfigureAwait(false)).Select(c => c with { Source = source.Name }));
            }
            catch (Exception ex) when (Pipeline.FindRules.StopsTheRun(ex))
            {
                // This provider's allowance is used up (or it can't sign in): the others carry on without it
                _outForToday.Add(source.Name);
                problems.Add(source.Name + ": " + ex.Message);
                stopped = ex;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                problems.Add(source.Name + ": " + ex.Message);
            }
        }

        Problems = problems;

        // Only when every provider is out for the day does the run stop
        if (stopped is not null && _sources.All(s => _outForToday.Contains(s.Name)))
        {
            throw stopped;
        }

        return all;
    }

    /// <inheritdoc />
    public Task<FetchedSubtitle?> FetchAsync(SubtitleCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return _sources.FirstOrDefault(s => s.Name == candidate.Source) is { } source
            ? source.FetchAsync(candidate, cancellationToken)
            : Task.FromResult<FetchedSubtitle?>(null);
    }
}
