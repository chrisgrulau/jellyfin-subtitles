using System;
using System.IO;
using System.Net.Http;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Jellyfin.Plugin.Subtitles.SpeechToText.BuiltIn;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// SUB-22: the built-in program is only offered where it can start
public sealed class SystemFitTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "subs-fit-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http = new();

    public void Dispose()
    {
        _http.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Musl_is_refused_whatever_the_version()
    {
        var problem = BuiltInSystem.Problem(true, "linux-musl-x64", "2.40");
        Assert.Contains("musl", problem, StringComparison.Ordinal);
        Assert.Contains("local speech-to-text service or a cloud service", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2.31", false)]
    [InlineData("2.34", false)]
    [InlineData("2.35", true)]
    [InlineData("2.36.9000", true)]
    [InlineData("3.0", true)]
    public void Glibc_must_be_new_enough(string version, bool fits)
    {
        var problem = BuiltInSystem.Problem(true, "linux-x64", version);
        Assert.Equal(fits, problem is null);
        if (!fits)
        {
            Assert.Contains("glibc " + version, problem, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("stable")]
    public void An_unreadable_version_counts_as_unsuitable(string? version)
        => Assert.NotNull(BuiltInSystem.Problem(true, "linux-x64", version));

    [Fact]
    public void Other_systems_are_not_checked()
        => Assert.Null(BuiltInSystem.Problem(false, "win-x64", null));

    [Fact]
    public void This_test_machine_is_checked_without_throwing()
    {
        // Whatever the answer, reading it must not throw (it's read through the C library on Linux)
        _ = BuiltInSystem.ProblemHere();
    }

    [Fact]
    public void An_unsuitable_system_is_explained_instead_of_downloading()
    {
        using var host = new BuiltInHost(_dir, platform: "linux-x64") { Problem = "can't run here" };
        var (service, problem) = SpeechToTextFactory.Create("builtin", string.Empty, string.Empty, false, true, new SpeechToTextKeys(Path.Combine(_dir, "keys.json")), _http, host);

        Assert.Null(service);
        Assert.Equal("can't run here", problem);
    }
}
