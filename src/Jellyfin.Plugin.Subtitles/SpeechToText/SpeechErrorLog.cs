using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.Common.Resilience;
using Jellyfin.Plugin.Common.Storage;

namespace Jellyfin.Plugin.Subtitles.SpeechToText;

/// <summary>
/// One speech-to-text call that failed at least once.
/// </summary>
/// <param name="Time">When the call ended.</param>
/// <param name="Provider">The service id (<c>deepgram</c>, <c>openai</c>, <c>local</c>).</param>
/// <param name="Failure">The failure class of the last failure (<c>Transient</c>, <c>NoConnection</c>,
/// <c>Authentication</c>, <c>ProviderLimit</c>, <c>BadRequest</c>).</param>
/// <param name="Message">What went wrong, short, with keys removed.</param>
/// <param name="Recovered">Whether a retry succeeded in the end.</param>
/// <param name="Attempts">How many attempts the call made.</param>
/// <param name="Run">The run it belonged to, if any.</param>
public sealed record SpeechFailure(DateTimeOffset Time, string Provider, string Failure, string Message, bool Recovered, int Attempts, string? Run);

/// <summary>
/// How a speech-to-text service has been doing lately (see <see cref="SpeechHealth.Classify"/>).
/// </summary>
/// <param name="Provider">The service id.</param>
/// <param name="Name">Its name for people.</param>
/// <param name="Systemic">Whether its failures look systemic (worth telling someone) rather than transitory.</param>
/// <param name="Problem">What to tell them, when systemic.</param>
/// <param name="Failures">Calls that failed for good in the last 24 hours.</param>
/// <param name="Recovered">Calls that failed but succeeded on a retry in the last 24 hours.</param>
/// <param name="Calls">Calls made in the last 24 hours.</param>
/// <param name="FailedRunsInARow">How many of its latest runs in a row had a call fail for good.</param>
/// <param name="LastFailure">When a call last failed (for good or not).</param>
public sealed record ProviderHealth(string Provider, string Name, bool Systemic, string? Problem, int Failures, int Recovered, int Calls, int FailedRunsInARow, DateTimeOffset? LastFailure);

/// <summary>
/// Tells a systemic speech-to-text problem (one worth telling someone about) from transitory failures.
/// </summary>
public static class SpeechHealth
{
    /// <summary>The window the counts cover.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    /// <summary>Calls failing for good (retries exhausted) in the window that make a problem systemic.</summary>
    public const int FailuresForSystemic = 5;

    /// <summary>The share of calls failing in the window that makes a problem systemic …</summary>
    public const double FailingShare = 0.5;

    /// <summary>… once at least this many calls were made.</summary>
    public const int MinimumCalls = 5;

    /// <summary>Runs in a row with a call failing for good that make a problem systemic.</summary>
    public const int FailedRuns = 3;

    /// <summary>Successes in a row that clear a systemic problem.</summary>
    public const int SuccessesToClear = 3;

