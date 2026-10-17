using System.Net;
using WebDavSync.Core.Model;

namespace WebDavSync.Core.Remote;

/// <summary>Error classes of the HTTP error taxonomy (§4.5).</summary>
public enum RemoteErrorKind
{
    /// <summary>408, 423, 429, 5xx (except 507), timeouts, malformed XML: retry with backoff.</summary>
    Transient,

    /// <summary>Other 4xx: no retry, goes to the error list.</summary>
    Permanent,

    /// <summary>401: stop the pair, ask for a new password.</summary>
    Auth,

    /// <summary>507: stop uploads, keep downloads.</summary>
    Quota,

    /// <summary>No HTTP response (DNS, connection) or a non-WebDAV answer (captive portal): stop the cycle.</summary>
    Offline,

    /// <summary>404.</summary>
    NotFound,

    /// <summary>405 on MKCOL, 409, 412: the target state differs from what was expected.</summary>
    Conflict,
}

public sealed class RemoteException(
    RemoteErrorKind kind,
    string message,
    HttpStatusCode? status = null,
    TimeSpan? retryAfter = null,
    Exception? inner = null) : Exception(message, inner)
{
    public RemoteErrorKind Kind { get; } = kind;

    public HttpStatusCode? Status { get; } = status;

    public TimeSpan? RetryAfter { get; } = retryAfter;

    public bool IsRetryable => Kind is RemoteErrorKind.Transient;

    /// <summary>True when the whole cycle must stop (no point in trying further operations).</summary>
    public bool StopsCycle => Kind is RemoteErrorKind.Auth or RemoteErrorKind.Offline;

    public static RemoteErrorKind Classify(HttpStatusCode status) => (int)status switch
    {
        401 => RemoteErrorKind.Auth,
        404 or 410 => RemoteErrorKind.NotFound,
        405 or 409 or 412 => RemoteErrorKind.Conflict,
        507 => RemoteErrorKind.Quota,
        408 or 423 or 429 => RemoteErrorKind.Transient,
        >= 500 => RemoteErrorKind.Transient,
        _ => RemoteErrorKind.Permanent,
    };
}

/// <summary>One PROPFIND response. <see cref="Path"/> is the decoded absolute remote path.</summary>
public sealed record RemoteItem(
    string Path,
    bool IsDir,
    long Size,
    DateTime? MtimeUtc,
    string? ETag,
    ContentHash? Hash,
    string? ContentType)
{
    public string Name => PathKey.Name(Path.TrimEnd('/'));

    public RemoteEntry ToEntry(string relativePath) =>
        new(PathKey.FromPath(relativePath), relativePath, IsDir, IsDir ? 0 : Size, MtimeUtc, ETag, Hash);
}

/// <summary>A Depth: 1 listing. <see cref="Complete"/> is the "complete listing" definition of §6.8.</summary>
public sealed record RemoteListing(RemoteItem Self, IReadOnlyList<RemoteItem> Children, bool Complete, IReadOnlyList<string> Problems);

/// <param name="offset">Where <paramref name="content"/> starts: the requested offset, or 0 when the server ignored the range.</param>
public sealed class RemoteDownload(Stream content, long? length, string? etag, IDisposable? owner, long offset = 0) : IAsyncDisposable
{
    public Stream Content { get; } = content;

    public long Offset { get; } = offset;

    public long? Length { get; } = length;

    public string? ETag { get; } = etag;

    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync().ConfigureAwait(false);
        owner?.Dispose();
    }
}

public sealed record RemoteQuota(long? UsedBytes, long? AvailableBytes);

/// <summary>The only remote abstraction (§2.3, §4.1). Paths are absolute, starting with <c>/</c>.</summary>
public interface IRemoteStore
{
    Task<RemoteListing> ListAsync(string path, CancellationToken ct);

    /// <summary>Depth: 0. Returns null when the item does not exist.</summary>
    Task<RemoteItem?> StatAsync(string path, CancellationToken ct);

    /// <summary>GET from <paramref name="offset"/> (a <c>Range</c> request); check <see cref="RemoteDownload.Offset"/>.</summary>
    Task<RemoteDownload> GetAsync(string path, long offset, CancellationToken ct);

    Task PutAsync(string path, Stream content, long length, DateTime? mtimeUtc, CancellationToken ct);

    Task MkdirAsync(string path, CancellationToken ct);

    Task MoveAsync(string from, string to, bool overwrite, CancellationToken ct);

    Task DeleteAsync(string path, CancellationToken ct);

    Task<RemoteQuota?> GetQuotaAsync(CancellationToken ct);
}
