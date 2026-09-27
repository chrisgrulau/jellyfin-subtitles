using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.Subtitles.Pipeline;

/// <summary>
/// What a bulk action does to each selected result, as the single-item button of the same name would.
/// </summary>
public enum BulkAction
{
    /// <summary>Apply everything waiting for review.</summary>
    Apply,

    /// <summary>Decline everything waiting for review.</summary>
    Decline,

    /// <summary>Put the original back (or remove an added or generated subtitle).</summary>
    Undo,

    /// <summary>Forget the result so the subtitle is checked again (or the video searched again) on the next run.</summary>
    CheckAgain,
}

/// <summary>
/// Where a bulk job stands.
/// </summary>
public enum BulkState
{
    /// <summary>Waiting for a scheduled task or other work on subtitle files to finish (see <see cref="RunGate"/>).</summary>
    Waiting,

    /// <summary>Working through the items.</summary>
    Running,

    /// <summary>Every item was handled.</summary>
    Done,

    /// <summary>Stopped (by the administrator, or because the server is stopping) before every item was handled.</summary>
    Cancelled,
}

/// <summary>
/// An item a bulk job skipped or failed on.
/// </summary>
/// <param name="Id">The result's id.</param>
/// <param name="Headline">The video, short (as the results list shows it).</param>
/// <param name="Reason">Why, in words.</param>
public sealed record BulkIssue(string Id, string Headline, string Reason);

/// <summary>
/// A bulk job's progress (polled by the settings page).
/// </summary>
/// <param name="Id">The job's id.</param>
/// <param name="Action">What it does.</param>
/// <param name="State">Where it stands.</param>
/// <param name="Total">How many items it works through.</param>
/// <param name="Done">How many have been handled (succeeded, skipped or failed).</param>
/// <param name="Succeeded">How many were done.</param>
/// <param name="Skipped">Items left alone, with the reason (nothing waiting any more, the file changed since …).</param>
/// <param name="Failed">Items that went wrong unexpectedly, with the reason.</param>
/// <param name="Left">Items the action applied to that weren't included, past the cap per job (run it again for them).</param>
/// <param name="WaitingFor">What it waits for, while <see cref="BulkState.Waiting"/>.</param>
/// <param name="Summary">One line, when finished (also written to the Activity log).</param>
public sealed record BulkProgress(
    string Id,
    BulkAction Action,
    BulkState State,
    int Total,
    int Done,
    int Succeeded,
    IReadOnlyList<BulkIssue> Skipped,
    IReadOnlyList<BulkIssue> Failed,
    int Left,
    string? WaitingFor,
    string? Summary)
{
    /// <summary>Gets a value indicating whether the job has finished (done or cancelled).</summary>
    public bool Finished => State is BulkState.Done or BulkState.Cancelled;
}

/// <summary>
/// How many of a selection each bulk action applies to.
/// </summary>
/// <param name="Selected">How many results are selected.</param>
/// <param name="Apply">How many have something to apply.</param>
/// <param name="Decline">How many have something waiting for review to decline.</param>
/// <param name="Undo">How many hold this plugin's changes that can be undone.</param>
/// <param name="CheckAgain">How many can be checked again (not changed by this plugin, nothing waiting for review).</param>
/// <param name="Max">The most items one job handles.</param>
public sealed record BulkCounts(int Selected, int Apply, int Decline, int Undo, int CheckAgain, int Max);

/// <summary>
/// Which results a bulk action applies to, and the words for its outcome. The rules are the settings page's for showing
/// each row's button, so a bulk action never does what the page wouldn't offer for the row.
/// </summary>
public static class BulkRules
{
    /// <summary>The most items one bulk job handles.</summary>
    public const int MaxPerJob = 5000;

    /// <summary>
    /// Why an action doesn't apply to a result, or <c>null</c> when it does.
    /// </summary>
    /// <param name="action">The action.</param>
    /// <param name="r">The result.</param>
    /// <returns>The reason, or <c>null</c>.</returns>
    public static string? WhyNot(BulkAction action, SubtitleResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return action switch
        {
            BulkAction.Apply when !r.PendingReview => "Nothing is waiting for review any more.",
            BulkAction.Apply when r.Status != ResultStatus.Proposed && r.CleanupPending.Count == 0 && !r.Findings.Any(f => f.Suggestion is not null)
                => "Only lines with nothing heard are left: remove or decline them one at a time.",
            BulkAction.Decline when !r.PendingReview => "Nothing is waiting for review any more.",
            BulkAction.Undo when !r.Changed => "There's no change to undo.",
            BulkAction.Undo when r.Backup is null && r.Status is not (ResultStatus.Added or ResultStatus.Generated) => "The original isn't kept, so this can't be undone.",
            BulkAction.CheckAgain when r.Changed => "It holds this plugin's changes: undo them first, then check it again.",
            BulkAction.CheckAgain when r.PendingReview => "It is waiting for review: apply or decline it first.",
            _ => null,
        };
    }

