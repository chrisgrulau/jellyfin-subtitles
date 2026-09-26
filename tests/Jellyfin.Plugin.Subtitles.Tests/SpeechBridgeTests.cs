using System;
using System.IO;
using Jellyfin.Plugin.Subtitles.Bridge;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

public sealed class SpeechBridgeTests : IDisposable
{
    private static readonly SpeechBridge.Settings Allowed = new(true, true, true);
    private readonly string _video = Path.Combine(Path.GetTempPath(), "bridge-" + Guid.NewGuid().ToString("N") + ".mkv");

    public SpeechBridgeTests() => File.WriteAllBytes(_video, [0]);

    public void Dispose() => File.Delete(_video);

    private string Request(string caller = "ingest", string purpose = "ingest.episode", double start = 300, double length = 120, string? path = null, string language = "\"en\"", int version = 1)
        => $$"""{"version":{{version}},"caller":"{{caller}}","purpose":"{{purpose}}","path":{{System.Text.Json.JsonSerializer.Serialize(path ?? _video)}},"start":{{start}},"length":{{length}},"language":{{language}}}""";

    [Fact]
    public void A_valid_request_from_an_allowed_caller_is_read()
    {
        Assert.Null(SpeechBridge.Parse(Request(), Allowed, out var request));
        Assert.Equal("ingest.episode", request!.Purpose);
        Assert.Equal(TimeSpan.FromMinutes(5), request.Start);
        Assert.Equal(TimeSpan.FromMinutes(2), request.Length);
        Assert.Equal("en", request.Language);
        Assert.Null(SpeechBridge.Parse(Request(language: "null"), Allowed, out request));
        Assert.Null(request!.Language);
    }

    [Theory]
    [InlineData(false, true, true, "not-allowed")]
    [InlineData(true, false, true, "not-allowed")]
    [InlineData(true, true, false, "not-set-up")]
    public void Settings_decide_who_may_ask(bool enabled, bool allowIngest, bool aiContextOn, string failure)
    {
        var problem = SpeechBridge.Parse(Request(), new SpeechBridge.Settings(enabled, allowIngest, aiContextOn), out var request);
        Assert.Equal(failure, problem!.Failure);
        Assert.Null(request);
    }

    [Fact]
    public void Only_known_callers_with_their_own_purposes_are_answered()
    {
        Assert.Equal("not-allowed", SpeechBridge.Parse(Request(caller: "other", purpose: "other.x"), Allowed, out _)!.Failure);
        Assert.Equal("not-allowed", SpeechBridge.Parse(Request(purpose: "subtitles.sync"), Allowed, out _)!.Failure);
    }

    [Theory]
    [InlineData(-1, 120)]
    [InlineData(0, 0)]
    [InlineData(0, 181)]
    public void The_stretch_is_bounded(double start, double length)
        => Assert.Equal("bad-request", SpeechBridge.Parse(Request(start: start, length: length), Allowed, out _)!.Failure);

    [Fact]
    public void Missing_or_relative_paths_bad_languages_versions_and_JSON_are_refused()
    {
        Assert.Equal("bad-request", SpeechBridge.Parse(Request(path: _video + ".gone"), Allowed, out _)!.Failure);
        Assert.Equal("bad-request", SpeechBridge.Parse(Request(path: "video.mkv"), Allowed, out _)!.Failure);
        Assert.Equal("bad-request", SpeechBridge.Parse(Request(language: "\"english!\""), Allowed, out _)!.Failure);
        Assert.Equal("bad-request", SpeechBridge.Parse(Request(version: 2), Allowed, out _)!.Failure);
        Assert.Equal("bad-request", SpeechBridge.Parse("{\"version\":\"one\"}", Allowed, out _)!.Failure);
        Assert.Equal("bad-request", SpeechBridge.Parse("not json", Allowed, out _)!.Failure);
        Assert.Equal("bad-request", SpeechBridge.Parse(new string(' ', SpeechBridge.MaxRequest + 1), Allowed, out _)!.Failure);
    }

    [Fact]
    public void The_words_become_one_line_of_text()
    {
        var transcript = new Transcript([new(" Where", 0, 0.3, null), new("were ", 0.3, 0.5, null), new("you?\n", 0.5, 0.9, null)], "en", "builtin", "base", 2);
        Assert.Equal("Where were you?", SpeechBridge.Text(transcript));
    }
}
