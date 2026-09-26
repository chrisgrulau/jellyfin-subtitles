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

        var found = await LocalServices.FindAsync(http, null, TestContext.Current.CancellationToken);

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