    /// <summary>
    /// How many of a selection each action applies to.
    /// </summary>
    /// <param name="selected">The selected results.</param>
    /// <returns>The counts.</returns>
    public static BulkCounts Count(IReadOnlyCollection<SubtitleResult> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        int Of(BulkAction a) => selected.Count(r => WhyNot(a, r) is null);
        return new BulkCounts(selected.Count, Of(BulkAction.Apply), Of(BulkAction.Decline), Of(BulkAction.Undo), Of(BulkAction.CheckAgain), MaxPerJob);
    }

    /// <summary>
    /// The items a job for an action works through: the selected results it applies to, in order, at most
    /// <paramref name="max"/>; and how many it applies to past that.
    /// </summary>
    /// <param name="action">The action.</param>
    /// <param name="selected">The selected results, in the list's order.</param>
    /// <param name="max">The cap.</param>
    /// <returns>The items and how many were left out.</returns>
    public static (IReadOnlyList<SubtitleResult> Items, int Left) ForAction(BulkAction action, IEnumerable<SubtitleResult> selected, int max = MaxPerJob)
    {
        ArgumentNullException.ThrowIfNull(selected);
        var all = selected.Where(r => WhyNot(action, r) is null).ToList();
        var take = Math.Max(0, max);
        return (all.Take(take).ToList(), Math.Max(0, all.Count - take));
    }

    /// <summary>
    /// One line saying what a job did, for the page and the Activity log: "Applied 214 changes; 3 skipped: the subtitle
    /// changed since it was checked".
    /// </summary>
    /// <param name="p">The job's progress.</param>
    /// <returns>The line.</returns>
    public static string Summary(BulkProgress p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var n = p.Succeeded;
        var s = n == 1 ? string.Empty : "s";
        var did = p.Action switch
        {
            BulkAction.Apply => $"Applied {n} change{s}",
            BulkAction.Decline => $"Declined {n} review{s}",
            BulkAction.Undo => $"Undid {n} change{s}",
            _ => $"Queued {n} subtitle{s} to check again",
        };
        var parts = new List<string> { did };
        if (p.Skipped.Count > 0)
        {
            var reasons = p.Skipped.Select(i => i.Reason).Distinct(StringComparer.Ordinal).ToList();
            parts.Add(p.Skipped.Count.ToString(CultureInfo.InvariantCulture) + " skipped" + (reasons.Count == 1 ? ": " + Lower(reasons[0]).TrimEnd('.') : " (see the list)"));
        }

        if (p.Failed.Count > 0)
        {
            parts.Add(p.Failed.Count.ToString(CultureInfo.InvariantCulture) + " failed");
        }

        var line = string.Join("; ", parts) + ".";
        if (p.State == BulkState.Cancelled)
        {
            line = $"Stopped after {p.Done} of {p.Total}. " + line;
        }

        if (p.Left > 0)
        {
            line += $" {p.Left} more weren't included (at most {MaxPerJob.ToString("N0", CultureInfo.InvariantCulture)} at a time): run it again for them.";
        }

        return line;
    }

    private static string Lower(string text) => text.Length > 1 && char.IsUpper(text[0]) && !char.IsUpper(text[1]) ? char.ToLowerInvariant(text[0]) + text[1..] : text;
}

/// <summary>
/// Runs bulk actions on the results (apply, decline, undo or check again many at once) in the background, one job at a
/// time. A job takes the <see cref="RunGate"/> alone, so it never works on subtitle files while a nightly task, the
/// handling of new videos or "Restore all originals" does (it waits for them; they wait for it). Items are handled one by
/// one with the same rules and safety as the single-item actions (<see cref="SubtitleProcessor"/>): each is read again
/// first, one that no longer qualifies or whose file changed since is skipped with the reason, one whose file no longer
/// exists (replaced or removed) is skipped and its result cleared (see <see cref="StaleResults"/>), and anything unexpected
/// is recorded as failed without stopping the job. It can be stopped, and is when the server stops.
/// </summary>
public sealed class BulkJobs : IHostedService, IDisposable
{
    private readonly SubtitleProcessor _processor;
    private readonly RunGate _gate;
    private readonly SubtitleActivity? _activity;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _lock = new();
    private Job? _job;

