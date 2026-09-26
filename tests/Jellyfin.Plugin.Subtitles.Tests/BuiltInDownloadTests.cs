using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.SpeechToText.BuiltIn;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// SUB-25: the built-in download runs in the background, one at a time, and reports its progress
public sealed class BuiltInDownloadTests : IDisposable
{
    private static readonly Uri Base = new("https://github.com/owner/repo/releases/download/whisper-v0-1/");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "subs-download-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Progress_counts_bytes_and_moves_through_its_states()
    {
        var progress = new BuiltInProgress();
        Assert.Equal(BuiltInProgress.Idle, progress.Report(installed: false).State);
        Assert.Equal(BuiltInProgress.Installed, progress.Report(installed: true).State);

        progress.Begin(200);
        progress.Add(50);
        var status = progress.Report(installed: false);
        Assert.Equal(new BuiltInInstallStatus(BuiltInProgress.Downloading, 50, 200, 25, null), status);

        // A download given up on no longer counts; more than the total is never shown
        progress.Discard(30);
        Assert.Equal(20, progress.Report(false).BytesDone);
        progress.Add(1000);
        Assert.Equal(100, progress.Report(false).Percent);

        progress.Verify();
        Assert.Equal(BuiltInProgress.Verifying, progress.Report(installed: true).State);

        progress.Complete();
        Assert.Equal(BuiltInProgress.Installed, progress.Report(installed: true).State);

        // Installed earlier but since removed (or another model chosen): nothing to show
        Assert.Equal(BuiltInProgress.Idle, progress.Report(installed: false).State);
    }

