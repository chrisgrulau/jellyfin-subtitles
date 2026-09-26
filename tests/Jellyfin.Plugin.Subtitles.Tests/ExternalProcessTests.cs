using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// FAM-06: the three external program runners share one way of running a program
public class ExternalProcessTests
{
    private const string Shell = "/bin/sh";

    private static void NeedsShell() => Assert.SkipWhen(OperatingSystem.IsWindows() || !File.Exists(Shell), "Needs a Unix shell.");

    [Fact]
    public async Task Output_exit_code_and_the_end_of_stderr_are_returned()
    {
        NeedsShell();
        var script = "printf 'hello'; i=0; while [ $i -lt 200 ]; do printf 'noise %03d\\n' $i >&2; i=$((i+1)); done; printf 'the real reason' >&2; exit 3";

        var run = await ExternalProcess.RunAsync(Shell, ["-c", script], TimeSpan.FromSeconds(30), Read, null, TestContext.Current.CancellationToken);

        Assert.Equal("hello", run!.Output);
        Assert.Equal(3, run.ExitCode);
        Assert.EndsWith("the real reason", run.Errors, StringComparison.Ordinal);
        Assert.InRange(run.Errors.Length, 1, ExternalProcess.MaxErrorChars);
    }

    [Fact]
    public async Task Stopping_early_and_running_too_long_kill_the_program()
    {
        NeedsShell();
        var clock = Stopwatch.StartNew();

        Assert.Null(await ExternalProcess.RunAsync<string>(Shell, ["-c", "exec tail -f /dev/null"], TimeSpan.FromMinutes(5), (_, _) => Task.FromResult<string?>(null), null, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<TimeoutException>(() => ExternalProcess.RunAsync(Shell, ["-c", "exec tail -f /dev/null"], TimeSpan.FromSeconds(1), Read, null, TestContext.Current.CancellationToken));

        Assert.True(clock.Elapsed < TimeSpan.FromMinutes(1));
    }

    private static async Task<string?> Read(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }
}