    /// <summary>
    /// Initializes a new instance of the <see cref="BulkJobs"/> class.
    /// </summary>
    /// <param name="processor">Apply, decline, undo and check again.</param>
    /// <param name="gate">Keeps bulk work apart from the nightly tasks and the handling of new videos.</param>
    /// <param name="activity">Where the summary is written when a job ends (Jellyfin's Activity log), if anywhere.</param>
    public BulkJobs(SubtitleProcessor processor, RunGate gate, SubtitleActivity? activity = null)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _activity = activity;
    }

    /// <summary>Gets or sets a hook called after each item with how many are done (tests use it to stop mid-way).</summary>
    internal Action<int>? AfterItem { get; set; }

    /// <summary>Gets the running (or last) job's work, for tests to wait on.</summary>
    internal Task? Work
    {
        get
        {
            lock (_lock)
            {
                return _job?.Work;
            }
        }
    }

    /// <summary>
    /// The latest job's progress (running or finished), or <c>null</c> when none has run since the server started.
    /// </summary>
    /// <returns>The progress.</returns>
    public BulkProgress? Latest()
    {
        lock (_lock)
        {
            return _job is { } job ? Snapshot(job) : null;
        }
    }

    /// <summary>
    /// A job's progress.
    /// </summary>
    /// <param name="id">The job's id.</param>
    /// <returns>The progress, or <c>null</c> for an unknown job (only the latest is kept).</returns>
    public BulkProgress? Get(string id)
    {
        lock (_lock)
        {
            return _job is { } job && string.Equals(job.Id, id, StringComparison.Ordinal) ? Snapshot(job) : null;
        }
    }

    /// <summary>
    /// Starts a job in the background, unless one is still running.
    /// </summary>
    /// <param name="action">The action.</param>
    /// <param name="items">The results to work through, in order (see <see cref="BulkRules.ForAction"/>; at most
    /// <see cref="BulkRules.MaxPerJob"/>).</param>
    /// <param name="left">How many more the action applied to but weren't included (past the cap).</param>
    /// <param name="policies">The clean-up settings for Apply, read when the job starts working.</param>
    /// <param name="headline">The video's short name, for an item that is skipped or fails.</param>
    /// <returns>The new job's progress, or <c>null</c> with the running job's progress when one is still running.</returns>
    public (BulkProgress? Started, BulkProgress? Running) Start(BulkAction action, IReadOnlyList<SubtitleResult> items, int left, Func<Policies> policies, Func<SubtitleResult, string> headline)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(headline);
        if (items.Count > BulkRules.MaxPerJob)
        {
            throw new ArgumentException("A job handles at most " + BulkRules.MaxPerJob + " items.", nameof(items));
        }

        Job job;
        lock (_lock)
        {
            if (_job is { } running && !running.Finished)
            {
                return (null, Snapshot(running));
            }

#pragma warning disable CA2000 // The job owns it: disposed when the next job replaces it
            job = new Job(Guid.NewGuid().ToString("N"), action, [.. items.Select(r => r.Id)], left, CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token));
