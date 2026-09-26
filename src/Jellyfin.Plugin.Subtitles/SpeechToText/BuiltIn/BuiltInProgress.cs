using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Subtitles.SpeechToText.BuiltIn;

/// <summary>
/// Where the built-in speech-to-text's download stands, as the settings page shows it (SUB-25). One is shared by every
/// install, whoever started it (the page's Download now or Test, or a nightly run), so the page shows the same
/// progress. Thread-safe.
/// </summary>
public sealed class BuiltInProgress
{
    /// <summary>Nothing is being downloaded, and nothing is known to be installed.</summary>
    public const string Idle = "idle";

    /// <summary>Files are arriving.</summary>
    public const string Downloading = "downloading";

    /// <summary>Everything has arrived and is being unpacked and checked.</summary>
    public const string Verifying = "verifying";

    /// <summary>The program and model are in place.</summary>
    public const string Installed = "installed";

    /// <summary>The last download failed (see the error).</summary>
    public const string Failed = "failed";

    private readonly Lock _gate = new();
    private string _state = Idle;
    private long _done;
    private long _total;
    private string? _error;

    /// <summary>
    /// An install begins: <paramref name="total"/> bytes need downloading.
    /// </summary>
    /// <param name="total">Bytes to download.</param>
    public void Begin(long total)
    {
        lock (_gate)
        {
            _state = Downloading;
            _done = 0;
            _total = Math.Max(0, total);
            _error = null;
        }
    }

    /// <summary>
    /// Bytes arrived.
    /// </summary>
    /// <param name="bytes">How many.</param>
    public void Add(long bytes)
    {
        lock (_gate)
        {
            _state = Downloading;
            _done = Math.Min(_total, _done + Math.Max(0, bytes));
        }
    }

    /// <summary>
    /// A download didn't complete and will be fetched again from the start: what it brought no longer counts.
    /// </summary>
    /// <param name="bytes">How many bytes it had brought.</param>
    public void Discard(long bytes)
    {
        lock (_gate)
        {
            _done = Math.Max(0, _done - Math.Max(0, bytes));
        }
    }

    /// <summary>Files are being unpacked and checked.</summary>
    public void Verify()
    {
        lock (_gate)
        {
            _state = Verifying;
        }
    }

    /// <summary>The install finished.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            _state = Installed;
            _done = _total;
            _error = null;
        }
    }

    /// <summary>
    /// The install failed.
    /// </summary>
    /// <param name="error">Why, in plain language (never a key or an address with credentials).</param>
    public void Fail(string error)
    {
        lock (_gate)
        {
            _state = Failed;
            _error = error;
        }
    }

    /// <summary>
    /// Why a download failed, in plain language.
    /// </summary>
    /// <param name="error">What went wrong.</param>
    /// <returns>The message.</returns>
    public static string Describe(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error switch
        {
            OperationCanceledException or ObjectDisposedException => "The download was stopped (the server is shutting down). It will be tried again.",
            SpeechToTextException => error.Message,
            System.IO.IOException or UnauthorizedAccessException => "The download couldn't be saved: " + error.Message,
            _ => "The download failed: " + error.Message,
        };
    }

    /// <summary>
    /// What to show.
    /// </summary>
    /// <param name="installed">Whether the chosen program and model are on disk now (they may have been installed
    /// earlier, by another run, or removed since).</param>
    /// <returns>The state.</returns>
    public BuiltInInstallStatus Report(bool installed)
    {
        lock (_gate)
        {
            var state = _state switch
            {
                Downloading or Verifying => _state,
                _ when installed => Installed,
                Failed => Failed,
                _ => Idle,
            };
            return new BuiltInInstallStatus(
                state,
                state is Downloading or Verifying ? _done : 0,
                state is Downloading or Verifying ? _total : 0,
                state is Downloading or Verifying ? Percent(_done, _total) : null,
                state == Failed ? _error : null);
        }
    }

    /// <summary>
    /// What Test says while the built-in speech-to-text downloads.
    /// </summary>
    /// <param name="status">Where the download stands.</param>
    /// <returns>The message.</returns>
    public static string TestMessage(BuiltInInstallStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.State switch
        {
            Verifying => "The built-in speech-to-text has downloaded and is being checked. It's tested as soon as that's done.",
            Installed => "The built-in speech-to-text has downloaded. Press Test again.",
            Failed => "The built-in speech-to-text couldn't be downloaded: " + status.Error,
            _ => "The built-in speech-to-text is downloading" + (status.Percent is { } p ? string.Create(CultureInfo.InvariantCulture, $" ({p}%)") : string.Empty) + " in the background. It's tested as soon as that's done.",
        };
    }

    /// <summary>
    /// Whole percent done, rounded down so 100 means everything arrived.
    /// </summary>
    /// <param name="done">Bytes done.</param>
    /// <param name="total">Bytes in all.</param>
    /// <returns>0 to 100, or <c>null</c> when the total isn't known.</returns>
    public static int? Percent(long done, long total) => total <= 0 ? null : (int)Math.Clamp(done * 100 / total, 0, 100);
}

/// <summary>
/// The built-in speech-to-text's download, as the settings page shows it.
/// </summary>
/// <param name="State">One of <c>idle</c>, <c>downloading</c>, <c>verifying</c>, <c>installed</c>, <c>failed</c>.</param>
/// <param name="BytesDone">Bytes downloaded so far (while downloading or verifying).</param>
/// <param name="BytesTotal">Bytes to download in all (while downloading or verifying).</param>
/// <param name="Percent">Whole percent done, while downloading or verifying.</param>
/// <param name="Error">Why the last download failed, when the state is <c>failed</c>.</param>
public sealed record BuiltInInstallStatus(string State, long BytesDone, long BytesTotal, int? Percent, string? Error);

/// <summary>
/// Runs one piece of background work at a time: asking again while it runs joins it rather than starting another
/// (SUB-25: one download of the built-in speech-to-text, however often Download now or Test is pressed).
/// </summary>
public sealed class SingleFlight
{
    private readonly Lock _gate = new();
    private Task? _current;

    /// <summary>Gets a value indicating whether the work is running.</summary>
    public bool Running
    {
        get
        {
            lock (_gate)
            {
                return _current is { IsCompleted: false };
            }
        }
    }

    /// <summary>Gets the work started last (running or finished), or <c>null</c> if none was.</summary>
    public Task? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>
    /// Starts the work in the background unless it is already running.
    /// </summary>
    /// <param name="work">The work; it should not throw (exceptions are kept on the returned task).</param>
    /// <param name="cancellationToken">Passed to the work (e.g. cancelled when the server stops).</param>
    /// <param name="starting">Run just before the work starts, only when it does (while no other caller can start it).</param>
    /// <returns><c>true</c> if it was started now, <c>false</c> if it was already running.</returns>
    public bool TryStart(Func<CancellationToken, Task> work, CancellationToken cancellationToken, Action? starting = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            if (_current is { IsCompleted: false })
            {
                return false;
            }

            starting?.Invoke();
            _current = Task.Run(() => work(cancellationToken), CancellationToken.None);
            return true;
        }
    }
}
