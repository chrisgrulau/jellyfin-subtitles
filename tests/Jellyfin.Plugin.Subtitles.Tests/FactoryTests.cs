using System;
using System.IO;
using System.Net.Http;
using Jellyfin.Plugin.Subtitles.SpeechToText;
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
        var (service, problem) = SpeechToTextFactory.Create("local", string.Empty, "http://localhost:8000/v1", paidAllowed: false, builtInAllowed: false, Keys(), _http);

        Assert.Null(problem);
        Assert.Equal("local", Assert.IsType<OpenAiCompatibleSpeechToText>(service).Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not an address")]
    public void A_local_service_without_an_address_says_so(string address)
    {
        var (service, problem) = SpeechToTextFactory.Create("local", string.Empty, address, false, false, Keys(), _http);

        Assert.Null(service);
        Assert.Contains("address", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_local_service_key_is_never_sent_over_http_to_the_internet()
    {
        var keys = Keys();
        keys.Set("local", "local-key-0123456789");

        var (service, problem) = SpeechToTextFactory.Create("local", string.Empty, "http://stt.example.test/v1", false, false, keys, _http);

        Assert.Null(service);
        Assert.Contains("https", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("deepgram")]
    [InlineData("openai")]
    public void Paid_services_need_a_key_and_a_limit_above_zero(string provider)
    {
        var keys = Keys();
        Assert.Contains("key", SpeechToTextFactory.Create(provider, string.Empty, string.Empty, true, false, keys, _http).Problem, StringComparison.Ordinal);

        keys.Set(provider, "cloud-key-0123456789");
        Assert.Contains("limit is 0", SpeechToTextFactory.Create(provider, string.Empty, string.Empty, false, false, keys, _http).Problem, StringComparison.Ordinal);

        var (service, problem) = SpeechToTextFactory.Create(provider, string.Empty, string.Empty, true, false, keys, _http);
        Assert.Null(problem);
        Assert.Equal(provider, service!.Id);
    }

    [Fact]
    public void Built_in_asks_for_permission_first()
    {
        Assert.Contains("permission", SpeechToTextFactory.Create("builtin", string.Empty, string.Empty, true, false, Keys(), _http).Problem, StringComparison.Ordinal);
        Assert.Contains("isn't available", SpeechToTextFactory.Create("builtin", string.Empty, string.Empty, true, true, Keys(), _http).Problem, StringComparison.Ordinal);
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
