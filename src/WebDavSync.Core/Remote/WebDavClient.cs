using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using WebDavSync.Core.Model;

namespace WebDavSync.Core.Remote;

public sealed class WebDavOptions
{
    public required Uri Endpoint { get; init; }

    public required string User { get; init; }

    public required string Password { get; init; }

    public double MaxRequestsPerSecond { get; init; } = 5;

    /// <summary>Timeout of metadata requests (PROPFIND, MKCOL, MOVE, DELETE).</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(100);

    /// <summary>Attempts for idempotent metadata requests on transient errors (§4.5).</summary>
    public int MaxAttempts { get; init; } = 4;

    /// <summary>Optional server property carrying the content hash of §3.3 (set from the probe).</summary>
    public XName? HashProperty { get; init; }
}

/// <summary>Thin WebDAV client over <see cref="HttpClient"/> (§2.2).</summary>
public sealed class WebDavClient : IRemoteStore, IDisposable
{
    internal const string ListBody =
        """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/><d:getcontentlength/><d:getlastmodified/><d:getetag/><d:getcontenttype/></d:prop></d:propfind>""";

    internal const string AllPropBody =
        """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:"><d:allprop/></d:propfind>""";

    private const string QuotaBody =
        """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:"><d:prop><d:quota-available-bytes/><d:quota-used-bytes/></d:prop></d:propfind>""";

    private static readonly HttpMethod Propfind = new("PROPFIND");
    private static readonly HttpMethod Proppatch = new("PROPPATCH");
    private static readonly HttpMethod Mkcol = new("MKCOL");
    private static readonly HttpMethod Move = new("MOVE");

    private readonly WebDavOptions _options;
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly RequestRateLimiter _limiter;
    private readonly string _basePath;

