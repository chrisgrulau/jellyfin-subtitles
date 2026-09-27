using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Subtitles.Pipeline;

namespace Jellyfin.Plugin.Subtitles.Api;

/// <summary>
/// A filter and search, as the results list has them (see <see cref="ResultQuery.Matches"/>).
/// </summary>
public sealed record BulkFilter
{
    /// <summary>Gets the filter: empty for everything; <c>waiting</c>, <c>wholefile</c>, <c>queued</c>, <c>fellback</c>, or a status name.</summary>
    public string? Filter { get; init; }

    /// <summary>Gets the search text.</summary>
    public string? Q { get; init; }
}

/// <summary>
/// Body of the bulk endpoints: the action and the selection, either results by id or every result matching a filter
/// and search (less any unticked since).
/// </summary>
public sealed record BulkRequest
{
    /// <summary>Gets the action: <c>Apply</c>, <c>Decline</c>, <c>Undo</c> or <c>CheckAgain</c> (not needed for a preview).</summary>
    public string? Action { get; init; }

    /// <summary>Gets the results chosen one by one.</summary>
    public IReadOnlyList<string>? Ids { get; init; }

    /// <summary>Gets the filter and search whose every matching result is chosen.</summary>
    public BulkFilter? Filter { get; init; }

    /// <summary>Gets results left out of a filter's selection (unticked after "Select all matching").</summary>
    public IReadOnlyList<string>? Except { get; init; }
}

/// <summary>
/// Turns a bulk request's selection into results, on the server (a filter's selection covers the whole list, not just
/// the rows the page has loaded).
/// </summary>
public static class BulkSelection
{
    /// <summary>The most ids a request may name.</summary>
    public const int MaxIds = 20_000;

    /// <summary>
    /// Reads the action.
    /// </summary>
    /// <param name="text">The action's name (any case).</param>
    /// <returns>The action, or <c>null</c> if it isn't one.</returns>
    public static BulkAction? ActionOf(string? text)
        => Enum.TryParse<BulkAction>(text, ignoreCase: true, out var a) && Enum.IsDefined(a) && !int.TryParse(text, out _) ? a : null;

    /// <summary>
    /// The selected results, in the list's order (waiting for review first, then newest).
    /// </summary>
    /// <param name="ordered">All results, in the list's order.</param>
    /// <param name="request">The selection.</param>
    /// <returns>The results, or why the selection isn't valid.</returns>
    public static (IReadOnlyList<SubtitleResult>? Selected, string? Problem) Resolve(IReadOnlyList<SubtitleResult> ordered, BulkRequest request)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        ArgumentNullException.ThrowIfNull(request);
        if ((request.Ids is null) == (request.Filter is null))
        {
            return (null, "Choose the subtitles either one by one or by a filter.");
        }

        if ((request.Ids?.Count ?? 0) + (request.Except?.Count ?? 0) > MaxIds)
        {
            return (null, "That's too many subtitles chosen one by one; use \"Select all matching\" instead.");
        }

        if (request.Ids is { } ids)
        {
            var chosen = new HashSet<string>(ids.Where(i => i is not null), StringComparer.Ordinal);
            return ([.. ordered.Where(r => chosen.Contains(r.Id))], null);
        }

        var q = request.Filter!.Q;
        q = q?.Length > 200 ? q[..200] : q;
        var except = new HashSet<string>((request.Except ?? []).Where(i => i is not null), StringComparer.Ordinal);
        return ([.. ordered.Where(r => !except.Contains(r.Id) && ResultQuery.Matches(r, request.Filter.Filter, q))], null);
    }
}
