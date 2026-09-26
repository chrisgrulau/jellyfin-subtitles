using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Jellyfin.Plugin.Subtitles.SpeechToText.BuiltIn;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

public sealed class BuiltInTests : IDisposable
{
    private static readonly Uri Base = new("https://github.com/owner/repo/releases/download/whisper-v0-1/");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "subs-builtin-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task Installs_verified_files_once_and_reuses_them()
    {
        var files = Release();
        using var handler = new FakeServer(files.Served);
        using var installer = new BuiltInInstaller(_dir, files.Source, handler);

        var (program, model) = await installer.EnsureAsync("linux-x64", "base", CancellationToken.None);

        Assert.Equal("program", await File.ReadAllTextAsync(program, TestContext.Current.CancellationToken));
        Assert.Equal("model", await File.ReadAllTextAsync(model, TestContext.Current.CancellationToken));
        Assert.True(installer.IsInstalled("linux-x64", "base"));
        var requests = handler.Requests.Count;
        await installer.EnsureAsync("linux-x64", "base", CancellationToken.None);
        Assert.Equal(requests, handler.Requests.Count);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(program)!));
            Assert.True(File.GetUnixFileMode(program).HasFlag(UnixFileMode.UserExecute));
            Assert.False(File.GetUnixFileMode(program).HasFlag(UnixFileMode.OtherRead));
        }
    }

    [Fact]
    public async Task A_changed_file_is_replaced_before_it_runs()
    {
        var files = Release();
        using var handler = new FakeServer(files.Served);
        using var installer = new BuiltInInstaller(_dir, files.Source, handler);
        var (program, _) = await installer.EnsureAsync("linux-x64", "base", CancellationToken.None);

        await File.WriteAllTextAsync(program, "tampered", TestContext.Current.CancellationToken);
        await installer.EnsureAsync("linux-x64", "base", CancellationToken.None);

        Assert.Equal("program", await File.ReadAllTextAsync(program, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_download_that_does_not_match_its_checksum_is_refused()
    {
        var files = Release();
        files.Served["ggml-base-q8_0.bin"] = Encoding.UTF8.GetBytes("MODEL");
        using var handler = new FakeServer(files.Served);
        using var installer = new BuiltInInstaller(_dir, files.Source, handler);

        var ex = await Assert.ThrowsAsync<SpeechToTextException>(() => installer.EnsureAsync("linux-x64", "base", CancellationToken.None));

        Assert.Contains("checksum", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.NeedsAttention);
        Assert.False(File.Exists(Path.Combine(_dir, "models", "ggml-base-q8_0.bin")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "models")));
    }

    [Fact]
    public async Task A_zip_with_an_unexpected_file_is_refused()
    {
        // The zip matches its compiled-in checksum, but holds a file the list doesn't name
        var zip = Zip(new() { ["whisper-cli"] = "program", ["extra.so"] = "x" });
        var files = Release(zip, new Dictionary<string, string> { ["whisper-cli"] = Sha("program") });
        using var handler = new FakeServer(files.Served);
        using var installer = new BuiltInInstaller(_dir, files.Source, handler);

        var ex = await Assert.ThrowsAsync<SpeechToTextException>(() => installer.EnsureAsync("linux-x64", "base", CancellationToken.None));

        Assert.Contains("exactly the expected files", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_dir, "whisper-v0-1", "linux-x64")));
    }

    [Fact]
    public async Task A_zip_with_a_folder_path_is_refused()
    {
        var zip = Zip(new() { ["../whisper-cli"] = "program" });
        var files = Release(zip, new Dictionary<string, string> { ["../whisper-cli"] = Sha("program") });
        using var handler = new FakeServer(files.Served);
        using var installer = new BuiltInInstaller(_dir, files.Source, handler);

        await Assert.ThrowsAsync<SpeechToTextException>(() => installer.EnsureAsync("linux-x64", "base", CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_dir, "whisper-v0-1", "whisper-cli")));
    }

    [Fact]
    public async Task Redirects_are_followed_only_to_allowed_https_hosts()
    {
        var files = Release();
        using var good = new FakeServer(files.Served) { RedirectTo = "https://objects.example.test/" };
        using (var installer = new BuiltInInstaller(Path.Combine(_dir, "a"), files.Source, good))
        {
            await installer.EnsureAsync("linux-x64", "base", CancellationToken.None);
        }

        Assert.Contains(good.Requests, u => u.Host == "objects.example.test");

        foreach (var target in new[] { "https://elsewhere.example.test/", "http://objects.example.test/" })
        {
            using var bad = new FakeServer(files.Served) { RedirectTo = target };
            using var installer = new BuiltInInstaller(Path.Combine(_dir, "b"), files.Source, bad);
            var ex = await Assert.ThrowsAsync<SpeechToTextException>(() => installer.EnsureAsync("linux-x64", "base", CancellationToken.None));
            Assert.Contains("isn't allowed", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(bad.Requests, u => u.Host == "elsewhere.example.test");
        }
    }

    [Fact]
    public async Task Unknown_platforms_and_models_are_refused_without_downloading()
    {
        var files = Release();
        using var handler = new FakeServer(files.Served);
        using var installer = new BuiltInInstaller(_dir, files.Source, handler);

        await Assert.ThrowsAsync<SpeechToTextException>(() => installer.EnsureAsync("freebsd-x64", "base", CancellationToken.None));
        await Assert.ThrowsAsync<SpeechToTextException>(() => installer.EnsureAsync("linux-x64", "large", CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void The_published_release_lists_every_platform_and_model()
    {
        var source = BuiltInSource.Published;
        foreach (var platform in new[] { "linux-x64", "linux-arm64", "windows-x64", "macos-arm64", "macos-x64" })
        {
            var program = source.Program(platform);
            Assert.NotNull(program);
            Assert.Contains(program!.Files!.Keys, f => f is "whisper-cli" or "whisper-cli.exe");
            Assert.All(program.Files.Keys, f => Assert.Equal(Path.GetFileName(f), f));
        }

        Assert.NotNull(source.Model("base"));
        Assert.NotNull(source.Model("small"));
        Assert.All(source.Downloads, d => Assert.Matches("^[0-9a-f]{64}$", d.Sha256));
        Assert.Equal("https", source.BaseUrl.Scheme);
        Assert.Equal("github.com", source.BaseUrl.Host);
    }

    [Fact]
    public void Arguments_are_separate_absolute_and_never_read_as_options()
    {
        using var installer = new BuiltInInstaller(_dir);
        var stt = new BuiltInSpeechToText(installer, Path.Combine(_dir, "work"), "linux-x64", "small");

        var args = stt.Arguments(Path.Combine(_dir, "m.bin"), Path.Combine(_dir, "-x.wav"), Path.Combine(_dir, "out"), "eng");

        Assert.Equal("en", args[Array.IndexOf(args, "--language") + 1]);
        Assert.Equal("small", args[Array.IndexOf(args, "--dtw") + 1]);
        Assert.True(Path.IsPathRooted(args[Array.IndexOf(args, "--file") + 1]));
        Assert.Equal("auto", stt.Arguments("m", "a.wav", "o", null)[Array.IndexOf(args, "--language") + 1]);
    }

    [Fact]
    public void Output_becomes_words_with_calibrated_times()
    {
        const string json = """
            {"result":{"language":"en"},"transcription":[
              {"tokens":[
                {"text":"[_BEG_]","offsets":{"from":0,"to":0},"p":0.9,"t_dtw":-1},
                {"text":" Hello","offsets":{"from":1000,"to":1400},"p":0.9,"t_dtw":130},
                {"text":",","offsets":{"from":1400,"to":1400},"p":0.8,"t_dtw":140},
                {"text":" Mich","offsets":{"from":1500,"to":1700},"p":0.6,"t_dtw":160},
                {"text":"ael","offsets":{"from":1700,"to":1900},"p":0.8,"t_dtw":175},
                {"text":" [","offsets":{"from":2000,"to":2000},"p":0.5,"t_dtw":200},
                {"text":"Music","offsets":{"from":2000,"to":2100},"p":0.5,"t_dtw":201},
                {"text":" playing","offsets":{"from":2100,"to":2200},"p":0.5,"t_dtw":210},
                {"text":"]","offsets":{"from":2200,"to":2200},"p":0.5,"t_dtw":220},
                {"text":" yes","offsets":{"from":3000,"to":3300},"p":1.0,"t_dtw":-1},
                {"text":"[_TT_150]","offsets":{"from":3300,"to":3300},"p":1.0,"t_dtw":-1}
              ]}]}
            """;

        var (words, language) = WhisperCppOutput.Parse(json);

        Assert.Equal("en", language);
        Assert.Equal(["Hello,", "Michael", "yes"], words.Select(w => w.Text));
        Assert.Equal(1.30 - WhisperCppOutput.DtwLag, words[0].Start, 3);
        Assert.Equal(1.4, words[0].End, 3);
        Assert.Equal(1.60 - WhisperCppOutput.DtwLag, words[1].Start, 3);
        Assert.Equal(0.7, words[1].Confidence!.Value, 3);
        Assert.Equal(3.0, words[2].Start, 3);
    }

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

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

    private static (BuiltInSource Source, Dictionary<string, byte[]> Served) Release(byte[]? zip = null, Dictionary<string, string>? files = null)
    {
        zip ??= Zip(new() { ["whisper-cli"] = "program", ["libggml.so"] = "library", ["LICENSE-whisper.cpp.txt"] = "MIT" });
        files ??= new() { ["whisper-cli"] = Sha("program"), ["libggml.so"] = Sha("library"), ["LICENSE-whisper.cpp.txt"] = Sha("MIT") };
        var model = Encoding.UTF8.GetBytes("model");
        var downloads = new List<BuiltInDownload>
        {
            new("whisper-cli-linux-x64.zip", Convert.ToHexStringLower(SHA256.HashData(zip)), zip.Length, files),
            new("ggml-base-q8_0.bin", Convert.ToHexStringLower(SHA256.HashData(model)), model.Length, null),
        };
        var served = new Dictionary<string, byte[]> { ["whisper-cli-linux-x64.zip"] = zip, ["ggml-base-q8_0.bin"] = model };
        return (new BuiltInSource(Base, "whisper-v0-1", downloads, ["github.com", "objects.example.test"]), served);
    }

    private sealed class FakeServer(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public string? RedirectTo { get; init; }

        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            var name = uri.Segments[^1];
            if (RedirectTo is not null && uri.Host == "github.com")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri(new Uri(RedirectTo), name);
                return Task.FromResult(redirect);
            }

            return Task.FromResult(files.TryGetValue(name, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    // SUB-15: an install under the plugins folder moves out of it on start
    [Fact]
    public void An_install_in_the_plugin_folder_moves_to_the_data_folder()
    {
        var legacy = Path.Combine(_dir, "plugins", "Jellyfin.Plugin.Subtitles", "builtin");
        Directory.CreateDirectory(Path.Combine(legacy, "bin"));
        File.WriteAllText(Path.Combine(legacy, "bin", "whisper.dll"), "x");
        var target = Path.Combine(_dir, "data", "shoal-subtitles", "builtin");

        BuiltInHost.MoveFromLegacy(legacy, target);

        Assert.False(Directory.Exists(legacy));
        Assert.True(File.Exists(Path.Combine(target, "bin", "whisper.dll")));
    }

    [Fact]
    public void An_old_copy_is_removed_when_the_new_place_already_has_one()
    {
        var legacy = Path.Combine(_dir, "plugins", "Jellyfin.Plugin.Subtitles", "builtin");
        var target = Path.Combine(_dir, "data", "shoal-subtitles", "builtin");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(legacy, "ggml.dll"), "old");

        BuiltInHost.MoveFromLegacy(legacy, target);

        Assert.False(Directory.Exists(legacy));
        Assert.True(Directory.Exists(target));
    }
}
