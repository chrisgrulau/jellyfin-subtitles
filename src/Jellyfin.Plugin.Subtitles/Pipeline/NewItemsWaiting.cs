using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Jellyfin.Plugin.Subtitles.Pipeline;

/// <summary>
/// Videos to handle soon: added to the library, or given a new subtitle file. Each video is queued once, however often
/// Jellyfin reports it; the queue is handled only once nothing has been added for the quiet delay, so a season being
/// filed episode by episode is handled in one go. The queue holds at most <see cref="MaxQueued"/> videos: past that (a
/// whole library being added) the rest are left to the nightly tasks.
/// </summary>
public sealed class NewItemsWaiting
{
    /// <summary>The most videos queued at once.</summary>
    public const int MaxQueued = 500;

    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, bool> _items = new();
    private readonly int _max;
    private DateTimeOffset? _last;

    /// <summary>
    /// Initializes a new instance of the <see cref="NewItemsWaiting"/> class.
    /// </summary>
    /// <param name="max">The most videos queued at once (for tests).</param>
    public NewItemsWaiting(int max = MaxQueued)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        _max = max;
    }

    /// <summary>Gets how many videos are queued.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _items.Count;
            }
        }
    }

    /// <summary>Gets how many reports were turned away because the queue was full (left to the nightly tasks).</summary>
    public int Dropped { get; private set; }

    /// <summary>
    /// Queues a video (again): the quiet delay starts over.
    /// </summary>
    /// <param name="itemId">The video.</param>
    /// <param name="isNew">Whether it was added to the library (rather than changed, such as given a new subtitle file).</param>
    /// <param name="now">The time.</param>
    /// <returns>Whether it is queued (<c>false</c> when the queue is full).</returns>
    public bool Add(Guid itemId, bool isNew, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_items.TryGetValue(itemId, out var wasNew))
            {
                _items[itemId] = wasNew || isNew;
            }
            else if (_items.Count >= _max)
            {
                Dropped++;
                return false;
            }
            else
            {
                _items[itemId] = isNew;
            }

            _last = now;
            return true;
        }
    }

    /// <summary>
    /// When the queue is due: the quiet delay after the last video was queued.
    /// </summary>
    /// <param name="delay">The quiet delay.</param>
    /// <returns>The time, or <c>null</c> when nothing is queued.</returns>
    public DateTimeOffset? DueAt(TimeSpan delay)
    {
        lock (_lock)
        {
            return _items.Count > 0 && _last is { } last ? last + delay : null;
        }
    }

    /// <summary>
    /// Takes everything queued, if the quiet delay has passed.
    /// </summary>
    /// <param name="now">The time.</param>
    /// <param name="delay">The quiet delay.</param>
    /// <returns>The videos, or <c>null</c> when nothing is due yet.</returns>
    public NewItemBatch? TakeIfDue(DateTimeOffset now, TimeSpan delay)
    {
        lock (_lock)
        {
            if (_items.Count == 0 || _last is not { } last || now < last + delay)
            {
                return null;
            }

            var batch = new NewItemBatch([.. _items.Where(i => i.Value).Select(i => i.Key)], [.. _items.Where(i => !i.Value).Select(i => i.Key)]);
            _items.Clear();
            return batch;
        }
    }

    /// <summary>
    /// Puts back a batch that couldn't be handled yet (a nightly task was running): it is due again after another quiet
    /// delay. Videos queued meanwhile are kept.
    /// </summary>
    /// <param name="batch">The batch.</param>
    /// <param name="now">The time.</param>
    public void PutBack(NewItemBatch batch, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(batch);
        foreach (var id in batch.Added)
        {
            Add(id, true, now);
        }

        foreach (var id in batch.Changed)
        {
            Add(id, false, now);
        }
    }

    /// <summary>
    /// Forgets everything queued (the setting was switched off).
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _items.Clear();
        }
    }
}

/// <summary>
/// Videos to handle together.
/// </summary>
/// <param name="Added">Videos added to the library: their subtitles are checked and missing ones searched for.</param>
/// <param name="Changed">Videos that changed (such as a new subtitle file beside them): only subtitle files not seen
/// before are checked.</param>
public sealed record NewItemBatch(IReadOnlyList<Guid> Added, IReadOnlyList<Guid> Changed)
{
    /// <summary>Gets every video in the batch.</summary>
    public IReadOnlyList<Guid> All => [.. Added, .. Changed];

    /// <summary>
    /// Whether a subtitle file beside a video in the batch is checked now: every file of a video added (as the nightly
    /// run would, within its limits), and only files not seen before beside a video that changed (Jellyfin reports a
    /// video as changed for many reasons; the nightly run sees to the rest).
    /// </summary>
    /// <param name="itemId">The video.</param>
    /// <param name="known">Whether the plugin has a result for the file.</param>
    /// <returns><c>true</c> to check it.</returns>
    public bool ChecksFile(Guid itemId, bool known) => !known || Added.Contains(itemId);
}

/// <summary>
/// The AI checks the handling of new videos may ask in a day: one run's allowance
/// (<see cref="Configuration.PluginConfiguration.MaxAiChecksPerRun"/>), shared by all of the day's runs, so frequent small
/// runs never add up to more than a nightly run may ask.
/// </summary>
public sealed class DailyAiChecks
{
    private readonly Lock _lock = new();
    private DateOnly _day;
    private int _limit = -1;
    private Ai.AiChecks? _checks;

    /// <summary>
    /// The day's allowance (a new one each day, or when the limit changes).
    /// </summary>
    /// <param name="day">The day.</param>
    /// <param name="limit">The limit per run.</param>
    /// <returns>The allowance.</returns>
    public Ai.AiChecks For(DateOnly day, int limit)
    {
        lock (_lock)
        {
            if (_checks is null || day != _day || limit != _limit)
            {
                _day = day;
                _limit = limit;
                _checks = new Ai.AiChecks(limit);
            }

            return _checks;
        }
    }
}
