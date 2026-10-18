using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WebDavSync.Core.Remote.MailRu;

/// <summary>The Mail.ru API failed (HTTP error, unexpected answer, login challenge); a listing falls back to WebDAV.</summary>
public class MailRuApiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The login was refused: wrong password or an app password without full access (§4.7). Retrying will not help.</summary>
public sealed class MailRuAuthException(string message) : MailRuApiException(message);

public sealed class MailRuApiOptions
{
    public required string User { get; init; }

    public required string Password { get; init; }

    public double MaxRequestsPerSecond { get; init; } = 5;

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(100);
}

/// <summary>
/// The parts of the Mail.ru Cloud API that WebDAV lacks (§4.7): a folder tree in one request and a folder's change
/// token. Login: OAuth password grant with an app password that has full access. Access tokens go into URLs, so URLs
/// are never logged.
/// </summary>
public sealed class MailRuApi : IDisposable
{
    public const string ClientId = "cloud-win";

    /// <summary>Deeper than any real tree; a cut is detected by the child counts anyway.</summary>
    private const ulong TreeDepth = 100_000;

    private static readonly Uri TokenUri = new("https://o2.mail.ru/token");
    private static readonly TimeSpan MetaServerLifetime = TimeSpan.FromMinutes(20);

    private readonly MailRuApiOptions _options;
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly RequestRateLimiter _limiter;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private string? _accessToken;
    private string? _refreshToken;
    private DateTime _tokenExpiresUtc;
    private string? _metaServer;
    private DateTime _metaServerExpiresUtc;

    public MailRuApi(MailRuApiOptions options, ILogger logger, HttpMessageHandler? handler = null)
    {
        _options = options;
        _logger = logger;
        _limiter = new RequestRateLimiter(options.MaxRequestsPerSecond);
        _http = new HttpClient(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WebDavSync/0.1");
    }

    /// <summary>The WebDAV endpoint of Mail.ru Cloud, whose account this API reaches.</summary>
    public static bool IsMailRuEndpoint(Uri endpoint) =>
        endpoint.Host.Equals("webdav.cloud.mail.ru", StringComparison.OrdinalIgnoreCase);

    /// <summary>The subtree at <paramref name="path"/> (opcode 117, §4.7); <c>null</c> when it does not exist.</summary>
    public async Task<RemoteTree?> ListTreeAsync(string path, bool hashKnown, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var data = await MetaAsync(MailRuTreeParser.Request(path, TreeDepth), ct).ConfigureAwait(false);
        var tree = MailRuTreeParser.Parse(data, path, hashKnown);
        _logger.LogInformation(
            "Mail.ru tree of {Path}: {Bytes} bytes, {Folders} complete folder listings, {Problems} left to WebDAV, in {Seconds:0.00} s",
            path, data.Length, tree?.Listings.Count ?? 0, tree?.Problems.Count ?? 0, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds);
        return tree;
    }

    /// <summary>The folder's subtree revision (<c>grev</c>), which changes with anything below it; <c>null</c> when it does not exist.</summary>
    public async Task<string?> GetFolderRevisionAsync(string path, CancellationToken ct)
    {
        var query = "home=" + Uri.EscapeDataString(path) + "&offset=0&limit=1";
        for (var attempt = 1; ; attempt++)
        {
            var token = await TokenAsync(force: attempt > 1, ct).ConfigureAwait(false);
            var uri = new Uri("https://cloud.mail.ru/api/m1/folder?" + query + "&access_token=" + Uri.EscapeDataString(token));
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, uri), "folder", ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized && attempt == 1)
            {
                continue;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new MailRuApiException($"Mail.ru folder info returned {(int)response.StatusCode}.");
            }

            try
            {
                using var json = JsonDocument.Parse(body);
                return json.RootElement.GetProperty("body").GetProperty("grev").GetInt64().ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                throw new MailRuApiException("Mail.ru folder info has an unexpected format.", ex);
            }
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _authLock.Dispose();
    }