    /// <summary>
    /// Classifies a service's recent record. Systemic: at least <see cref="FailuresForSystemic"/> calls failed for good in
    /// the last 24 hours; or at least half of the calls in that time failed for good (with at least
    /// <see cref="MinimumCalls"/> calls); or calls failed for good on <see cref="FailedRuns"/> runs in a row; or the key was
    /// refused (or the provider's allowance used up) with no success since. Anything else, including failures a retry
    /// recovered, is transitory; and a problem clears once the service has succeeded <see cref="SuccessesToClear"/> times
    /// in a row. What to tell someone depends on the service: a paid one's key, network, credit or status page; whether the
    /// local service is running at its address; whether the built-in one needs downloading again or can run here.
    /// </summary>
    /// <param name="provider">The service id.</param>
    /// <param name="failures">Its failures (any age; only the window counts).</param>
    /// <param name="calls">Calls made in the window.</param>
    /// <param name="runsNewestFirst">Calls and failures for good per run, newest run first.</param>
    /// <param name="lastSuccess">When a call last succeeded, if known.</param>
    /// <param name="now">The current time.</param>
    /// <param name="successesInARow">Calls that succeeded in a row, most recent last.</param>
    /// <param name="localAddress">The local service's address, for its message.</param>
    /// <returns>The health.</returns>
    public static ProviderHealth Classify(string provider, IEnumerable<SpeechFailure> failures, int calls, IReadOnlyList<(int Calls, int Failed)> runsNewestFirst, DateTimeOffset? lastSuccess, DateTimeOffset now, int successesInARow = 0, string? localAddress = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(failures);
        ArgumentNullException.ThrowIfNull(runsNewestFirst);
        var mine = failures.Where(f => string.Equals(f.Provider, provider, StringComparison.Ordinal)).ToList();
        var recent = mine.Where(f => now - f.Time <= Window).ToList();
        var failed = recent.Count(f => !f.Recovered);
        var recovered = recent.Count(f => f.Recovered);
        var inARow = runsNewestFirst.TakeWhile(r => r.Failed > 0).Count();
        var name = NameOf(provider);
        var refused = mine.Where(f => !f.Recovered && f.Failure is nameof(FailureClass.Authentication) or nameof(FailureClass.ProviderLimit))
            .OrderByDescending(f => f.Time).FirstOrDefault(f => lastSuccess is null || f.Time > lastSuccess);
        var how = string.Create(CultureInfo.InvariantCulture, $"{failed} time{(failed == 1 ? string.Empty : "s")} since yesterday");
        var why = refused is not null && now - refused.Time <= Window ? "refused"
            : failed >= FailuresForSystemic ? "count"
            : calls >= MinimumCalls && failed >= calls * FailingShare ? string.Create(CultureInfo.InvariantCulture, $"{failed} of {calls} calls since yesterday")
            : inARow >= FailedRuns ? string.Create(CultureInfo.InvariantCulture, $"on each of the last {inARow} runs")
            : null;
        if (why is not null and not "refused" and not "count")
        {
            how = why;
        }

        var problem = why is null || successesInARow >= SuccessesToClear ? null
            : why == "refused" ? (refused!.Failure == nameof(FailureClass.Authentication)
                ? name + " refused the key — check it under Services."
                : name + " says its allowance or credit is used up — check your account with it.")
            : provider switch
            {
                SpeechToTextFactory.Local => string.IsNullOrWhiteSpace(localAddress)
                    ? $"The local service isn't answering ({how}) — is the service running?"
                    : $"The local service isn't answering at {localAddress.Trim()} ({how}) — is the service running?",
                SpeechToTextFactory.BuiltIn => $"The built-in speech-to-text keeps failing ({how}) — use Download again under Services, or check that this server's CPU and memory can run it.",
                _ => $"{name} has failed {how} — check the key, the network, your credit or the provider's status page.",
            };
        return new ProviderHealth(provider, name, problem is not null, problem, failed, recovered, calls, inARow, mine.Count > 0 ? mine.Max(f => f.Time) : null);
    }

    /// <summary>
    /// A service's name for people, capitalised (for the start of a sentence).
    /// </summary>
    /// <param name="provider">The service id.</param>
    /// <returns>The name.</returns>
    public static string NameOf(string provider) => provider switch
    {
        SpeechToTextFactory.Local => "The local service",
        SpeechToTextFactory.BuiltIn => "The built-in speech-to-text",
        _ => ProviderWording.NameOf(provider),
    };
}

/// <summary>
/// A small rotating record of speech-to-text failures (JSON Lines, the last <see cref="MaxEntries"/> within
/// <see cref="KeepFor"/>), and counts of calls per hour and per run, so the settings page can tell a systemic problem
/// (worth a banner and an Activity log entry) from transitory failures (only listed under Advanced). Never throws: it is
/// a record, and a problem writing it mustn't affect a run.
/// </summary>
public sealed class SpeechErrorLog
{
    /// <summary>The most failures kept.</summary>
    public const int MaxEntries = 500;

    /// <summary>The longest a failure is kept.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);

    /// <summary>The most characters of a failure's message kept.</summary>
    public const int MaxMessage = 300;

    private const int RunsKept = 10;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _failuresPath;
    private readonly string _callsPath;
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private List<SpeechFailure>? _failures;
    private Dictionary<string, CallRecord>? _calls;

    /// <summary>
    /// Initializes a new instance of the <see cref="SpeechErrorLog"/> class.
    /// </summary>
    /// <param name="folder">The plugin's data folder.</param>
    /// <param name="clock">Clock.</param>
    public SpeechErrorLog(string folder, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        _failuresPath = Path.Combine(folder, "speech-errors.jsonl");
        _callsPath = Path.Combine(folder, "speech-calls.json");
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Gets or sets what happens when a failure leaves a service's problem systemic (writing it to the Activity log, at most
    /// once a day).
    /// </summary>
    public Action<ProviderHealth>? Systemic { get; set; }

    /// <summary>Gets or sets where the local service's address comes from (the settings), for its message.</summary>
    public Func<string?>? LocalAddress { get; set; }

    /// <summary>
    /// The record of a failed call.
    /// </summary>
    /// <param name="provider">The service id.</param>
    /// <param name="ex">The (last) failure.</param>
    /// <param name="recovered">Whether a retry succeeded.</param>
    /// <param name="attempts">Attempts made.</param>
    /// <param name="run">The run, if any.</param>
    /// <returns>The record (time set when it is logged).</returns>
    internal static SpeechFailure FailureOf(string provider, SpeechToTextException ex, bool recovered, int attempts, string? run)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var message = ex.Message.Length > MaxMessage ? ex.Message[..MaxMessage] + "…" : ex.Message;
        return new SpeechFailure(default, provider, ex.Failure.ToString(), message, recovered, attempts, run);
    }

