using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

public class DeepgramAccountTests
{
    private const string AdminKey = "admin-key-0123456789abcdef";
    private const string MemberKey = "member-key-0123456789abcdef";
    private const string Project = "0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b";

    [Fact]
    public async Task The_balance_is_read_with_an_admin_key_and_cached()
    {
        DeepgramAccount.ClearCache();
        var api = new FakeDeepgram();
        using var http = new HttpClient(api);

        var balance = await DeepgramAccount.BalanceAsync(http, AdminKey, TimeProvider.System, TestContext.Current.CancellationToken);
        await DeepgramAccount.BalanceAsync(http, AdminKey, TimeProvider.System, TestContext.Current.CancellationToken);

        Assert.Equal(154.17m, balance.Amount);
        Assert.Equal("USD", balance.Currency);
        Assert.Equal(2, api.Requests.Count);
        Assert.All(api.Requests, r => Assert.Equal("api.deepgram.com", r.Host));
    }

    [Fact]
    public async Task Admin_and_limited_keys_are_told_apart()
    {
        using var http = new HttpClient(new FakeDeepgram());

        Assert.True(await DeepgramAccount.CanReadBillingAsync(http, AdminKey, TestContext.Current.CancellationToken));
        Assert.False(await DeepgramAccount.CanReadBillingAsync(http, MemberKey, TestContext.Current.CancellationToken));
        var ex = await Assert.ThrowsAsync<SpeechToTextException>(() => DeepgramAccount.BalanceAsync(http, MemberKey + "x", TimeProvider.System, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(MemberKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_created_key_can_only_transcribe()
    {
        var api = new FakeDeepgram();
        using var http = new HttpClient(api);

        var key = await DeepgramAccount.CreateTranscriptionKeyAsync(http, AdminKey, "Shoal Subtitles", TestContext.Current.CancellationToken);

        Assert.Equal("new-limited-key-0123456789", key);
        Assert.Contains("\"scopes\":[\"usage:write\"]", api.CreatedWith, StringComparison.Ordinal);
    }

    private sealed class FakeDeepgram : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        public string CreatedWith { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            var key = request.Headers.Authorization?.Parameter;
            if (key is not (AdminKey or MemberKey))
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"err_msg\":\"Invalid credentials " + key + "\"}") };
            }

            if (uri.AbsolutePath == "/v1/projects")
            {
                return Json("{\"projects\":[{\"project_id\":\"" + Project + "\",\"name\":\"p\"}]}");
            }

            if (uri.AbsolutePath == "/v1/projects/" + Project + "/balances")
            {
                return key == AdminKey
                    ? Json("{\"balances\":[{\"balance_id\":\"b\",\"amount\":154.17,\"units\":\"usd\"}]}")
                    : new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"category\":\"INSUFFICIENT_PERMISSIONS\"}") };
            }

            if (uri.AbsolutePath == "/v1/projects/" + Project + "/keys" && request.Method == HttpMethod.Post && key == AdminKey)
            {
                CreatedWith = await request.Content!.ReadAsStringAsync(cancellationToken);
                return Json("{\"api_key_id\":\"k\",\"key\":\"new-limited-key-0123456789\",\"scopes\":[\"usage:write\"]}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    }
}