    public WebDavClient(WebDavOptions options, ILogger logger, HttpMessageHandler? handler = null)
    {
        _options = options;
        _logger = logger;
        _limiter = new RequestRateLimiter(options.MaxRequestsPerSecond);
        _http = new HttpClient(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(options.User + ":" + options.Password)));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WebDavSync/0.1");
        _basePath = Uri.UnescapeDataString(options.Endpoint.AbsolutePath).TrimEnd('/');
    }

    public Uri ToUri(string path, bool trailingSlash = false)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString);
        var relative = string.Join('/', segments);
        if (trailingSlash && relative.Length > 0)
        {
            relative += "/";
        }

        var baseUri = _options.Endpoint.AbsoluteUri.EndsWith('/') ? _options.Endpoint : new Uri(_options.Endpoint.AbsoluteUri + "/");
        return new Uri(baseUri, relative);
    }

    /// <summary>Converts an <c>href</c> to a decoded absolute remote path relative to the endpoint.</summary>
    public string FromHref(string href)
    {
        var path = href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? new Uri(href).AbsolutePath
            : href;
        path = Uri.UnescapeDataString(path);
        if (_basePath.Length > 0 && path.StartsWith(_basePath, StringComparison.OrdinalIgnoreCase))
        {
            path = path[_basePath.Length..];
        }

        path = "/" + path.Trim('/');
        return path;
    }

    public async Task<RemoteListing> ListAsync(string path, CancellationToken ct)
    {
        var (status, responses) = await PropfindAsync(path, "1", ListBody, trailingSlash: true, ct).ConfigureAwait(false);
        if (status == HttpStatusCode.NotFound)
        {
            throw new RemoteException(RemoteErrorKind.NotFound, $"Remote folder '{path}' not found.", status);
        }

        var requested = Normalize(path);
        RemoteItem? self = null;
        var children = new List<RemoteItem>();
        var problems = new List<string>();
        var complete = true;
        foreach (var response in responses)
        {
            var decoded = FromHref(response.Href);
            var normalized = Normalize(decoded);
            var isSelf = string.Equals(normalized, requested, StringComparison.OrdinalIgnoreCase);
            if (!isSelf && !string.Equals(Normalize(PathKey.Parent(normalized.TrimStart('/'))), requested, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"Unexpected href '{response.Href}' in listing of '{path}'.");
                complete = false;
                continue;
            }

            if (!response.IsOk)
            {
                problems.Add($"Incomplete properties for '{decoded}' (status {string.Join(',', response.PropStatuses)}).");
                complete = false;
                continue;
            }

            var item = PropfindParser.ToItem(response, decoded, _options.HashProperty);
            if (isSelf)
            {
                self = item;
            }
            else
            {
                children.Add(item);
            }
        }

        if (self is null)
        {
            problems.Add($"Listing of '{path}' does not contain the folder itself.");
            complete = false;
            self = new RemoteItem(path, true, 0, null, null, null, null);
        }
        else if (!self.IsDir)
        {
            throw new RemoteException(RemoteErrorKind.Conflict, $"Remote '{path}' is a file, not a folder.");
        }

        return new RemoteListing(self, children, complete, problems);
    }

    public async Task<RemoteItem?> StatAsync(string path, CancellationToken ct)
    {
        var (status, responses) = await PropfindAsync(path, "0", ListBody, trailingSlash: false, ct).ConfigureAwait(false);
        if (status == HttpStatusCode.NotFound)
        {
            return null;
        }

        var response = responses.FirstOrDefault(r => r.IsOk) ??
                       throw new RemoteException(RemoteErrorKind.Transient, $"No usable PROPFIND response for '{path}'.");
        return PropfindParser.ToItem(response, FromHref(response.Href), _options.HashProperty);
    }

    public async Task<RemoteDownload> GetAsync(string path, long offset, CancellationToken ct)
    {
        var response = await SendAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, ToUri(path));
                if (offset > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(offset, null);
                }

                return request;
            },
            retry: true,
            timeout: null,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, path, ct).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return new RemoteDownload(stream, response.Content.Headers.ContentLength, PropfindParser.NormalizeETag(response.Headers.ETag?.Tag), response);
    }

    public Task PutAsync(string path, Stream content, long length, DateTime? mtimeUtc, CancellationToken ct) =>
        PutAsync(path, content, length, mtimeUtc is null ? null : [("X-OC-Mtime", ToUnix(mtimeUtc.Value))], ct);

    public async Task PutAsync(string path, Stream content, long length, IEnumerable<(string Name, string Value)>? headers, CancellationToken ct)
    {
        using var response = await SendAsync(
            () =>
            {
                var body = new StreamContent(content, 81920);
                body.Headers.ContentLength = length;
                body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                var request = new HttpRequestMessage(HttpMethod.Put, ToUri(path)) { Content = body };
                foreach (var (name, value) in headers ?? [])
                {
                    request.Headers.TryAddWithoutValidation(name, value);
                }

                return request;
            },
            retry: false,
            timeout: null,
            HttpCompletionOption.ResponseContentRead,
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, path, ct).ConfigureAwait(false);
    }

    public async Task MkdirAsync(string path, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(Mkcol, ToUri(path, trailingSlash: true)), retry: true, _options.RequestTimeout, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, path, ct).ConfigureAwait(false);
    }

    public async Task MoveAsync(string from, string to, bool overwrite, CancellationToken ct)
    {
        using var response = await SendAsync(
            () =>
            {
                var request = new HttpRequestMessage(Move, ToUri(from));
                request.Headers.TryAddWithoutValidation("Destination", ToUri(to).AbsoluteUri);
                request.Headers.TryAddWithoutValidation("Overwrite", overwrite ? "T" : "F");
                return request;
            },
            retry: false,
            _options.RequestTimeout,
            HttpCompletionOption.ResponseContentRead,
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, from, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string path, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, ToUri(path)), retry: false, _options.RequestTimeout, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, path, ct).ConfigureAwait(false);
    }

    public async Task<RemoteQuota?> GetQuotaAsync(CancellationToken ct)
    {
        var (status, responses) = await PropfindAsync("/", "0", QuotaBody, trailingSlash: true, ct).ConfigureAwait(false);
        if (status == HttpStatusCode.NotFound || responses.Count == 0)
        {
            return null;
        }

        var r = responses[0];
        long? Read(string name) =>
            long.TryParse(r.Text(PropfindParser.Dav + name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        var used = Read("quota-used-bytes");
        var available = Read("quota-available-bytes");
        return used is null && available is null ? null : new RemoteQuota(used, available);
    }

    /// <summary>Raw PROPFIND. Returns 404 as a status instead of throwing.</summary>
    public async Task<(HttpStatusCode Status, IReadOnlyList<PropfindParser.Response> Responses)> PropfindAsync(
        string path, string depth, string body, bool trailingSlash, CancellationToken ct)
    {
        using var response = await SendAsync(
            () =>
            {
                var request = new HttpRequestMessage(Propfind, ToUri(path, trailingSlash))
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/xml"),
                };
                request.Headers.TryAddWithoutValidation("Depth", depth);
                return request;
            },
            retry: true,
            _options.RequestTimeout,
            HttpCompletionOption.ResponseContentRead,
            ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return (response.StatusCode, []);
        }

        await EnsureSuccessAsync(response, path, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.MultiStatus)
        {
            throw new RemoteException(RemoteErrorKind.Offline, $"PROPFIND '{path}' returned {(int)response.StatusCode} instead of 207.", response.StatusCode);
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (!mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase))
        {
            throw new RemoteException(RemoteErrorKind.Offline, $"PROPFIND '{path}' returned '{mediaType}' instead of XML (captive portal?).", response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return (response.StatusCode, PropfindParser.Parse(stream));
    }

    /// <summary>Raw PROPPATCH (used by the probe). Returns the HTTP status and the per-property statuses.</summary>
    public async Task<(HttpStatusCode Status, IReadOnlyList<PropfindParser.Response> Responses)> ProppatchAsync(string path, string body, CancellationToken ct)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(Proppatch, ToUri(path)) { Content = new StringContent(body, Encoding.UTF8, "application/xml") },
            retry: false,
            _options.RequestTimeout,
            HttpCompletionOption.ResponseContentRead,
            ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.MultiStatus)
        {
            return (response.StatusCode, []);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return (response.StatusCode, PropfindParser.Parse(stream));
    }

    public void Dispose() => _http.Dispose();

    private static string Normalize(string path) => "/" + path.Normalize(NormalizationForm.FormC).Trim('/');

    private static string ToUnix(DateTime utc) =>
        ((long)(utc.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds).ToString(CultureInfo.InvariantCulture);

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string path, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var kind = RemoteException.Classify(response.StatusCode);
        string detail;
        try
        {
            detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            detail = detail.Length > 200 ? detail[..200] : detail;
        }
        catch (HttpRequestException)
        {
            detail = string.Empty;
        }

        throw new RemoteException(
            kind,
            $"{response.RequestMessage?.Method} '{path}' failed: {(int)response.StatusCode} {response.ReasonPhrase}. {detail}".Trim(),
            response.StatusCode,
            GetRetryAfter(response));
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta;
        }

        return retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null;
    }

    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> createRequest,
        bool retry,
        TimeSpan? timeout,
        HttpCompletionOption completion,
        CancellationToken ct)
    {
        var attempts = retry ? Math.Max(1, _options.MaxAttempts) : 1;
        for (var attempt = 1; ; attempt++)
        {
            await _limiter.WaitAsync(ct).ConfigureAwait(false);
            using var request = createRequest();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeout is { } t)
            {
                timeoutCts.CancelAfter(t);
            }

            var started = Stopwatch.GetTimestamp();
            HttpResponseMessage? response = null;
            RemoteException? failure;
            try
            {
                response = await _http.SendAsync(request, completion, timeoutCts.Token).ConfigureAwait(false);
                _logger.LogDebug("{Method} {Uri} -> {Status} in {Ms} ms", request.Method, request.RequestUri, (int)response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                var kind = response.IsSuccessStatusCode ? (RemoteErrorKind?)null : RemoteException.Classify(response.StatusCode);
                if (kind != RemoteErrorKind.Transient || attempt >= attempts)
                {
                    return response;
                }

                failure = new RemoteException(kind.Value, $"{request.Method} {request.RequestUri} -> {(int)response.StatusCode}", response.StatusCode, GetRetryAfter(response));
                response.Dispose();
            }
            catch (HttpRequestException ex)
            {
                response?.Dispose();
                failure = new RemoteException(RemoteErrorKind.Offline, $"{request.Method} {request.RequestUri} failed: {ex.Message}", inner: ex);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                response?.Dispose();
                failure = new RemoteException(RemoteErrorKind.Transient, $"{request.Method} {request.RequestUri} timed out.", inner: ex);
            }

            if (attempt >= attempts)
            {
                throw failure;
            }

            var delay = Backoff.Delay(attempt, failure.RetryAfter);
            _logger.LogWarning("{Message}; retry {Attempt}/{Attempts} in {Delay}", failure.Message, attempt + 1, attempts, delay);
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>Exponential backoff with jitter, honoring <c>Retry-After</c> (§6.6).</summary>
public static class Backoff
{
    public static TimeSpan Max { get; set; } = TimeSpan.FromMinutes(5);

    public static TimeSpan Delay(int attempt, TimeSpan? retryAfter = null)
    {
        if (retryAfter is { } ra && ra > TimeSpan.Zero)
        {
            return ra < Max ? ra : Max;
        }

        var seconds = Math.Min(Max.TotalSeconds, Math.Pow(4, attempt - 1));
        return TimeSpan.FromSeconds(seconds * (0.8 + Random.Shared.NextDouble() * 0.4));
    }
}

/// <summary>Global request rate limit (<c>MaxRequestsPerSecond</c>, §11), monotonic clock.</summary>
internal sealed class RequestRateLimiter(double perSecond)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly long _interval = perSecond > 0 ? (long)(Stopwatch.Frequency / perSecond) : 0;
    private long _next;

    public async Task WaitAsync(CancellationToken ct)
    {
        if (_interval == 0)
        {
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = Stopwatch.GetTimestamp();
            if (_next > now)
            {
                await Task.Delay(TimeSpan.FromSeconds((double)(_next - now) / Stopwatch.Frequency), ct).ConfigureAwait(false);
                now = Stopwatch.GetTimestamp();
            }

            _next = Math.Max(now, _next) + _interval;
        }
        finally
        {
            _gate.Release();
        }
    }
}