    /// <summary>
    /// Counts a call, and records its failure if it had one.
    /// </summary>
    /// <param name="provider">The service id.</param>
    /// <param name="run">The run, if any.</param>
    /// <param name="failure">The failure, or <c>null</c> when it succeeded first time.</param>
    public void RecordCall(string provider, string? run, SpeechFailure? failure)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var now = _clock.GetUtcNow();
        ProviderHealth? systemic = null;
        lock (_lock)
        {
            var calls = LoadCalls();
            if (!calls.TryGetValue(provider, out var record))
            {
                calls[provider] = record = new CallRecord();
            }

            var failed = failure is { Recovered: false };
            var hour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
            var bucket = record.Hours.FirstOrDefault(h => h.Hour == hour);
            if (bucket is null)
            {
                record.Hours.Add(bucket = new CallCount { Hour = hour });
            }

            bucket.Calls++;
            bucket.Failed += failed ? 1 : 0;
            record.Hours.RemoveAll(h => now - h.Hour > SpeechHealth.Window + TimeSpan.FromHours(1));
            if (!failed)
            {
                record.LastSuccess = now;
                record.SuccessesInARow++;
            }
            else
            {
                record.SuccessesInARow = 0;
            }

            if (run is not null)
            {
                var r = record.Runs.FirstOrDefault(x => string.Equals(x.Run, run, StringComparison.Ordinal));
                if (r is null)
                {
                    record.Runs.Insert(0, r = new RunCount { Run = run, Time = now });
                    if (record.Runs.Count > RunsKept)
                    {
                        record.Runs.RemoveRange(RunsKept, record.Runs.Count - RunsKept);
                    }
                }

                r.Calls++;
                r.Failed += failed ? 1 : 0;
            }

            SaveCalls(calls);
            if (failure is not null)
            {
                var stamped = failure with { Time = now, Provider = provider };
                var list = LoadFailures();
                list.Add(stamped);
                Append(stamped, list);
                if (failed && HealthOf(provider, list, calls, now, Address()) is { Systemic: true } health)
                {
                    systemic = health;
                }
            }
        }

        if (systemic is not null)
        {
            try
            {
                Systemic?.Invoke(systemic);
            }
#pragma warning disable CA1031 // Telling someone is a convenience; it mustn't stop a run
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
    }

