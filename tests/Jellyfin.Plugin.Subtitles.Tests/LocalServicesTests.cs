using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

public class LocalServicesTests
{
    [Fact]
    public void Only_this_machine_is_looked_at()
    {
        var list = LocalServices.Candidates("http://192.168.1.20:8000/v1");
        Assert.All(list, a => Assert.StartsWith("http://localhost:", a, StringComparison.Ordinal));
        Assert.Equal("http://127.0.0.1:9100/v1", LocalServices.Candidates("http://127.0.0.1:9100/v1/")[0]);
    }

    [Fact]
    public async Task Services_are_recognised_by_their_model_list()
    {
        using var http = new HttpClient(new Fake());

        var found = await LocalServices.FindAsync(http, null, false, TestContext.Current.CancellationToken);

        var service = Assert.Single(found);
        Assert.Equal("http://localhost:8000/v1", service.Address);
        Assert.Equal(["Systran/faster-whisper-small"], service.Models);
        Assert.Null(LocalServices.ModelsOf("<html>not an API</html>"));
    }

    [Theory]
    [InlineData("nvenc", true, "latest-cuda")]
    [InlineData("vaapi", false, "latest-cpu")]
    [InlineData(null, false, "latest-cpu")]
    public void The_suggestion_follows_the_hardware(string? accel, bool gpu, string image)
    {
        var s = LocalServices.Suggest(accel);
        Assert.Equal(gpu, s.Gpu);
        Assert.Contains(image, s.Commands[0], StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:8000:8000", s.Commands[0], StringComparison.Ordinal);
        Assert.Equal(gpu, s.Commands[0].Contains("--gpus=all", StringComparison.Ordinal));
    }

    // SUB-23: from a container, localhost is Jellyfin's own container
    [Fact]
    public void A_container_is_recognised_by_its_runtime_files()
    {
        Assert.True(LocalServices.IsContainer(p => p == "/.dockerenv"));
        Assert.True(LocalServices.IsContainer(p => p == "/run/.containerenv"));
        Assert.False(LocalServices.IsContainer(_ => false));
    }

    [Fact]
    public void From_a_container_a_shared_network_is_suggested()
    {
        var s = LocalServices.Suggest("none", inContainer: true);

        Assert.Equal("http://speaches:8000/v1", s.Address);
        Assert.DoesNotContain(s.Commands, c => c.Contains("127.0.0.1", StringComparison.Ordinal) || c.Contains("localhost", StringComparison.Ordinal));
        Assert.Contains(s.Commands, c => c.StartsWith("docker network create", StringComparison.Ordinal));
        Assert.Contains(s.Commands, c => c.Contains("--network " + LocalServices.SharedNetwork, StringComparison.Ordinal) && c.Contains("--name speaches", StringComparison.Ordinal));
        Assert.Contains("--add-host=host.docker.internal:host-gateway", s.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void From_a_container_the_shared_network_and_the_host_are_looked_at_too()
    {
        Assert.DoesNotContain("http://speaches:8000/v1", LocalServices.Candidates(null));
        var list = LocalServices.Candidates(null, inContainer: true);
        Assert.Contains("http://speaches:8000/v1", list);
        Assert.Contains("http://host.docker.internal:8000/v1", list);
    }

    private sealed class Fake : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => request.RequestUri!.Port == 8000
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"id\":\"Systran/faster-whisper-small\"}]}") })
                : request.RequestUri.Port == 8080
                    ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html></html>") })
                    : throw new HttpRequestException("refused");
    }
}
