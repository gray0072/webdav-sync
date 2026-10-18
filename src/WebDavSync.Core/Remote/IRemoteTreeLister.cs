namespace WebDavSync.Core.Remote;

/// <summary>A remote store together with its connection: what the CLI and the tray create for a pair.</summary>
public interface IRemoteClient : IRemoteStore, IDisposable;

/// <summary>
/// Optional capability of a remote store (§4.3 #0): a whole subtree in one request, and a cheap token that changes
/// whenever anything below a folder changes. Mail.ru API (§4.7) provides both; plain WebDAV provides neither.
/// </summary>
public interface IRemoteTreeLister
{
    /// <summary>
    /// The subtree at <paramref name="path"/>. <c>null</c> when the tree listing is not available right now (the caller
    /// lists folder by folder); folders missing from <see cref="RemoteTree.Listings"/> are listed folder by folder too.
    /// </summary>
    Task<RemoteTree?> ListTreeAsync(string path, CancellationToken ct);

    /// <summary>The change token of <paramref name="path"/> (see <see cref="RemoteTree.ChangeToken"/>); <c>null</c> when unknown.</summary>
    Task<string?> GetChangeTokenAsync(string path, CancellationToken ct);
}

/// <param name="Listings">
/// Complete <c>Depth: 1</c> listings by absolute folder path (case-insensitive). Only folders whose listing is verifiably
/// complete are here; the others are left to per-folder listings (invariant #1).
/// </param>
/// <param name="ChangeToken">Changes whenever anything below the listed folder changes; <c>null</c> when unknown.</param>
/// <param name="Problems">Why some folders are not in <paramref name="Listings"/>.</param>
public sealed record RemoteTree(IReadOnlyDictionary<string, RemoteListing> Listings, string? ChangeToken, IReadOnlyList<string> Problems);