#pragma warning restore CA2000
            _job?.Cancel.Dispose();
            _job = job;
            job.Work = Task.Run(() => RunAsync(job, policies, headline));
            return (Snapshot(job), null);
        }
    }

    /// <summary>
    /// Stops a job: the item in hand is finished, the rest are left as they are.
    /// </summary>
    /// <param name="id">The job's id.</param>
    /// <returns><c>false</c> for an unknown or finished job.</returns>
    public bool Cancel(string id)
    {
        lock (_lock)
        {
            if (_job is not { } job || !string.Equals(job.Id, id, StringComparison.Ordinal) || job.Finished)
            {
                return false;
            }

            job.Cancel.Cancel();
            return true;
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (Work is { } work)
        {
            try
            {
                await work.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }

    private async Task RunAsync(Job job, Func<Policies> policies, Func<SubtitleResult, string> headline)
    {
        var token = job.Cancel.Token;
        try
        {
            using var hold = await _gate.EnterAloneAsync(WordsFor(job.Action), token).ConfigureAwait(false);
            lock (_lock)
            {
                job.State = BulkState.Running;
            }

            var settings = job.Action == BulkAction.Apply ? policies() : null;
            foreach (var id in job.Ids)
            {
                token.ThrowIfCancellationRequested();
                var (ok, issue, failed) = One(job.Action, id, settings, headline);
                int done;
                lock (_lock)
                {
                    job.Done++;
                    done = job.Done;
                    if (ok)
                    {
                        job.Succeeded++;
                    }
                    else if (failed)
                    {
                        job.Failed.Add(issue!);
                    }
                    else
                    {
                        job.Skipped.Add(issue!);
                    }
                }

                AfterItem?.Invoke(done);
            }

            Finish(job, BulkState.Done);
        }
        catch (OperationCanceledException)
        {
            Finish(job, BulkState.Cancelled);
        }
#pragma warning disable CA1031 // A job must always end with a state the page can show
        catch (Exception ex)
#pragma warning restore CA1031
        {
            lock (_lock)
            {
                job.Failed.Add(new BulkIssue(string.Empty, "The job", ex.Message));
            }

            Finish(job, BulkState.Cancelled);
        }
    }

    // One item, as its single-item button would do it: read again, checked again, then done
    private (bool Ok, BulkIssue? Issue, bool Failed) One(BulkAction action, string id, Policies? policies, Func<SubtitleResult, string> headline)
    {
        var r = _processor.Get(id);
        if (r is null)
        {
            return (false, new BulkIssue(id, "A subtitle", "It is no longer in the results."), false);
        }

        string Name()
        {
            try
            {
                return headline(r);
            }
#pragma warning disable CA1031 // The name is only for the list of skipped items
            catch (Exception)
#pragma warning restore CA1031
            {
                return r.Name;
            }
        }

        // Its video or subtitle file replaced or removed: skipped, and the result cleared
        if (_processor.ClearIfStale(id) is not null)
        {
            return (false, new BulkIssue(id, Name(), StaleResults.BulkSkipReason), false);
        }

        if (BulkRules.WhyNot(action, r) is { } why)
        {
            return (false, new BulkIssue(id, Name(), why), false);
        }

        try
        {
            switch (action)
            {
                case BulkAction.Apply:
                    _processor.Apply(id, policies!);
                    break;
                case BulkAction.Decline:
                    _processor.Decline(id);
                    break;
                case BulkAction.Undo:
                    _processor.Undo(id);
                    break;
                default:
                    _processor.CheckAgain(id);
                    break;
            }

            return (true, null, false);
        }
        catch (StaleResultException)
        {
            return (false, new BulkIssue(id, Name(), StaleResults.BulkSkipReason), false);
        }
        catch (InvalidOperationException ex)
        {
            return (false, new BulkIssue(id, Name(), ex.Message), false);
        }
#pragma warning disable CA1031 // One item going wrong (a file that can't be read or written) mustn't stop the others
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return (false, new BulkIssue(id, Name(), ex.Message), true);
        }
    }

    private void Finish(Job job, BulkState state)
    {
        BulkProgress done;
        lock (_lock)
        {
            job.State = state;
            done = Snapshot(job);
            job.Summary = BulkRules.Summary(done);
            done = done with { Summary = job.Summary };
        }

        try
        {
            _processor.FlushResults();
        }
#pragma warning disable CA1031 // Results are written again with the next change; the job's outcome stands
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        if (_activity is not null && done.Done > 0)
        {
            _ = _activity.NotifyBulkAsync(done.Id, done.Summary!, done.Failed.Count > 0 || done.Skipped.Count > 0);
        }
    }

    private BulkProgress Snapshot(Job job)
        => new(job.Id, job.Action, job.State, job.Ids.Count, job.Done, job.Succeeded, [.. job.Skipped], [.. job.Failed], job.Left, job.State == BulkState.Waiting ? _gate.Busy : null, job.Summary);

    private static string WordsFor(BulkAction action) => action switch
    {
        BulkAction.Apply => "applying results in bulk",
        BulkAction.Decline => "declining results in bulk",
        BulkAction.Undo => "undoing results in bulk",
        _ => "queuing results to check again in bulk",
    };

    private sealed class Job(string id, BulkAction action, List<string> ids, int left, CancellationTokenSource cancel)
    {
        public string Id { get; } = id;

        public BulkAction Action { get; } = action;

        public List<string> Ids { get; } = ids;

        public int Left { get; } = left;

        public CancellationTokenSource Cancel { get; } = cancel;

        public Task? Work { get; set; }

        public BulkState State { get; set; } = BulkState.Waiting;

        public int Done { get; set; }

        public int Succeeded { get; set; }

        public List<BulkIssue> Skipped { get; } = [];

        public List<BulkIssue> Failed { get; } = [];

        public string? Summary { get; set; }

        public bool Finished => State is BulkState.Done or BulkState.Cancelled;
    }
}
