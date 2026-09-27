using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Subtitles.Pipeline;

/// <summary>
/// Handles videos soon after they are added (with <see cref="PluginConfiguration.HandleNewItems"/>): films and episodes
/// Jellyfin adds, and those it reports as changed (a new subtitle file beside them, for example), are queued (see
/// <see cref="NewItemsWaiting"/>); once nothing has been queued for the quiet delay, the same work as the nightly tasks is
/// done for just those videos: their subtitle files are checked, then missing subtitles are searched for. The nightly
/// tasks' own rules and limits apply (the setup gate, libraries, languages, files per run, videos searched per run,
/// downloads per day, spending), and the day's new-video runs share one run's allowance of AI checks. Generating
/// subtitles stays nightly: a video nothing was found for is a candidate for the next night's run. Never at the same
/// time as a nightly task (see <see cref="RunGate"/>): while one runs, the queue waits and is tried again after another
/// quiet delay.
/// </summary>
internal sealed partial class NewItemsHost : IHostedService, IDisposable
{
    private readonly ILibraryManager _library;
    private readonly IServiceProvider _services;
    private readonly RunGate _gate;
    private readonly ILogger<NewItemsHost> _logger;
    private readonly NewItemsWaiting _queue = new();
    private readonly DailyAiChecks _checks = new();
    private readonly TimeProvider _clock = TimeProvider.System;
    private readonly CancellationTokenSource _stopping = new();
    private ITimer? _timer;
    private int _running;
    private int _droppedLogged;

    /// <summary>
    /// Initializes a new instance of the <see cref="NewItemsHost"/> class.
    /// </summary>
    /// <param name="library">Jellyfin's library (for its item-added and item-updated events).</param>
    /// <param name="services">The server's services (to make the tasks' runs).</param>
    /// <param name="gate">Keeps this apart from the nightly tasks.</param>
    /// <param name="logger">Logger.</param>
    public NewItemsHost(ILibraryManager library, IServiceProvider services, RunGate gate, ILogger<NewItemsHost> logger)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = _clock.CreateTimer(_ => _ = RunDueAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _library.ItemAdded += OnItemAdded;
        _library.ItemUpdated += OnItemUpdated;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded -= OnItemAdded;
        _library.ItemUpdated -= OnItemUpdated;
        _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        await _stopping.CancelAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer?.Dispose();
        _stopping.Dispose();
    }

    private static PluginConfiguration? Settings => SubtitlesPlugin.Instance?.Configuration;

    private static TimeSpan DelayOf(PluginConfiguration config) => TimeSpan.FromMinutes(Math.Clamp(config.NewItemsDelayMinutes, 1, 1440));

    private void OnItemAdded(object? sender, ItemChangeEventArgs e) => Queue(e, isNew: true);

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        // A new subtitle file beside a video arrives as a change to the video; new artwork alone is nothing to do
        if (e?.UpdateReason != ItemUpdateType.ImageUpdate)
        {
            Queue(e, isNew: false);
        }
    }

    // Quick and never throws: this runs on Jellyfin's scanning thread
    private void Queue(ItemChangeEventArgs? e, bool isNew)
    {
        if (Settings is not { Enabled: true, HandleNewItems: true } config || e?.Item is not (Movie or Episode) || e.Item.IsVirtualItem)
        {
            return;
        }

        if (!_queue.Add(e.Item.Id, isNew, _clock.GetUtcNow()))
        {
            if (Interlocked.Exchange(ref _droppedLogged, 1) == 0)
            {
                LogQueueFull(_logger, NewItemsWaiting.MaxQueued);
            }

            return;
        }

        // Every addition starts the quiet delay over
        _timer?.Change(DelayOf(config), Timeout.InfiniteTimeSpan);
    }

    private async Task RunDueAsync()
    {
        // One run at a time: a run that ends re-arms the timer for anything queued meanwhile
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            if (Settings is not { Enabled: true, HandleNewItems: true } config)
            {
                _queue.Clear();
                return;
            }

            var now = _clock.GetUtcNow();
            if (_queue.TakeIfDue(now, DelayOf(config)) is not { } batch)
            {
                return;
            }

            using var hold = _gate.TryEnterAlone("handling new videos");
            if (hold is null)
            {
                _queue.PutBack(batch, now);
                LogWaiting(_logger, batch.All.Count);
                return;
            }

            Interlocked.Exchange(ref _droppedLogged, 0);
            LogHandling(_logger, batch.Added.Count, batch.Changed.Count);
            var checks = _checks.For(DateOnly.FromDateTime(now.LocalDateTime), config.MaxAiChecksPerRun);
            await ActivatorUtilities.CreateInstance<SubtitleSyncTask>(_services).RunForAsync(batch, checks, _stopping.Token).ConfigureAwait(false);
            if (batch.Added.Count > 0 && config.FindMissing)
            {
                await ActivatorUtilities.CreateInstance<SubtitleFindTask>(_services).RunForAsync(batch.Added, checks, _stopping.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // The server is stopping; the nightly tasks see to what was left
        }
#pragma warning disable CA1031 // Handling new videos is a convenience: whatever goes wrong, the nightly tasks still run and the server carries on
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFailed(_logger, ex.Message);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
            if (!_stopping.IsCancellationRequested && Settings is { } config && _queue.DueAt(DelayOf(config)) is { } due)
            {
                var wait = due - _clock.GetUtcNow();
                _timer?.Change(wait > TimeSpan.FromSeconds(1) ? wait : TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Shoal Subtitles: handling new videos: {Added} added, {Changed} changed")]
    private static partial void LogHandling(ILogger logger, int added, int changed);

    [LoggerMessage(Level = LogLevel.Information, Message = "Shoal Subtitles: {Count} new videos wait for a scheduled task to finish")]
    private static partial void LogWaiting(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Shoal Subtitles: more than {Max} new videos at once; the rest are left to the nightly tasks")]
    private static partial void LogQueueFull(ILogger logger, int max);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shoal Subtitles: handling new videos failed: {Error}")]
    private static partial void LogFailed(ILogger logger, string error);
}
