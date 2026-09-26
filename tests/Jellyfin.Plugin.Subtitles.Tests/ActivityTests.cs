using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.Pipeline;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// FAM-05: what needs attention, and subtitles added, reach Jellyfin's Activity log once a day each
public sealed class ActivityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "subs-activity-" + Guid.NewGuid().ToString("N"));

    public ActivityTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 26, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static SubtitleResult R(ResultStatus status) => new() { Id = "a", SubtitlePath = "/m/Film.en.srt", Name = "Invented Film", Status = status, Explanation = "Why." };

    [Fact]
    public void Added_waiting_and_cant_write_are_noted_and_routine_checks_are_not()
    {
        Assert.Equal(LogLevel.Information, SubtitleActivity.NoteFor(R(ResultStatus.Added))!.Severity);
        Assert.Equal(LogLevel.Warning, SubtitleActivity.NoteFor(R(ResultStatus.Proposed))!.Severity);
        Assert.StartsWith("A subtitle is waiting for review", SubtitleActivity.NoteFor(R(ResultStatus.Proposed))!.Name, StringComparison.Ordinal);
        Assert.Equal(LogLevel.Warning, SubtitleActivity.NoteFor(R(ResultStatus.CantWrite))!.Severity);
        Assert.Null(SubtitleActivity.NoteFor(R(ResultStatus.InSync)));
        Assert.Null(SubtitleActivity.NoteFor(R(ResultStatus.Unreliable)));
    }

    [Fact]
    public async Task Once_a_day_switchable_and_never_throws()
    {
        var clock = new Clock();
        var on = true;
        var written = new List<ActivityNote>();
        var activity = new SubtitleActivity(n => { written.Add(n); return Task.CompletedTask; }, clock, () => on);

        Assert.True(await activity.NotifyAsync(R(ResultStatus.Added)));
        Assert.False(await activity.NotifyAsync(R(ResultStatus.Added)));
        Assert.True(await activity.NotifyStoppedAsync("Daily download limit reached."));
        on = false;
        clock.Now += TimeSpan.FromDays(2);
        Assert.False(await activity.NotifyAsync(R(ResultStatus.Added)));
        Assert.Equal(2, written.Count);

        var failing = new SubtitleActivity(_ => throw new InvalidOperationException("busy"), clock);
        Assert.False(await failing.NotifyAsync(R(ResultStatus.Added)));
    }

    [Fact]
    public void Recording_a_result_passes_it_on()
    {
        var store = new ResultStore(Path.Combine(_dir, "results.json"));
        var seen = new List<SubtitleResult>();
        store.Recorded = seen.Add;

        store.Put(R(ResultStatus.Added));

        Assert.Single(seen);
    }
}
