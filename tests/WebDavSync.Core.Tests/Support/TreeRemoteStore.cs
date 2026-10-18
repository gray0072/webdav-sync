using WebDavSync.Core.Remote;

namespace WebDavSync.Core.Tests.Support;

/// <summary>Adds a tree listing and a change token (§4.3 #0) to <see cref="InMemoryRemoteStore"/>, like the Mail.ru API does.</summary>
internal sealed class TreeRemoteStore(InMemoryRemoteStore inner) : IRemoteStore, IRemoteTreeLister
{
    private int _folderListings;
    private int _tokenChecks;
    private volatile string _token = "1";

    /// <summary>False: the tree listing and the token are "not available right now".</summary>
    public volatile bool Available = true;

    /// <summary>Folders left out of the tree, as the parser leaves out incomplete ones.</summary>
    public HashSet<string> Omit { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string Token
    {
        get => _token;
        set => _token = value;
    }

    /// <summary>Per-folder listings asked by the scanner.</summary>
    public int FolderListings => Volatile.Read(ref _folderListings);

    public int TokenChecks => Volatile.Read(ref _tokenChecks);

    public async Task<RemoteTree?> ListTreeAsync(string path, CancellationToken ct)
    {
        if (!Available)
        {
            return null;
        }

        var listings = new Dictionary<string, RemoteListing>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>([path]);
        while (queue.TryDequeue(out var folder))
        {
            RemoteListing listing;
            try
            {
                listing = await inner.ListAsync(folder, ct);
            }
            catch (RemoteException ex) when (ex.Kind == RemoteErrorKind.NotFound)
            {
                return null;
            }

            if (!Omit.Contains(folder))
            {
                listings[folder] = listing;
            }

            foreach (var child in listing.Children.Where(c => c.IsDir))
            {
                queue.Enqueue(child.Path);
            }
        }

        return new RemoteTree(listings, Token, []);
    }

    public Task<string?> GetChangeTokenAsync(string path, CancellationToken ct)
    {
        Interlocked.Increment(ref _tokenChecks);
        return Task.FromResult(Available ? Token : null);
    }

    public Task<RemoteListing> ListAsync(string path, CancellationToken ct)
    {
        Interlocked.Increment(ref _folderListings);
        return inner.ListAsync(path, ct);
    }

    public Task<RemoteItem?> StatAsync(string path, CancellationToken ct) => inner.StatAsync(path, ct);

    public Task<RemoteDownload> GetAsync(string path, long offset, CancellationToken ct) => inner.GetAsync(path, offset, ct);

    public Task PutAsync(string path, Stream content, long length, DateTime? mtimeUtc, CancellationToken ct) =>
        inner.PutAsync(path, content, length, mtimeUtc, ct);

    public Task MkdirAsync(string path, CancellationToken ct) => inner.MkdirAsync(path, ct);

    public Task MoveAsync(string from, string to, bool overwrite, CancellationToken ct) => inner.MoveAsync(from, to, overwrite, ct);

    public Task DeleteAsync(string path, CancellationToken ct) => inner.DeleteAsync(path, ct);

    public Task<RemoteQuota?> GetQuotaAsync(CancellationToken ct) => inner.GetQuotaAsync(ct);
}
