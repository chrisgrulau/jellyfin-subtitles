using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Secrets;

namespace Jellyfin.Plugin.Subtitles.SpeechToText;

/// <summary>
/// A Deepgram project's remaining credit.
/// </summary>
/// <param name="Amount">The amount.</param>
/// <param name="Currency">Its currency (Deepgram reports US dollars).</param>
/// <param name="Checked">When it was read.</param>
public sealed record DeepgramBalance(decimal Amount, string Currency, DateTimeOffset Checked);

/// <summary>
/// Reads a Deepgram project's credit balance and, on request, creates a transcription-only key from an Admin key.
/// <para>
/// An Admin (or Owner) key can create and delete keys and see billing for the whole project, so it is handled with
/// care: it is only ever sent to Deepgram's own API address, only for these calls (list projects, read balances, and
/// create a key when the administrator asks), never used for transcription, and every error message is redacted.
/// </para>
/// </summary>
public static partial class DeepgramAccount
{
    /// <summary>Deepgram's management API.</summary>
    public static readonly Uri Api = new("https://api.deepgram.com/v1/");

    /// <summary>The key-store id of a separate billing key.</summary>
    public const string BillingKey = "deepgram-billing";

    /// <summary>The only scope a key created by the plugin gets: enough to transcribe, nothing else.</summary>
    public const string TranscriptionScope = "usage:write";

