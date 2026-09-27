using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.Pipeline;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// FEAT-03: new videos are queued once each, handled after a quiet delay, within the same limits, never alongside a
// nightly task.
public class NewItemsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Delay = TimeSpan.FromMinutes(10);

    [Fact]
    public void Nothing_is_due_until_the_quiet_delay_after_the_last_addition()
    {
        var waiting = new NewItemsWaiting();
        Assert.Null(waiting.DueAt(Delay));
        waiting.Add(Guid.NewGuid(), true, T0);
        waiting.Add(Guid.NewGuid(), true, T0.AddMinutes(8));
        Assert.Equal(T0.AddMinutes(18), waiting.DueAt(Delay));

        // Ten minutes after the first, but only two after the last: a season still being filed
        Assert.Null(waiting.TakeIfDue(T0.AddMinutes(10), Delay));
        var batch = waiting.TakeIfDue(T0.AddMinutes(18), Delay);
        Assert.NotNull(batch);
        Assert.Equal(2, batch.Added.Count);
        Assert.Equal(0, waiting.Count);
        Assert.Null(waiting.TakeIfDue(T0.AddMinutes(30), Delay));
    }

    [Fact]
    public void A_video_reported_many_times_is_queued_once_and_added_wins_over_changed()
    {
        var waiting = new NewItemsWaiting();
        var film = Guid.NewGuid();
        var episode = Guid.NewGuid();
        waiting.Add(film, false, T0);
        waiting.Add(film, true, T0.AddSeconds(1));
        waiting.Add(film, false, T0.AddSeconds(2));
        waiting.Add(episode, false, T0.AddSeconds(3));
        waiting.Add(episode, false, T0.AddSeconds(4));
        Assert.Equal(2, waiting.Count);

        var batch = waiting.TakeIfDue(T0.AddHours(1), Delay)!;
        Assert.Equal([film], batch.Added);
        Assert.Equal([episode], batch.Changed);
        Assert.Equal(2, batch.All.Count);
    }

    [Fact]
    public void Past_the_cap_the_rest_are_left_to_the_nightly_tasks()
    {
        var waiting = new NewItemsWaiting(max: 3);
        var first = Guid.NewGuid();
        Assert.True(waiting.Add(first, false, T0));
        Assert.True(waiting.Add(Guid.NewGuid(), true, T0));
        Assert.True(waiting.Add(Guid.NewGuid(), true, T0));
        Assert.False(waiting.Add(Guid.NewGuid(), true, T0));
        Assert.Equal(1, waiting.Dropped);

        // One already queued is still taken (as added now)
        Assert.True(waiting.Add(first, true, T0));
        Assert.Equal(3, waiting.Count);
        Assert.Equal(3, waiting.TakeIfDue(T0.AddHours(1), Delay)!.Added.Count);
    }

    [Fact]
    public void A_batch_that_had_to_wait_is_due_again_after_another_delay()
    {
        var waiting = new NewItemsWaiting();
        var film = Guid.NewGuid();
        var changed = Guid.NewGuid();
        waiting.Add(film, true, T0);
        waiting.Add(changed, false, T0);
        var batch = waiting.TakeIfDue(T0.AddMinutes(10), Delay)!;

        waiting.PutBack(batch, T0.AddMinutes(10));
        Assert.Equal(T0.AddMinutes(20), waiting.DueAt(Delay));
        var again = waiting.TakeIfDue(T0.AddMinutes(20), Delay)!;
        Assert.Equal([film], again.Added);
        Assert.Equal([changed], again.Changed);
    }

    [Fact]
    public void Added_videos_have_every_file_checked_and_changed_ones_only_files_not_seen_before()
    {
        var added = Guid.NewGuid();
        var changed = Guid.NewGuid();
        var batch = new NewItemBatch([added], [changed]);
        Assert.True(batch.ChecksFile(added, known: true));
        Assert.True(batch.ChecksFile(added, known: false));
        Assert.False(batch.ChecksFile(changed, known: true));
        Assert.True(batch.ChecksFile(changed, known: false));
    }

    [Fact]
    public void The_days_new_video_runs_share_one_runs_ai_checks()
    {
        var daily = new DailyAiChecks();
        var today = new DateOnly(2026, 9, 27);
        var first = daily.For(today, 2);
        Assert.True(first.TryTake());

        // A later run the same day gets what is left
        var second = daily.For(today, 2);
        Assert.Same(first, second);
        Assert.True(second.TryTake());
        Assert.False(second.TryTake());

        // A new day, or a new limit, starts afresh
        Assert.True(daily.For(today.AddDays(1), 2).TryTake());
        Assert.Equal(5, daily.For(today.AddDays(1), 5).Limit);
    }

    [Fact]
    public async Task Handling_new_videos_never_starts_while_a_nightly_task_runs()
    {
        var gate = new RunGate();
        var nightly = await gate.EnterSharedAsync(TestContext.Current.CancellationToken);
        var other = await gate.EnterSharedAsync(TestContext.Current.CancellationToken);
        Assert.Null(gate.TryEnterAlone("handling new videos"));
        Assert.NotNull(gate.Busy);
        nightly.Dispose();
        Assert.Null(gate.TryEnterAlone("handling new videos"));
        other.Dispose();

        using var alone = gate.TryEnterAlone("handling new videos");
        Assert.NotNull(alone);
        Assert.Equal("handling new videos", gate.Busy);
        Assert.Null(gate.TryEnterAlone("restoring all originals"));
    }

    [Fact]
    public async Task A_nightly_task_waits_for_new_videos_to_be_handled()
    {
        var gate = new RunGate();
        var alone = gate.TryEnterAlone("handling new videos")!;
        var nightly = gate.EnterSharedAsync(TestContext.Current.CancellationToken);
        Assert.False(nightly.IsCompleted);

        alone.Dispose();
        alone.Dispose();
        using var hold = await nightly.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Null(gate.TryEnterAlone("handling new videos"));
    }

    [Fact]
    public async Task A_nightly_task_waiting_for_the_gate_can_be_cancelled()
    {
        var gate = new RunGate();
        using var alone = gate.TryEnterAlone("restoring all originals")!;
        using var cancel = new CancellationTokenSource();
        var nightly = gate.EnterSharedAsync(cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => nightly);
    }
}