    private async Task<byte[]> MetaAsync(byte[] body, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var token = await TokenAsync(force: attempt > 1, ct).ConfigureAwait(false);
            var server = await MetaServerAsync(token, ct).ConfigureAwait(false);
            var uri = new Uri(server + "?client_id=" + ClientId + "&token=" + Uri.EscapeDataString(token));
            using var response = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Post, uri)
                {
                    Content = new ByteArrayContent(body) { Headers = { ContentType = new("application/x-www-form-urlencoded") } },
                },
                "metaserver",
                ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized && attempt == 1)
            {
                _metaServer = null;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                _metaServer = null;
                throw new MailRuApiException($"Mail.ru metaserver returned {(int)response.StatusCode}.");
            }

            return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task<string> MetaServerAsync(string token, CancellationToken ct)
    {
        if (_metaServer is { } cached && DateTime.UtcNow < _metaServerExpiresUtc)
        {
            return cached;
        }

        var uri = new Uri("https://dispatcher.cloud.mail.ru/m?token=" + Uri.EscapeDataString(token));
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, uri), "dispatcher", ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var server = text.Split(' ', 2, StringSplitOptions.TrimEntries)[0];
        if (!response.IsSuccessStatusCode || !server.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new MailRuApiException($"Mail.ru dispatcher returned {(int)response.StatusCode} without a metaserver.");
        }

        _metaServer = server;
        _metaServerExpiresUtc = DateTime.UtcNow + MetaServerLifetime;
        return server;
    }

    private async Task<string> TokenAsync(bool force, CancellationToken ct)
    {
        await _authLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!force && _accessToken is { } token && DateTime.UtcNow < _tokenExpiresUtc)
            {
                return token;
            }

            if (_refreshToken is { } refresh)
            {
                try
                {
                    return await RequestTokenAsync([new("grant_type", "refresh_token"), new("refresh_token", refresh)], ct).ConfigureAwait(false);
                }
                catch (MailRuApiException ex)
                {
                    _logger.LogInformation("Mail.ru token refresh failed ({Message}); logging in again", ex.Message);
                    _refreshToken = null;
                }
            }

            return await RequestTokenAsync(
                [new("grant_type", "password"), new("username", _options.User), new("password", _options.Password)], ct).ConfigureAwait(false);
        }
        finally
        {
            _authLock.Release();
        }
    }

    private async Task<string> RequestTokenAsync(KeyValuePair<string, string>[] grant, CancellationToken ct)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, TokenUri) { Content = new FormUrlEncodedContent([new("client_id", ClientId), .. grant]) },
            "login",
            ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonElement root;
        try
        {
            using var json = JsonDocument.Parse(body);
            root = json.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new MailRuApiException($"Mail.ru login returned {(int)response.StatusCode} without JSON.", ex);
        }

        if (root.TryGetProperty("access_token", out var access) && access.GetString() is { Length: > 0 } accessToken)
        {
            var lifetime = root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds) ? seconds : 3600;
            _accessToken = accessToken;
            _tokenExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, lifetime - 300));
            if (root.TryGetProperty("refresh_token", out var refresh) && refresh.GetString() is { Length: > 0 } refreshToken)
            {
                _refreshToken = refreshToken;
            }

            return accessToken;
        }

        var code = root.TryGetProperty("error_code", out var c) && c.TryGetInt32(out var n) ? n : 0;
        var description = root.TryGetProperty("error_description", out var d) ? d.GetString() : null;
        if (code == 3 && grant[0].Value == "password")
        {
            throw new MailRuAuthException(
                "Mail.ru API login refused: the app password needs full access (\"Mail, Cloud and Calendar\"); a WebDAV-only one works for WebDAV only.");
        }

        throw new MailRuApiException($"Mail.ru login failed (error {code}{(description is null ? string.Empty : ": " + description)}).");
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> create, string what, CancellationToken ct)
    {
        await _limiter.WaitAsync(ct).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.RequestTimeout);
        using var request = create();
        try
        {
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            return response;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new MailRuApiException($"Mail.ru {what} request timed out.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new MailRuApiException($"Mail.ru {what} request failed: {ex.Message}", ex);
        }
    }
}