    /// <summary>
    /// The latest failures, newest first.
    /// </summary>
    /// <param name="offset">How many to skip.</param>
    /// <param name="limit">How many to return (1 to 100).</param>
    /// <returns>The failures and how many there are in all.</returns>
    public (IReadOnlyList<SpeechFailure> Items, int Total) Recent(int offset, int limit)
    {
        lock (_lock)
        {
            var list = LoadFailures();
            Rotate(list, force: false);
            var items = Enumerable.Reverse(list).Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 100)).ToList();
            return (items, list.Count);
        }
    }

    /// <summary>
    /// How each service that has been called or has failed is doing (see <see cref="SpeechHealth.Classify"/>).
    /// </summary>
    /// <returns>One per service, those with a systemic problem first.</returns>
    public IReadOnlyList<ProviderHealth> Health()
    {
        var now = _clock.GetUtcNow();
        lock (_lock)
        {
            var failures = LoadFailures();
            Rotate(failures, force: false);
            var calls = LoadCalls();
            return [.. calls.Keys.Concat(failures.Select(f => f.Provider)).Distinct(StringComparer.Ordinal)
                .Select(p => HealthOf(p, failures, calls, now, Address()))
                .OrderByDescending(h => h.Systemic).ThenBy(h => h.Provider, StringComparer.Ordinal)];
        }
    }

    private static ProviderHealth HealthOf(string provider, List<SpeechFailure> failures, Dictionary<string, CallRecord> calls, DateTimeOffset now, string? localAddress)
    {
        calls.TryGetValue(provider, out var record);
        var inWindow = record?.Hours.Where(h => now - h.Hour <= SpeechHealth.Window).Sum(h => h.Calls) ?? 0;
        var runs = record?.Runs.Select(r => (r.Calls, r.Failed)).ToList() ?? [];
        return SpeechHealth.Classify(provider, failures, inWindow, runs, record?.LastSuccess, now, record?.SuccessesInARow ?? 0, localAddress);
    }

    private string? Address()
    {
        try
        {
            return LocalAddress?.Invoke();
        }
#pragma warning disable CA1031 // Only for a message
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private List<SpeechFailure> LoadFailures()
    {
        if (_failures is not null)
        {
            return _failures;
        }

        var list = new List<SpeechFailure>();
        try
        {
            if (File.Exists(_failuresPath))
            {
                foreach (var line in File.ReadLines(_failuresPath))
                {
                    try
                    {
                        if (line.Length > 0 && JsonSerializer.Deserialize<SpeechFailure>(line, Json) is { Provider: not null, Message: not null, Failure: not null } f)
                        {
                            list.Add(f);
                        }
                    }
                    catch (JsonException)
                    {
                        // A damaged line is skipped; the next rewrite drops it
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable for now: start with what this session records
        }

        _failures = list;
        Rotate(list, force: false);
        return list;
    }

    private void Append(SpeechFailure failure, List<SpeechFailure> list)
    {
        if (Rotate(list, force: false))
        {
            return;
        }

        try
        {
            File.AppendAllText(_failuresPath, JsonSerializer.Serialize(failure, Json) + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory; written with the next rewrite
        }
    }

    // Drops failures past the age or count limit; rewrites the file (atomically) when anything was dropped, allowing a
    // little slack so the file isn't rewritten on every call once full
    private bool Rotate(List<SpeechFailure> list, bool force)
    {
        var now = _clock.GetUtcNow();
        var old = list.Count(f => now - f.Time > KeepFor);
        if (!force && old == 0 && list.Count <= MaxEntries + (MaxEntries / 10))
        {
            return false;
        }

        list.RemoveAll(f => now - f.Time > KeepFor);
        if (list.Count > MaxEntries)
        {
            list.RemoveRange(0, list.Count - MaxEntries);
        }

        try
        {
            var temp = _failuresPath + ".tmp";
            File.WriteAllLines(temp, list.Select(f => JsonSerializer.Serialize(f, Json)));
            File.Move(temp, _failuresPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tried again at the next rotation
        }

        return true;
    }

    private Dictionary<string, CallRecord> LoadCalls()
    {
        if (_calls is not null)
        {
            return _calls;
        }

        var read = JsonFile.Read<Dictionary<string, CallRecord>>(_callsPath, Json);
        _calls = read is { IsLoaded: true, Value: { } loaded } ? new Dictionary<string, CallRecord>(loaded, StringComparer.Ordinal) : new Dictionary<string, CallRecord>(StringComparer.Ordinal);
        foreach (var record in _calls.Values)
        {
            record.Hours ??= [];
            record.Runs ??= [];
        }

        return _calls;
    }

    private void SaveCalls(Dictionary<string, CallRecord> calls)
    {
        try
        {
            JsonFile.WriteAtomic(_callsPath, calls, Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory; written with the next call
        }
    }

    /// <summary>Calls to one service: per hour, per run, and its last success.</summary>
    internal sealed class CallRecord
    {
        /// <summary>Gets or sets the calls per hour (the last day or so).</summary>
        public List<CallCount> Hours { get; set; } = [];

        /// <summary>Gets or sets the calls per run, newest first.</summary>
        public List<RunCount> Runs { get; set; } = [];

        /// <summary>Gets or sets when a call last succeeded.</summary>
        public DateTimeOffset? LastSuccess { get; set; }

        /// <summary>Gets or sets how many calls in a row have succeeded (a systemic problem clears after a few).</summary>
        public int SuccessesInARow { get; set; }
    }

    /// <summary>Calls in one hour.</summary>
    internal sealed class CallCount
    {
        /// <summary>Gets or sets the hour (UTC).</summary>
        public DateTimeOffset Hour { get; set; }

        /// <summary>Gets or sets the calls made.</summary>
        public int Calls { get; set; }

        /// <summary>Gets or sets the calls that failed for good.</summary>
        public int Failed { get; set; }
    }

    /// <summary>Calls in one run.</summary>
    internal sealed class RunCount
    {
        /// <summary>Gets or sets the run.</summary>
        public string Run { get; set; } = string.Empty;

        /// <summary>Gets or sets when its first call was made.</summary>
        public DateTimeOffset Time { get; set; }

        /// <summary>Gets or sets the calls made.</summary>
        public int Calls { get; set; }

        /// <summary>Gets or sets the calls that failed for good.</summary>
        public int Failed { get; set; }
    }
}
