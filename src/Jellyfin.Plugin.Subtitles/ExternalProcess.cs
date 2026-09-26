using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Subtitles;

/// <summary>
/// What an external program run produced (see <see cref="ExternalProcess.RunAsync"/>).
/// </summary>
/// <typeparam name="T">What was read from its standard output.</typeparam>
/// <param name="Output">What was read from its standard output.</param>
/// <param name="ExitCode">Its exit code.</param>
/// <param name="Errors">The end of what it wrote to standard error (its own messages; at most
/// <see cref="ExternalProcess.MaxErrorChars"/> characters, trimmed).</param>
internal sealed record ExternalProcessResult<T>(T Output, int ExitCode, string Errors);

/// <summary>
/// Runs the external programs the plugin uses (Jellyfin's ffmpeg, the built-in speech-to-text) in one way: started
/// without a shell with the arguments as a list, at low priority, with a time limit; both pipes are drained at once so
/// neither can fill up and stall the program; standard error is kept only as its last few thousand characters; and the
/// whole process tree is killed on cancellation, on timeout, or when the caller stops reading early.
/// </summary>
internal static class ExternalProcess
{
    /// <summary>The most of standard error reported (its end, where programs put the reason they failed).</summary>
    public const int MaxErrorChars = 300;

    // How much of standard error is held while reading (bounded, whatever the program writes)
    private const int KeptErrorChars = 4000;

    /// <summary>
    /// Runs a program to the end.
    /// </summary>
    /// <typeparam name="T">What is read from its standard output.</typeparam>
    /// <param name="program">Absolute path of the program.</param>
    /// <param name="arguments">Its arguments, one per item.</param>
    /// <param name="timeLimit">The longest it may run.</param>
    /// <param name="readOutput">Reads its standard output (with a cap of its own). Returning <c>null</c> stops the run
    /// at once, without waiting for the program to end (it is killed), and gives <c>null</c>.</param>
    /// <param name="workingDirectory">The folder it runs in, or <c>null</c> for the server's.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What it produced, or <c>null</c> when <paramref name="readOutput"/> stopped early.</returns>
    /// <exception cref="IOException">It couldn't be started (or reading its output failed).</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">It couldn't be started.</exception>
    /// <exception cref="TimeoutException">It ran longer than <paramref name="timeLimit"/> (it was killed).</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled (it was killed).</exception>
    public static async Task<ExternalProcessResult<T>?> RunAsync<T>(string program, IEnumerable<string> arguments, TimeSpan timeLimit, Func<Stream, CancellationToken, Task<T?>> readOutput, string? workingDirectory, CancellationToken cancellationToken)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(readOutput);
        var info = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
        };
        if (workingDirectory is not null)
        {
            info.WorkingDirectory = workingDirectory;
        }

        foreach (var a in arguments)
        {
            info.ArgumentList.Add(a);
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeLimit);
        using var process = Process.Start(info) ?? throw new IOException(Path.GetFileName(program) + " could not be started.");
        try
        {
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
            {
                // Lowering priority is a courtesy; carry on without it
            }

            // Both pipes are drained at once so neither can fill up and stall the process
            var errors = TailAsync(process.StandardError, limit.Token);
            var output = await readOutput(process.StandardOutput.BaseStream, limit.Token).ConfigureAwait(false);
            if (output is null)
            {
                return null;
            }

            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            var tail = (await errors.ConfigureAwait(false)).Trim();
            return new ExternalProcessResult<T>(output, process.ExitCode, tail.Length > MaxErrorChars ? tail[^MaxErrorChars..] : tail);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(Path.GetFileName(program) + " took longer than " + timeLimit.TotalMinutes.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " minutes and was stopped.");
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already ended
            }
        }
    }

    /// <summary>
    /// Reads a standard output to its end, discarding it (for programs that write their result to a file).
    /// </summary>
    /// <param name="stream">The output.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A non-null marker.</returns>
    public static async Task<object?> DiscardAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        await stream.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
        return string.Empty;
    }

    // Reads standard error to its end, keeping only its last part in memory
    private static async Task<string> TailAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var kept = new StringBuilder();
        var chunk = new char[4096];
        int n;
        while ((n = await reader.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            kept.Append(chunk, 0, n);
            if (kept.Length > 2 * KeptErrorChars)
            {
                kept.Remove(0, kept.Length - KeptErrorChars);
            }
        }

        return kept.ToString();
    }
}
