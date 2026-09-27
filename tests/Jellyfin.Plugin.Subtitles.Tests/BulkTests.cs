using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.Api;
using Jellyfin.Plugin.Subtitles.Configuration;
using Jellyfin.Plugin.Subtitles.Discrepancy;
using Jellyfin.Plugin.Subtitles.Pipeline;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// Bulk actions on the results: selection, rules, the background job and its gate. Invented dialogue throughout.
public sealed class BulkTests : IDisposable
{
    private const string Original = "1\r\n00:00:01,000 --> 00:00:02,000\r\nHello there.\r\n\r\n2\r\n00:00:05,000 --> 00:00:06,000\r\nGoodbye now.\r\n\r\n";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "subs-bulk-" + Guid.NewGuid().ToString("N"));
    private readonly ResultStore _store;
    private readonly SubtitleFiles _files;
    private readonly SubtitleProcessor _processor;
    private readonly RunGate _gate = new();
    private readonly BulkJobs _jobs;

    public BulkTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new ResultStore(Path.Combine(_dir, "results.json"));
        _files = new SubtitleFiles(Path.Combine(_dir, "originals"));
        _processor = new SubtitleProcessor(_store, _files);
        _jobs = new BulkJobs(_processor, _gate);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _jobs.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Apply_over_mixed_items_applies_the_valid_ones_and_skips_changed_files_with_the_reason()
    {
        var good = Proposed("a.en.srt");
        var alsoGood = Proposed("b.en.srt");
        var edited = Proposed("c.en.srt");
        File.AppendAllText(edited, "3\r\n00:00:09,000 --> 00:00:10,000\r\nMine.\r\n\r\n");
        var declinedMeanwhile = Proposed("d.en.srt");
        var inSync = InSync("e.en.srt");

        var (items, left) = BulkRules.ForAction(BulkAction.Apply, _processor.Ordered());
        Assert.Equal(4, items.Count);
        Assert.Equal(0, left);
        Assert.DoesNotContain(items, r => r.SubtitlePath == inSync);
        _processor.Decline(ResultStore.IdFor(declinedMeanwhile));

        var progress = await Run(BulkAction.Apply, items);

        Assert.Equal(BulkState.Done, progress.State);
        Assert.Equal(4, progress.Total);
        Assert.Equal(4, progress.Done);
        Assert.Equal(2, progress.Succeeded);
        Assert.Equal(2, progress.Skipped.Count);
        Assert.Empty(progress.Failed);
        Assert.Contains(progress.Skipped, s => s.Id == ResultStore.IdFor(edited) && s.Reason.Contains("changed since", StringComparison.Ordinal) && s.Headline == "Film c");
        Assert.Contains(progress.Skipped, s => s.Id == ResultStore.IdFor(declinedMeanwhile) && s.Reason.Contains("Nothing is waiting", StringComparison.Ordinal));
        Assert.Equal(ResultStatus.Corrected, _store.ForPath(good)!.Status);
        Assert.Equal(ResultStatus.Corrected, _store.ForPath(alsoGood)!.Status);
        Assert.Equal(ResultStatus.Proposed, _store.ForPath(edited)!.Status);
        Assert.Contains("Mine.", File.ReadAllText(edited), StringComparison.Ordinal);
        Assert.StartsWith("Applied 2 changes; 2 skipped (see the list).", progress.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Undo_refuses_files_edited_since_and_check_again_forgets_the_rest()
    {
        var undone = Corrected("a.en.srt");
        var edited = Corrected("b.en.srt");
        File.AppendAllText(edited, "\r\n");

        var progress = await Run(BulkAction.Undo, BulkRules.ForAction(BulkAction.Undo, _processor.Ordered()).Items);

        Assert.Equal(1, progress.Succeeded);
        Assert.Single(progress.Skipped);
        Assert.Contains("changed after", progress.Skipped[0].Reason, StringComparison.Ordinal);
        Assert.Equal("Undid 1 change; 1 skipped: the subtitle was changed after this plugin changed it; undo would lose that change.", progress.Summary);
        Assert.Equal(Original, File.ReadAllText(undone));

        // The undone one can now be checked again; the edited one still holds this plugin's change, so it isn't included
        var (again, _) = BulkRules.ForAction(BulkAction.CheckAgain, _processor.Ordered());
        Assert.Single(again);
        var checkAgain = await Run(BulkAction.CheckAgain, again);
        Assert.Equal(1, checkAgain.Succeeded);
        Assert.Null(_store.ForPath(undone));
        Assert.NotNull(_store.ForPath(edited));
    }

    [Fact]
    public async Task Decline_leaves_the_files_alone()
    {
        var a = Proposed("a.en.srt");
        var progress = await Run(BulkAction.Decline, BulkRules.ForAction(BulkAction.Decline, _processor.Ordered()).Items);
        Assert.Equal("Declined 1 review.", progress.Summary);
        Assert.Equal(ResultStatus.Declined, _store.ForPath(a)!.Status);
        Assert.Equal(Original, File.ReadAllText(a));
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_counts_as_failed_without_stopping_the_job()
    {
        var gone = Proposed("a.en.srt");
        var fine = Proposed("b.en.srt");
        File.Delete(gone);

        var progress = await Run(BulkAction.Apply, BulkRules.ForAction(BulkAction.Apply, _processor.Ordered()).Items);

        Assert.Equal(1, progress.Succeeded);
        Assert.Single(progress.Failed);
        Assert.Equal(ResultStore.IdFor(gone), progress.Failed[0].Id);
        Assert.Equal(ResultStatus.Corrected, _store.ForPath(fine)!.Status);
        Assert.EndsWith("1 failed.", progress.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_filter_selection_resolves_the_same_set_as_the_list_query()
    {
        for (var i = 0; i < 130; i++)
        {
            _store.Put(new SubtitleResult
            {
                Id = "r" + i,
                SubtitlePath = Path.Combine(_dir, (i % 3 == 0 ? "Night Garden " : "Other ") + i + ".en.srt"),
                Name = "Film " + i,
                Status = i % 4 == 0 ? ResultStatus.Proposed : ResultStatus.InSync,
                Time = DateTimeOffset.UnixEpoch.AddMinutes(i),
            });
        }

        var ordered = _processor.Ordered();
        foreach (var (filter, q) in new[] { ("waiting", ""), ("", "night garden"), ("InSync", "garden"), ("", ""), ("nonsense", "") })
        {
            var listed = new List<string>();
            int? offset = 0;
            while (offset is { } at)
            {
                var page = ResultQuery.Page(ordered, filter, q, at, ResultQuery.MaxPage, _ => null!);
                listed.AddRange(page.Items.Select(r => r.Result.Id));
                offset = page.Next;
            }

            var (selected, problem) = BulkSelection.Resolve(ordered, new BulkRequest { Filter = new BulkFilter { Filter = filter, Q = q } });
            Assert.Null(problem);
            Assert.Equal(listed, selected!.Select(r => r.Id));
        }

        var (except, _) = BulkSelection.Resolve(ordered, new BulkRequest { Filter = new BulkFilter { Filter = "waiting" }, Except = ["r0", "r4"] });
        Assert.Equal(31, except!.Count);
        Assert.DoesNotContain(except, r => r.Id is "r0" or "r4");

        var (byId, _) = BulkSelection.Resolve(ordered, new BulkRequest { Ids = ["r5", "r1", "missing", "r5"] });
        Assert.Equal(["r5", "r1"], byId!.Select(r => r.Id));
        Assert.NotNull(BulkSelection.Resolve(ordered, new BulkRequest()).Problem);
        Assert.NotNull(BulkSelection.Resolve(ordered, new BulkRequest { Ids = [], Filter = new BulkFilter() }).Problem);
        Assert.NotNull(BulkSelection.Resolve(ordered, new BulkRequest { Ids = [.. Enumerable.Range(0, BulkSelection.MaxIds + 1).Select(i => "x" + i)] }).Problem);
    }

    [Fact]
    public void A_job_is_capped_and_says_how_many_were_left_out()
    {
        var many = Enumerable.Range(0, BulkRules.MaxPerJob + 7).Select(i => new SubtitleResult { Id = "p" + i, SubtitlePath = "/x/" + i + ".srt", Status = ResultStatus.Proposed }).ToList();
        many.Add(new SubtitleResult { Id = "done", SubtitlePath = "/x/done.srt", Status = ResultStatus.InSync });

        var (items, left) = BulkRules.ForAction(BulkAction.Apply, many);
        Assert.Equal(BulkRules.MaxPerJob, items.Count);
        Assert.Equal(7, left);
        Assert.Equal("p0", items[0].Id);
        Assert.Throws<ArgumentException>(() => _jobs.Start(BulkAction.Apply, many, 0, Policies, r => r.Name));

        var summary = BulkRules.Summary(new BulkProgress("j", BulkAction.Apply, BulkState.Done, 5000, 5000, 5000, [], [], 7, null, null));
        Assert.Equal("Applied 5000 changes. 7 more weren't included (at most 5,000 at a time): run it again for them.", summary);
    }

    [Fact]
    public async Task Only_one_job_runs_at_a_time()
    {
        Proposed("a.en.srt");
        Proposed("b.en.srt");
        var nightly = await _gate.EnterSharedAsync(Ct);
        var items = BulkRules.ForAction(BulkAction.Apply, _processor.Ordered()).Items;

        var (first, none) = _jobs.Start(BulkAction.Apply, items, 0, Policies, r => r.Name);
        Assert.NotNull(first);
        Assert.Null(none);
        var (second, running) = _jobs.Start(BulkAction.Decline, items, 0, Policies, r => r.Name);
        Assert.Null(second);
        Assert.Equal(first!.Id, running!.Id);

        nightly.Dispose();
        await _jobs.Work!.WaitAsync(Patience, Ct);

        // Once it has finished, another may start
        var (third, _) = _jobs.Start(BulkAction.Undo, BulkRules.ForAction(BulkAction.Undo, _processor.Ordered()).Items, 0, Policies, r => r.Name);
        Assert.NotNull(third);
        await _jobs.Work!.WaitAsync(Patience, Ct);
        Assert.Null(_jobs.Get(first.Id));
        Assert.Equal(2, _jobs.Get(third!.Id)!.Succeeded);
    }

    [Fact]
    public async Task Progress_is_reported_item_by_item()
    {
        foreach (var n in "abcd")
        {
            Proposed(n + ".en.srt");
        }

        var seen = new List<BulkProgress>();
        _jobs.AfterItem = _ => seen.Add(_jobs.Latest()!);
        var (started, _) = _jobs.Start(BulkAction.Decline, BulkRules.ForAction(BulkAction.Decline, _processor.Ordered()).Items, 0, Policies, r => r.Name);
        Assert.Equal(4, started!.Total);
        Assert.Equal(0, started.Done);
        Assert.False(started.Finished);
        await _jobs.Work!.WaitAsync(Patience, Ct);

        Assert.Equal([1, 2, 3, 4], seen.Select(p => p.Done));
        Assert.All(seen, p => Assert.Equal(BulkState.Running, p.State));
        var end = _jobs.Get(started.Id)!;
        Assert.True(end.Finished);
        Assert.Equal(BulkState.Done, end.State);
        Assert.Equal(4, end.Succeeded);
        Assert.Equal("Declined 4 reviews.", end.Summary);
    }

    [Fact]
    public async Task A_job_can_be_stopped_mid_way_and_the_rest_are_left_alone()
    {
        var files = "abc".Select(n => Proposed(n + ".en.srt")).ToList();
        string? id = null;
        _jobs.AfterItem = done =>
        {
            if (done == 1)
            {
                Assert.True(_jobs.Cancel(id!));
            }
        };
        id = _jobs.Start(BulkAction.Apply, BulkRules.ForAction(BulkAction.Apply, _processor.Ordered()).Items, 0, Policies, r => r.Name).Started!.Id;
        await _jobs.Work!.WaitAsync(Patience, Ct);

        var end = _jobs.Get(id)!;
        Assert.Equal(BulkState.Cancelled, end.State);
        Assert.Equal(1, end.Done);
        Assert.Equal(1, files.Count(f => _store.ForPath(f)!.Status == ResultStatus.Corrected));
        Assert.StartsWith("Stopped after 1 of 3. Applied 1 change.", end.Summary, StringComparison.Ordinal);
        Assert.False(_jobs.Cancel(id));
        Assert.Null(_gate.Busy);
    }

    [Fact]
    public async Task A_job_waiting_for_a_nightly_task_says_so_and_stops_when_the_server_stops()
    {
        Proposed("a.en.srt");
        using var nightly = await _gate.EnterSharedAsync(Ct);
        var started = _jobs.Start(BulkAction.Apply, BulkRules.ForAction(BulkAction.Apply, _processor.Ordered()).Items, 0, Policies, r => r.Name).Started!;

        var waiting = _jobs.Get(started.Id)!;
        Assert.Equal(BulkState.Waiting, waiting.State);
        Assert.Equal("a Shoal Subtitles scheduled task is running", waiting.WaitingFor);

        await _jobs.StopAsync(Ct).WaitAsync(Patience, Ct);
        var end = _jobs.Get(started.Id)!;
        Assert.Equal(BulkState.Cancelled, end.State);
        Assert.Equal(0, end.Done);
        Assert.Equal(ResultStatus.Proposed, _store.All().Single().Status);
    }

    [Fact]
    public async Task A_running_job_holds_the_gate_alone_so_nightly_tasks_and_new_videos_wait()
    {
        Proposed("a.en.srt");
        Proposed("b.en.srt");
        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _jobs.AfterItem = done =>
        {
            if (done == 1)
            {
                reached.Set();
                release.Wait(Patience);
            }
        };
        _jobs.Start(BulkAction.Apply, BulkRules.ForAction(BulkAction.Apply, _processor.Ordered()).Items, 0, Policies, r => r.Name);
        Assert.True(reached.Wait(Patience, Ct));

        Assert.Equal("applying results in bulk", _gate.Busy);
        Assert.Null(_gate.TryEnterAlone("handling new videos"));
        var nightly = _gate.EnterSharedAsync(Ct);
        Assert.False(nightly.IsCompleted);

        release.Set();
        await _jobs.Work!.WaitAsync(Patience, Ct);
        using var hold = await nightly.WaitAsync(Patience, Ct);
        Assert.Equal(2, _jobs.Latest()!.Succeeded);
    }

    [Fact]
    public async Task Taking_the_gate_alone_waits_for_every_holder_and_can_be_cancelled()
    {
        var one = await _gate.EnterSharedAsync(Ct);
        var two = await _gate.EnterSharedAsync(Ct);
        var alone = _gate.EnterAloneAsync("bulk", Ct);
        Assert.False(alone.IsCompleted);
        one.Dispose();
        Assert.False(alone.IsCompleted);
        two.Dispose();
        using (await alone.WaitAsync(Patience, Ct))
        {
            Assert.Equal("bulk", _gate.Busy);
            using var cancel = new CancellationTokenSource();
            var other = _gate.EnterAloneAsync("another", cancel.Token);
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => other);
        }

        Assert.Null(_gate.Busy);
    }

    [Fact]
    public void The_rules_match_the_rows_buttons_and_count_a_selection()
    {
        LineFinding Extra() => new(5, "Goodbye now.", null, DiscrepancyFinder.Extra, "nothing heard") { From = DiscrepancyReview.WholeFile };
        var proposed = new SubtitleResult { Id = "1", SubtitlePath = "/a", Status = ResultStatus.Proposed };
        var onlyExtra = new SubtitleResult { Id = "2", SubtitlePath = "/b", Status = ResultStatus.Corrected, Changed = true, Backup = "x", Findings = [Extra()] };
        var added = new SubtitleResult { Id = "3", SubtitlePath = "/c", Status = ResultStatus.Added, Changed = true };
        var inSync = new SubtitleResult { Id = "4", SubtitlePath = "/d", Status = ResultStatus.InSync };
        var noBackup = new SubtitleResult { Id = "5", SubtitlePath = "/e", Status = ResultStatus.Corrected, Changed = true };

        Assert.Null(BulkRules.WhyNot(BulkAction.Apply, proposed));
        Assert.Contains("one at a time", BulkRules.WhyNot(BulkAction.Apply, onlyExtra), StringComparison.Ordinal);
        Assert.Null(BulkRules.WhyNot(BulkAction.Decline, onlyExtra));
        Assert.NotNull(BulkRules.WhyNot(BulkAction.Decline, inSync));
        Assert.Null(BulkRules.WhyNot(BulkAction.Undo, added));
        Assert.NotNull(BulkRules.WhyNot(BulkAction.Undo, noBackup));
        Assert.NotNull(BulkRules.WhyNot(BulkAction.Undo, inSync));
        Assert.Null(BulkRules.WhyNot(BulkAction.CheckAgain, inSync));
        Assert.NotNull(BulkRules.WhyNot(BulkAction.CheckAgain, proposed));
        Assert.NotNull(BulkRules.WhyNot(BulkAction.CheckAgain, added));

        Assert.Equal(new BulkCounts(5, 1, 2, 2, 1, BulkRules.MaxPerJob), BulkRules.Count([proposed, onlyExtra, added, inSync, noBackup]));

        Assert.Equal(BulkAction.CheckAgain, BulkSelection.ActionOf("checkagain"));
        Assert.Equal(BulkAction.Apply, BulkSelection.ActionOf("Apply"));
        Assert.Null(BulkSelection.ActionOf("1"));
        Assert.Null(BulkSelection.ActionOf("Delete"));
        Assert.Null(BulkSelection.ActionOf(null));
    }

    [Fact]
    public void Apply_all_suggestions_applies_every_line_with_one_and_keeps_lines_with_nothing_heard()
    {
        var path = Path.Combine(_dir, "a.en.srt");
        File.WriteAllText(path, Original);
        var id = ResultStore.IdFor(path);
        _store.Put(new SubtitleResult
        {
            Id = id,
            SubtitlePath = path,
            Name = "Film a",
            Status = ResultStatus.InSync,
            Fingerprint = SubtitleFiles.Fingerprint(Encoding.UTF8.GetBytes(Original)),
            Findings =
            [
                new LineFinding(1, "Hello there.", "Hello, Theo.", DiscrepancyFinder.Name, "a name differs"),
                new LineFinding(5, "Goodbye now.", null, DiscrepancyFinder.Extra, "nothing heard") { From = DiscrepancyReview.WholeFile },
                new LineFinding(20, "Not in the file.", "Something else.", DiscrepancyFinder.Words, "words missing") { From = DiscrepancyReview.WholeFile },
                new LineFinding(8, string.Empty, "Wait for me!", DiscrepancyFinder.MissingLine, "heard, not shown") { From = DiscrepancyReview.WholeFile, End = 9 },
            ],
        });

        var (result, applied) = _processor.ApplyFindings(id);

        Assert.Equal(2, applied);
        var text = File.ReadAllText(path);
        Assert.Contains("Hello, Theo.", text, StringComparison.Ordinal);
        Assert.Contains("Wait for me!", text, StringComparison.Ordinal);
        Assert.Contains("Goodbye now.", text, StringComparison.Ordinal);
        Assert.Equal(2, result.Findings.Count);
        Assert.Contains(result.Findings, f => f.Kind == DiscrepancyFinder.Extra);
        Assert.Contains(result.Findings, f => f.Current == "Not in the file.");
        Assert.True(result.Changed);
        Assert.NotNull(result.Backup);

        // Undo brings the original back
        _processor.Undo(id);
        Assert.Equal(Original, File.ReadAllText(path));
    }

    [Fact]
    public void Apply_all_suggestions_refuses_when_none_can_be_applied_and_decline_all_clears_the_lines()
    {
        var path = Path.Combine(_dir, "a.en.srt");
        File.WriteAllText(path, Original);
        var id = ResultStore.IdFor(path);
        _store.Put(new SubtitleResult
        {
            Id = id,
            SubtitlePath = path,
            Status = ResultStatus.Proposed,
            Offset = 1,
            Fingerprint = SubtitleFiles.Fingerprint(Encoding.UTF8.GetBytes(Original)),
            Findings =
            [
                new LineFinding(30, "Gone.", "Went.", DiscrepancyFinder.Words, "words missing"),
                new LineFinding(5, "Goodbye now.", null, DiscrepancyFinder.Extra, "nothing heard") { From = DiscrepancyReview.WholeFile },
            ],
        });

        Assert.Contains("changed since", Assert.Throws<InvalidOperationException>(() => _processor.ApplyFindings(id)).Message, StringComparison.Ordinal);
        Assert.Equal(Original, File.ReadAllText(path));

        var declined = _processor.DeclineFindings(id);
        Assert.Empty(declined.Findings);
        Assert.Equal(ResultStatus.Proposed, declined.Status);
        Assert.True(declined.PendingReview);
        Assert.Throws<InvalidOperationException>(() => _processor.DeclineFindings(id));
        Assert.Throws<InvalidOperationException>(() => _processor.ApplyFindings(id));
    }

    // The page: a labelled tick per row and "select all shown", the bar with counts, server-side "select all matching",
    // the quick "Apply all waiting" with a confirmation, the job polled, the selection cleared on a new filter
    [Fact]
    public void The_page_offers_multiselect_and_bulk_actions()
    {
        using var stream = typeof(BulkJobs).Assembly.GetManifestResourceStream("Jellyfin.Plugin.Subtitles.Configuration.configPage.html")!;
        using var reader = new StreamReader(stream);
        var page = reader.ReadToEnd();

        Assert.Contains("<input type=\"checkbox\" id=\"ResAll\" aria-label=\"Select all shown\" />", page, StringComparison.Ordinal);
        Assert.Contains("class=\"res-sel\" data-id=\"' + esc(r.Id) + '\"' + (isSelected(r.Id) ? ' checked' : '') + ' aria-label=\"Select ' + esc(", page, StringComparison.Ordinal);
        Assert.Contains("id=\"BulkBar\" class=\"subs-bulk\" role=\"region\" aria-label=\"Selected results\" hidden", page, StringComparison.Ordinal);
        Assert.Contains(">Select all ' + results.total + ' matching this filter<", page, StringComparison.Ordinal);
        Assert.Contains("{ Filter: currentFilter(), Except: Object.keys(sel.except) } : { Ids: Object.keys(sel.ids) }", page, StringComparison.Ordinal);
        Assert.Contains("'Subtitles/Results/Bulk/Preview'", page, StringComparison.Ordinal);
        Assert.Contains("ApiClient.getUrl('Subtitles/Results/Bulk'), data: JSON.stringify(body)", page, StringComparison.Ordinal);
        Assert.Contains("'Subtitles/Results/Bulk/' + p.Id", page, StringComparison.Ordinal);
        Assert.Contains("id=\"ResApplyWaiting\"", page, StringComparison.Ordinal);
        Assert.Contains("'This applies what waits for review for ' + plural(c.Apply, 'subtitle')", page, StringComparison.Ordinal);
        Assert.Contains("results.items = []; clearSelection(); loadResults(false);", page, StringComparison.Ordinal);
        Assert.Contains("'/Findings/' + (applyAll ? 'Apply' : 'Decline')", page, StringComparison.Ordinal);
        Assert.Contains("<td colspan=\"8\">", page, StringComparison.Ordinal);
    }

    private static Policies Policies() => new(ChangePolicy.Automatic, ChangePolicy.Review, new CleanupSettings());

    private async Task<BulkProgress> Run(BulkAction action, IReadOnlyList<SubtitleResult> items)
    {
        var (started, running) = _jobs.Start(action, items, 0, Policies, r => r.Name);
        Assert.Null(running);
        await _jobs.Work!.WaitAsync(Patience, Ct);
        return _jobs.Get(started!.Id)!;
    }

    // A subtitle whose timing correction waits for review
    private string Proposed(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, Original);
        _store.Put(new SubtitleResult
        {
            Id = ResultStore.IdFor(path),
            SubtitlePath = path,
            Name = "Film " + name[0],
            Status = ResultStatus.Proposed,
            Offset = 1,
            Confidence = 0.9,
            Fingerprint = SubtitleFiles.Fingerprint(Encoding.UTF8.GetBytes(Original)),
            Time = DateTimeOffset.UtcNow,
        });
        return path;
    }

    // A subtitle whose timing the plugin corrected (the original kept)
    private string Corrected(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, Original);
        var (backup, written) = _files.Replace(path, SubtitleFiles.Fingerprint(File.ReadAllBytes(path)), Encoding.UTF8.GetBytes(Original.Replace("00:00:01,000", "00:00:02,000", StringComparison.Ordinal)));
        _store.Put(new SubtitleResult { Id = ResultStore.IdFor(path), SubtitlePath = path, Name = "Film " + name[0], Status = ResultStatus.Corrected, Changed = true, Backup = backup, Fingerprint = written });
        return path;
    }

    private string InSync(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, Original);
        _store.Put(new SubtitleResult { Id = ResultStore.IdFor(path), SubtitlePath = path, Name = "Film " + name[0], Status = ResultStatus.InSync, Fingerprint = SubtitleFiles.Fingerprint(Encoding.UTF8.GetBytes(Original)) });
        return path;
    }
}
