using System;
using System.IO;
using System.Net.Http;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Jellyfin.Plugin.Subtitles.SpeechToText.BuiltIn;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

public sealed class FactoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "subs-keys-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http = new();

    private SpeechToTextKeys Keys() => new(Path.Combine(_dir, "keys.json"));

    public void Dispose()
    {
        _http.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void A_local_service_needs_only_its_address()
    {
        var (service, problem) = SpeechToTextFactory.Create("local", string.Empty, "http://localhost:8000/v1", paidAllowed: false, builtInAllowed: false, Keys(), _http, null);

        Assert.Null(problem);
        Assert.Equal("local", Assert.IsType<OpenAiCompatibleSpeechToText>(service).Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not an address")]
    public void A_local_service_without_an_address_says_so(string address)
    {
        var (service, problem) = SpeechToTextFactory.Create("local", string.Empty, address, false, false, Keys(), _http, null);

        Assert.Null(service);
        Assert.Contains("address", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_local_service_key_is_never_sent_over_http_to_the_internet()
    {
        var keys = Keys();
        keys.Set("local", "local-key-0123456789");

        var (service, problem) = SpeechToTextFactory.Create("local", string.Empty, "http://stt.example.test/v1", false, false, keys, _http, null);

        Assert.Null(service);
        Assert.Contains("https", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("deepgram")]
    [InlineData("openai")]
    public void Paid_services_need_a_key_and_a_limit_above_zero(string provider)
    {
        var keys = Keys();
        Assert.Contains("key", SpeechToTextFactory.Create(provider, string.Empty, string.Empty, true, false, keys, _http, null).Problem, StringComparison.Ordinal);

        keys.Set(provider, "cloud-key-0123456789");
        Assert.Contains("limit is 0", SpeechToTextFactory.Create(provider, string.Empty, string.Empty, false, false, keys, _http, null).Problem, StringComparison.Ordinal);

        var (service, problem) = SpeechToTextFactory.Create(provider, string.Empty, string.Empty, true, false, keys, _http, null);
        Assert.Null(problem);
        Assert.Equal(provider, service!.Id);
    }

    [Fact]
    public void Built_in_asks_for_permission_first()
    {
        var ask = SpeechToTextFactory.Create("builtin", string.Empty, string.Empty, true, false, Keys(), _http, null).Problem;
        Assert.Contains("permission", ask, StringComparison.Ordinal);

        // SUB-29: the consent box is below the services, so the message names it rather than pointing "above"
        Assert.DoesNotContain("above", ask, StringComparison.Ordinal);
        Assert.Contains("Allow the built-in speech-to-text to download and run", ask, StringComparison.Ordinal);
        Assert.Contains("no build", SpeechToTextFactory.Create("builtin", string.Empty, string.Empty, true, true, Keys(), _http, null).Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Built_in_is_created_once_allowed_with_a_known_model()
    {
        using var host = new BuiltInHost(_dir, platform: "linux-x64");
        var (service, problem) = SpeechToTextFactory.Create("builtin", string.Empty, string.Empty, false, true, Keys(), _http, host);
        Assert.Null(problem);
        Assert.Equal("builtin", service!.Id);
        Assert.NotNull(SpeechToTextFactory.Create("builtin", "Small", string.Empty, false, true, Keys(), _http, host).Service);
        Assert.Contains("Unknown built-in model", SpeechToTextFactory.Create("builtin", "large", string.Empty, false, true, Keys(), _http, host).Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Keys_are_write_only_and_limited_to_known_services()
    {
        var keys = Keys();
        keys.Set("deepgram", "cloud-key-0123456789");

        Assert.True(keys.Status()["deepgram"]);
        Assert.False(keys.Status()["openai"]);
        Assert.Throws<ArgumentException>(() => keys.Set("anthropic", "cloud-key-0123456789"));
    }
}
