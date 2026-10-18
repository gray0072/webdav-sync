using Microsoft.Extensions.Logging;

namespace WebDavSync.Core.Remote.MailRu;

/// <summary>
/// Mail.ru Cloud (§4.7): every operation goes through WebDAV as before; the Mail.ru API only adds the tree listing and
/// the change token. When the API fails, the tree is "not available" and the scanner lists folder by folder over
/// WebDAV, so a broken or changed API costs speed, never correctness.
/// </summary>
public sealed class MailRuStore(WebDavClient webDav, MailRuApi api, bool hashKnown, ILogger logger) : IRemoteClient, IRemoteTreeLister
{
    /// <summary>After a failure other than a refused login, the API is tried again after this pause.</summary>
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(10);

    private readonly Lock _lock = new();
    private bool _loginRefused;
    private DateTime _retryAtUtc;

    /// <summary>Why the API was not used last time; <c>null</c> after a success.</summary>
    public string? ApiProblem { get; private set; }

    public WebDavClient WebDav => webDav;

    public Task<RemoteListing> ListAsync(string path, CancellationToken ct) => webDav.ListAsync(path, ct);

    public Task<RemoteItem?> StatAsync(string path, CancellationToken ct) => webDav.StatAsync(path, ct);

    public Task<RemoteDownload> GetAsync(string path, long offset, CancellationToken ct) => webDav.GetAsync(path, offset, ct);

    public Task PutAsync(string path, Stream content, long length, DateTime? mtimeUtc, CancellationToken ct) =>
        webDav.PutAsync(path, content, length, mtimeUtc, ct);

    public Task MkdirAsync(string path, CancellationToken ct) => webDav.MkdirAsync(path, ct);

    public Task MoveAsync(string from, string to, bool overwrite, CancellationToken ct) => webDav.MoveAsync(from, to, overwrite, ct);

    public Task DeleteAsync(string path, CancellationToken ct) => webDav.DeleteAsync(path, ct);

    public Task<RemoteQuota?> GetQuotaAsync(CancellationToken ct) => webDav.GetQuotaAsync(ct);

    public Task<RemoteTree?> ListTreeAsync(string path, CancellationToken ct) =>
        UseApiAsync(c => api.ListTreeAsync(path, hashKnown, c), "tree listing", ct);

    public Task<string?> GetChangeTokenAsync(string path, CancellationToken ct) =>
        UseApiAsync(c => api.GetFolderRevisionAsync(path, c), "change check", ct);

    public void Dispose()
    {
        api.Dispose();
        webDav.Dispose();
    }

    private async Task<T?> UseApiAsync<T>(Func<CancellationToken, Task<T?>> call, string what, CancellationToken ct)
        where T : class
    {
        lock (_lock)
        {
            if (_loginRefused || DateTime.UtcNow < _retryAtUtc)
            {
                return null;
            }
        }

        try
        {
            var result = await call(ct).ConfigureAwait(false);
            ApiProblem = null;
            return result;
        }
        catch (MailRuAuthException ex)
        {
            // Retrying a refused login every cycle could lock the account; a new password restarts the store.
            lock (_lock)
            {
                _loginRefused = true;
            }

            ApiProblem = ex.Message;
            logger.LogWarning("{Message} Using WebDAV only.", ex.Message);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Anything else, a bug included: WebDAV is always a safe way to list.
            lock (_lock)
            {
                _retryAtUtc = DateTime.UtcNow + RetryAfterFailure;
            }

            ApiProblem = ex.Message;
            logger.LogWarning(ex, "Mail.ru API {What} failed; using WebDAV, next try in {Minutes} min", what, RetryAfterFailure.TotalMinutes);
            return null;
        }
    }
}
