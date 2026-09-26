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

    // SUB-26: account calls go through the shared provider HTTP helper
    [Fact]
    public async Task A_rejected_key_is_an_authentication_failure_not_a_transient_one()
    {
        DeepgramAccount.ClearCache();
        using var http = new HttpClient(new FakeDeepgram());

        var ex = await Assert.ThrowsAsync<SpeechToTextException>(() => DeepgramAccount.BalanceAsync(http, "wrong-key-0123456789abcdef", TimeProvider.System, TestContext.Current.CancellationToken));

        Assert.Equal(Jellyfin.Plugin.Common.Resilience.FailureClass.Authentication, ex.Failure);
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Equal("Deepgram didn't accept this key.", ex.Message);
    }

    [Fact]
    public async Task A_huge_error_body_is_not_read_in_full()
    {
        var endless = new Endless();
        using var http = new HttpClient(new Huge(endless));

        var ex = await Assert.ThrowsAsync<SpeechToTextException>(() => DeepgramAccount.CanReadBillingAsync(http, AdminKey, TestContext.Current.CancellationToken));

        Assert.Equal(Jellyfin.Plugin.Common.Resilience.FailureClass.Transient, ex.Failure);
        Assert.InRange(endless.Served, 1, HttpSpeechToText.MaxReplyBytes + (64 * 1024));
    }

    // SUB-31: the key swap lives in DeepgramAccount, not in the controller
    [Theory]
    [InlineData(false, Configuration.BalanceSource.TranscriptionKey, Configuration.BalanceSource.Off)]
    [InlineData(true, Configuration.BalanceSource.TranscriptionKey, Configuration.BalanceSource.SeparateKey)]
    public async Task An_admin_key_is_swapped_for_a_limited_one(bool keep, Configuration.BalanceSource before, Configuration.BalanceSource after)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "subs-dg-" + Guid.NewGuid().ToString("N"));
        try
        {
            var keys = new SpeechToTextKeys(System.IO.Path.Combine(dir, "keys.json"));
            using var http = new HttpClient(new FakeDeepgram());

            var none = await DeepgramAccount.LimitTranscriptionKeyAsync(http, keys, keep, before, TestContext.Current.CancellationToken);
            Assert.False(none.Swapped);

            keys.Set(SpeechToTextFactory.Deepgram, AdminKey);
            var done = await DeepgramAccount.LimitTranscriptionKeyAsync(http, keys, keep, before, TestContext.Current.CancellationToken);

            Assert.True(done.Swapped);
            Assert.Equal(after, done.BalanceSource);
            Assert.Equal("new-limited-key-0123456789", keys.Get(SpeechToTextFactory.Deepgram));
            Assert.Equal(keep ? AdminKey : null, keys.Get(DeepgramAccount.BillingKey));
            Assert.Contains("stays there if this plugin is removed", done.Message, StringComparison.Ordinal);

            // Already limited: nothing changes
            keys.Set(SpeechToTextFactory.Deepgram, MemberKey);
            var again = await DeepgramAccount.LimitTranscriptionKeyAsync(http, keys, keep, after, TestContext.Current.CancellationToken);
            Assert.False(again.Swapped);
            Assert.Equal(after, again.BalanceSource);
            Assert.Equal(MemberKey, keys.Get(SpeechToTextFactory.Deepgram));
        }
        finally
        {
            if (System.IO.Directory.Exists(dir))
            {
                System.IO.Directory.Delete(dir, recursive: true);
            }
        }
    }

    // SUB-27: the page is told which key reads the balance after the swap
    [Theory]
    [InlineData(Configuration.BalanceSource.TranscriptionKey, false, Configuration.BalanceSource.Off)]
    [InlineData(Configuration.BalanceSource.TranscriptionKey, true, Configuration.BalanceSource.SeparateKey)]
    [InlineData(Configuration.BalanceSource.Off, true, Configuration.BalanceSource.SeparateKey)]
    [InlineData(Configuration.BalanceSource.Off, false, Configuration.BalanceSource.Off)]
    [InlineData(Configuration.BalanceSource.SeparateKey, false, Configuration.BalanceSource.SeparateKey)]
    public void The_balance_setting_follows_a_key_swap(Configuration.BalanceSource before, bool kept, Configuration.BalanceSource after)
        => Assert.Equal(after, DeepgramAccount.BalanceAfterLimiting(before, kept));

    private sealed class Huge(Endless body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StreamContent(body) });
    }

    private sealed class Endless : System.IO.Stream
    {
        public long Served { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => Served; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)'x', offset, count);
            Served += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
