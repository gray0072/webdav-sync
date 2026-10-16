using System.Net;
using WebDavSync.Core.Model;
using WebDavSync.Core.Remote;

namespace WebDavSync.Core.Tests.Support;

/// <summary>In-memory <see cref="IRemoteStore"/> (§19.4): case-insensitive like the cloud, second-precision mtimes.</summary>
internal sealed class InMemoryRemoteStore : IRemoteStore
{
    private readonly Dictionary<string, Item> _items = new(StringComparer.OrdinalIgnoreCase);
    private int _etag;

    public InMemoryRemoteStore() => _items["/"] = new Item("/", true, [], Now(), NextETag());

    /// <summary>Fault injection: return an exception to throw for a listing of the given path.</summary>
    public Func<string, Exception?>? ListFault { get; set; }

    public int Requests { get; private set; }

    public void AddDir(string path)
    {
        path = Norm(path);
        if (path != "/" && !_items.ContainsKey(PathKey.Parent(path.TrimStart('/')) is { Length: > 0 } p ? "/" + p : "/"))
        {
            AddDir("/" + PathKey.Parent(path.TrimStart('/')));
        }

        _items.TryAdd(path, new Item(path, true, [], Now(), NextETag()));
    }

    public void AddFile(string path, string content, DateTime? mtime = null)
    {
        path = Norm(path);
        AddDir("/" + PathKey.Parent(path.TrimStart('/')));
        _items[path] = new Item(path, false, System.Text.Encoding.UTF8.GetBytes(content), Trunc(mtime ?? Now()), NextETag());
    }

    public string? Read(string path) =>
        _items.TryGetValue(Norm(path), out var item) && !item.IsDir ? System.Text.Encoding.UTF8.GetString(item.Content) : null;

    public bool Exists(string path) => _items.ContainsKey(Norm(path));

    /// <summary>All files under a root as relative path → content.</summary>
    public Dictionary<string, string> Files(string root)
    {
        root = Norm(root);
        return _items.Values
            .Where(i => !i.IsDir && i.Path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase) && !i.Path.Contains("/.webdavsync/", StringComparison.Ordinal))
            .ToDictionary(i => i.Path[(root.Length + 1)..], i => System.Text.Encoding.UTF8.GetString(i.Content), StringComparer.Ordinal);
    }

    public Task<RemoteListing> ListAsync(string path, CancellationToken ct)
    {
        Requests++;
        path = Norm(path);
        if (ListFault?.Invoke(path) is { } fault)
        {
            throw fault;
        }

        if (!_items.TryGetValue(path, out var self))
        {
            throw new RemoteException(RemoteErrorKind.NotFound, $"{path} not found", HttpStatusCode.NotFound);
        }

        var children = _items.Values
            .Where(i => i.Path != "/" && string.Equals(ParentOf(i.Path), path, StringComparison.OrdinalIgnoreCase))
            .Select(ToItem)
            .ToList();
        return Task.FromResult(new RemoteListing(ToItem(self), children, true, []));
    }

    public Task<RemoteItem?> StatAsync(string path, CancellationToken ct)
    {
        Requests++;
        return Task.FromResult(_items.TryGetValue(Norm(path), out var item) ? ToItem(item) : null);
    }

    public Task<RemoteDownload> GetAsync(string path, long offset, CancellationToken ct)
    {
        Requests++;
        if (!_items.TryGetValue(Norm(path), out var item) || item.IsDir)
        {
            throw new RemoteException(RemoteErrorKind.NotFound, $"{path} not found", HttpStatusCode.NotFound);
        }

        return Task.FromResult(new RemoteDownload(new MemoryStream(item.Content[(int)offset..]), item.Content.Length - offset, item.ETag, null));
    }

    public async Task PutAsync(string path, Stream content, long length, DateTime? mtimeUtc, CancellationToken ct)
    {
        Requests++;
        path = Norm(path);
        RequireParent(path);
        if (_items.TryGetValue(path, out var existing) && existing.IsDir)
        {
            throw new RemoteException(RemoteErrorKind.Conflict, "is a folder", HttpStatusCode.Conflict);
        }

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        if (buffer.Length != length)
        {
            throw new RemoteException(RemoteErrorKind.Permanent, "length mismatch", HttpStatusCode.BadRequest);
        }

        var name = existing?.Path ?? path;
        _items[path] = new Item(name, false, buffer.ToArray(), Trunc(Now()), NextETag());
    }

    public Task MkdirAsync(string path, CancellationToken ct)
    {
        Requests++;
        path = Norm(path);
        if (_items.ContainsKey(path))
        {
            throw new RemoteException(RemoteErrorKind.Conflict, "exists", HttpStatusCode.MethodNotAllowed);
        }

        RequireParent(path);
        _items[path] = new Item(path, true, [], Now(), NextETag());
        return Task.CompletedTask;
    }

    public Task MoveAsync(string from, string to, bool overwrite, CancellationToken ct)
    {
        Requests++;
        from = Norm(from);
        to = Norm(to);
        if (!_items.ContainsKey(from))
        {
            throw new RemoteException(RemoteErrorKind.NotFound, $"{from} not found", HttpStatusCode.NotFound);
        }

        if (_items.ContainsKey(to) && !overwrite && !string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            throw new RemoteException(RemoteErrorKind.Conflict, "target exists", HttpStatusCode.PreconditionFailed);
        }

        RequireParent(to);
        var moved = _items.Values.Where(i => i.Path.Equals(from, StringComparison.OrdinalIgnoreCase) || i.Path.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var item in moved)
        {
            _items.Remove(item.Path);
        }

        foreach (var item in moved)
        {
            var newPath = to + item.Path[from.Length..];
            _items[newPath] = item with { Path = newPath };
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path, CancellationToken ct)
    {
        Requests++;
        path = Norm(path);
        if (!_items.ContainsKey(path))
        {
            throw new RemoteException(RemoteErrorKind.NotFound, $"{path} not found", HttpStatusCode.NotFound);
        }

        foreach (var item in _items.Values.Where(i => i.Path.Equals(path, StringComparison.OrdinalIgnoreCase) || i.Path.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _items.Remove(item.Path);
        }

        return Task.CompletedTask;
    }

    public Task<RemoteQuota?> GetQuotaAsync(CancellationToken ct) => Task.FromResult<RemoteQuota?>(new RemoteQuota(0, 10L * 1024 * 1024 * 1024));

    private static string Norm(string path) => "/" + path.Trim('/');

    private static string ParentOf(string path)
    {
        var parent = PathKey.Parent(path.TrimStart('/'));
        return "/" + parent;
    }

    private static DateTime Now() => Trunc(DateTime.UtcNow);

    private static DateTime Trunc(DateTime t) => new(t.Ticks - (t.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);

    private static RemoteItem ToItem(Item item) =>
        new(item.Path, item.IsDir, item.Content.Length, item.Mtime, item.ETag, null, item.IsDir ? null : "application/octet-stream");

    private void RequireParent(string path)
    {
        if (!_items.TryGetValue(ParentOf(path), out var parent) || !parent.IsDir)
        {
            throw new RemoteException(RemoteErrorKind.Conflict, $"parent of {path} missing", HttpStatusCode.Conflict);
        }
    }

    private string NextETag() => "e" + (++_etag).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private sealed record Item(string Path, bool IsDir, byte[] Content, DateTime Mtime, string ETag);
}