    [Fact]
    public void A_failure_is_shown_until_a_new_download_starts()
    {
        var progress = new BuiltInProgress();
        progress.Begin(10);
        progress.Fail("no network");
        Assert.Equal(new BuiltInInstallStatus(BuiltInProgress.Failed, 0, 0, null, "no network"), progress.Report(false));
        Assert.Contains("no network", BuiltInProgress.TestMessage(progress.Report(false)), StringComparison.Ordinal);

        progress.Begin(10);
        Assert.Equal(BuiltInProgress.Downloading, progress.Report(false).State);
        Assert.Null(progress.Report(false).Error);
    }

    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(0, 100, 0)]
    [InlineData(999, 1000, 99)]
    [InlineData(1000, 1000, 100)]
    public void Percent_rounds_down(long done, long total, int? expected)
        => Assert.Equal(expected, BuiltInProgress.Percent(done, total));

    [Fact]
    public void Test_says_how_far_the_download_is()
    {
        Assert.Contains("downloading (42%)", BuiltInProgress.TestMessage(new BuiltInInstallStatus(BuiltInProgress.Downloading, 42, 100, 42, null)), StringComparison.Ordinal);
        Assert.Contains("being checked", BuiltInProgress.TestMessage(new BuiltInInstallStatus(BuiltInProgress.Verifying, 1, 1, 100, null)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Single_flight_joins_work_that_is_running()
    {
        var flight = new SingleFlight();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        var starts = 0;
        Task Work(CancellationToken token)
        {
            Interlocked.Increment(ref runs);
            return gate.Task;
        }

        Assert.True(flight.TryStart(Work, CancellationToken.None, () => starts++));
        Assert.True(flight.Running);
        Assert.False(flight.TryStart(Work, CancellationToken.None, () => starts++));
        Assert.Equal(1, starts);

        gate.SetResult();
        await flight.Current!;
        Assert.False(flight.Running);
        Assert.Equal(1, runs);

        // Once finished, it can run again
        Assert.True(flight.TryStart(_ => Task.CompletedTask, CancellationToken.None));
        await flight.Current!;
    }

    [Fact]
    public async Task Progress_is_reported_while_files_arrive()
    {
        var (source, served) = Release();
        BuiltInInstallStatus? whileModel = null;
        BuiltInInstaller? installer = null;
        using var handler = new Server(served, name =>
        {
            if (name == "ggml-base-q8_0.bin")
            {
                whileModel = installer!.Progress.Report(false);
            }

            return Task.CompletedTask;
        });
        installer = new BuiltInInstaller(_dir, source, handler);
        using (installer)
        {
            await installer.EnsureAsync("linux-x64", "base", TestContext.Current.CancellationToken);

            var zip = served["whisper-cli-linux-x64.zip"].Length;
            var total = zip + served["ggml-base-q8_0.bin"].Length;

            // When the model was asked for, the program's zip had arrived (and been unpacked and checked)
            Assert.NotNull(whileModel);
            Assert.Equal(BuiltInProgress.Verifying, whileModel.State);
            Assert.Equal(zip, whileModel.BytesDone);
            Assert.Equal(total, whileModel.BytesTotal);
            Assert.Equal(BuiltInProgress.Installed, installer.Progress.Report(installer.IsInstalled("linux-x64", "base")).State);
        }
    }

    [Fact]
    public async Task One_background_download_runs_and_Test_does_not_wait_for_it()
    {
        var (source, served) = Release();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Server(served, _ => gate.Task);
        using var host = new BuiltInHost(_dir, new BuiltInInstaller(Path.Combine(_dir, "builtin"), source, handler), "linux-x64");

        Assert.True(host.StartInstall("base"));
        Assert.True(host.Installing);
        Assert.False(host.StartInstall("base"));
        Assert.Equal(BuiltInProgress.Downloading, host.Status("base").State);

        gate.SetResult();
        await host.Background!;
        Assert.False(host.Installing);
        Assert.True(host.IsInstalled("base"));
        Assert.Equal(BuiltInProgress.Installed, host.Status("base").State);
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task A_failed_background_download_is_shown_and_can_be_retried()
    {
        var (source, served) = Release();
        served.Remove("ggml-base-q8_0.bin");
        using var handler = new Server(served, _ => Task.CompletedTask);
        using var host = new BuiltInHost(_dir, new BuiltInInstaller(Path.Combine(_dir, "builtin"), source, handler), "linux-x64");

        Assert.True(host.StartInstall("base"));
        await host.Background!;
        var status = host.Status("base");
        Assert.Equal(BuiltInProgress.Failed, status.State);
        Assert.Contains("HTTP 404", status.Error, StringComparison.Ordinal);

        Assert.True(host.StartInstall("base"));
        await host.Background!;
    }

    [Fact]
    public async Task Stopping_the_server_cancels_a_background_download()
    {
        var (source, served) = Release();
        var never = new TaskCompletionSource();
        var handler = new Server(served, _ => never.Task);
        var host = new BuiltInHost(_dir, new BuiltInInstaller(Path.Combine(_dir, "builtin"), source, handler), "linux-x64");

        Assert.True(host.StartInstall("base"));
        var background = host.Background!;
        host.Dispose();
        await background;
        Assert.Equal(BuiltInProgress.Failed, host.Installer.Progress.Report(false).State);
        handler.Dispose();
    }

    private static byte[] Zip(Dictionary<string, string> entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(text);
            }
        }

        return ms.ToArray();
    }

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static (BuiltInSource Source, Dictionary<string, byte[]> Served) Release()
    {
        var zip = Zip(new() { ["whisper-cli"] = "program", ["LICENSE-whisper.cpp.txt"] = "MIT" });
        var files = new Dictionary<string, string> { ["whisper-cli"] = Sha("program"), ["LICENSE-whisper.cpp.txt"] = Sha("MIT") };
        var model = Encoding.UTF8.GetBytes("a model of some length");
        var downloads = new List<BuiltInDownload>
        {
            new("whisper-cli-linux-x64.zip", Convert.ToHexStringLower(SHA256.HashData(zip)), zip.Length, files),
            new("ggml-base-q8_0.bin", Convert.ToHexStringLower(SHA256.HashData(model)), model.Length, null),
        };
        var served = new Dictionary<string, byte[]> { ["whisper-cli-linux-x64.zip"] = zip, ["ggml-base-q8_0.bin"] = model };
        return (new BuiltInSource(Base, "whisper-v0-1", downloads, ["github.com"]), served);
    }

    // Serves the release; each request first waits for what the test says
    private sealed class Server(Dictionary<string, byte[]> files, Func<string, Task> before) : HttpMessageHandler
    {
        private int _requests;

        public int Requests => _requests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            var name = request.RequestUri!.Segments[^1];
            await before(name).WaitAsync(cancellationToken);
            return files.TryGetValue(name, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