    private static readonly string[] TranscriptionScopes = [TranscriptionScope];
    private static readonly string[] Tags = ["shoal-subtitles"];
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);
    private static readonly Lock CacheLock = new();
    private static (string KeyHash, DeepgramBalance Balance)? _cache;

    /// <summary>
    /// Reads the credit balance (cached for a few minutes).
    /// </summary>
    /// <param name="http">HTTP client.</param>
    /// <param name="key">A key with the <c>billing:read</c> scope (Admin or Owner).</param>
    /// <param name="clock">Clock.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The balance.</returns>
    /// <exception cref="SpeechToTextException">It couldn't be read; the message says why (without the key).</exception>
    public static async Task<DeepgramBalance> BalanceAsync(HttpClient http, string key, TimeProvider clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
        lock (CacheLock)
        {
            if (_cache is { } c && c.KeyHash == hash && clock.GetUtcNow() - c.Balance.Checked < CacheFor)
            {
                return c.Balance;
            }
        }

        var project = await ProjectAsync(http, key, cancellationToken).ConfigureAwait(false);
        using var doc = await SendAsync(http, key, HttpMethod.Get, new Uri(Api, "projects/" + project + "/balances"), null, cancellationToken).ConfigureAwait(false);
        var balances = doc.RootElement.TryGetProperty("balances", out var b) && b.ValueKind == JsonValueKind.Array ? b : default;
        decimal total = 0;
        var units = "usd";
        var any = false;
        foreach (var item in balances.ValueKind == JsonValueKind.Array ? balances.EnumerateArray() : Enumerable.Empty<JsonElement>())
        {
            if (item.TryGetProperty("amount", out var a) && a.TryGetDecimal(out var amount) && amount is >= 0 and < 10_000_000m)
            {
                total += amount;
                units = item.TryGetProperty("units", out var u) && u.GetString() is { Length: 3 } code ? code : units;
                any = true;
            }
        }

        if (!any)
        {
            throw new SpeechToTextException("Deepgram didn't report a balance for this project.");
        }

        var balance = new DeepgramBalance(total, units.ToUpperInvariant(), clock.GetUtcNow());
        lock (CacheLock)
        {
            _cache = (hash, balance);
        }

        return balance;
    }

    /// <summary>
    /// Whether a key can read billing, which means it is an Admin or Owner key.
    /// </summary>
    /// <param name="http">HTTP client.</param>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> for an Admin or Owner key; <c>false</c> for a limited one.</returns>
    /// <exception cref="SpeechToTextException">Deepgram couldn't be asked, or the key isn't valid at all.</exception>
    public static async Task<bool> CanReadBillingAsync(HttpClient http, string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        var project = await ProjectAsync(http, key, cancellationToken).ConfigureAwait(false);
        try
        {
            using var _ = await SendAsync(http, key, HttpMethod.Get, new Uri(Api, "projects/" + project + "/balances"), null, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeechToTextException ex) when (ex.StatusCode is HttpStatusCode.Forbidden)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates a key that can only transcribe (<see cref="TranscriptionScope"/>), using an Admin key.
    /// </summary>
    /// <param name="http">HTTP client.</param>
    /// <param name="adminKey">An Admin or Owner key.</param>
    /// <param name="comment">The new key's comment in Deepgram's console.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new key.</returns>
    /// <exception cref="SpeechToTextException">It couldn't be created.</exception>
    public static async Task<string> CreateTranscriptionKeyAsync(HttpClient http, string adminKey, string comment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        var project = await ProjectAsync(http, adminKey, cancellationToken).ConfigureAwait(false);
        var body = JsonContent.Create(new { comment, scopes = TranscriptionScopes, tags = Tags });
        using var doc = await SendAsync(http, adminKey, HttpMethod.Post, new Uri(Api, "projects/" + project + "/keys"), body, cancellationToken).ConfigureAwait(false);
        return doc.RootElement.TryGetProperty("key", out var k) && k.GetString() is { } key && SpeechToTextKeys.IsWellFormed(key)
            ? key
            : throw new SpeechToTextException("Deepgram didn't return a usable key.");
    }

    /// <summary>
    /// Which key reads the balance once the transcription key has been swapped for a limited one: the kept Admin key if it
    /// was kept, otherwise nothing when the balance was read with the transcription key (a limited key can't read it).
    /// </summary>
    /// <param name="current">The setting before the swap.</param>
    /// <param name="keptForBalance">Whether the Admin key was kept, only to read the balance.</param>
    /// <returns>The setting after the swap.</returns>
    public static Configuration.BalanceSource BalanceAfterLimiting(Configuration.BalanceSource current, bool keptForBalance)
        => keptForBalance ? Configuration.BalanceSource.SeparateKey
            : current == Configuration.BalanceSource.TranscriptionKey ? Configuration.BalanceSource.Off
            : current;

    /// <summary>
    /// Forgets the cached balance (after a key changes).
    /// </summary>
    public static void ClearCache()
    {
        lock (CacheLock)
        {
            _cache = null;
        }
    }

    // The key's project: the first one it can see. The id is checked before it is put into a URL
    private static async Task<string> ProjectAsync(HttpClient http, string key, CancellationToken cancellationToken)
    {
        using var doc = await SendAsync(http, key, HttpMethod.Get, new Uri(Api, "projects"), null, cancellationToken).ConfigureAwait(false);
        var id = doc.RootElement.TryGetProperty("projects", out var p) && p.ValueKind == JsonValueKind.Array && p.GetArrayLength() > 0
            && p[0].TryGetProperty("project_id", out var pid) ? pid.GetString() : null;
        return id is not null && ProjectId().IsMatch(id) ? id : throw new SpeechToTextException("Deepgram didn't return a project for this key.");
    }

    private static async Task<JsonDocument> SendAsync(HttpClient http, string key, HttpMethod method, Uri address, HttpContent? body, CancellationToken cancellationToken)
    {
        if (address.Host != Api.Host || address.Scheme != Uri.UriSchemeHttps)
        {
            throw new SpeechToTextException("Refused: Deepgram keys are only sent to Deepgram.");
        }

        using var request = new HttpRequestMessage(method, address) { Content = body };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Token", key);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new SpeechToTextException("Deepgram couldn't be reached: " + Redaction.Redact(ex.Message, [key]), ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (text.Length > HttpSpeechToText.MaxReplyBytes)
            {
                throw new SpeechToTextException("Deepgram's reply was too large.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var why = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "Deepgram didn't accept this key.",
                    HttpStatusCode.Forbidden => "This key isn't allowed to do that (it needs an Admin or Owner key).",
                    _ => "Deepgram answered " + (int)response.StatusCode + ": " + Redaction.Redact(text, [key]),
                };
                throw new SpeechToTextException(why) { StatusCode = response.StatusCode };
            }

            try
            {
                return JsonDocument.Parse(text);
            }
            catch (JsonException ex)
            {
                throw new SpeechToTextException("Deepgram's reply wasn't valid JSON.", ex);
            }
        }
    }

    [GeneratedRegex("^[0-9a-fA-F-]{8,64}$")]
    private static partial Regex ProjectId();
}
