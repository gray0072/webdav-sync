using Microsoft.Extensions.Logging;
using WebDavSync.Core.Config;
using WebDavSync.Core.Model;

namespace WebDavSync.Core.Remote;

/// <summary>
/// Full breadth-first <c>Depth: 1</c> crawl (§4.3). A folder is added to
/// <see cref="Snapshot{TEntry}.ListedFolders"/> only when its listing is complete (§6.8).
/// </summary>
public sealed class RemoteScanner(IRemoteStore store, SyncFilter filter, ILogger logger)
{
    /// <param name="remoteRoot">Absolute remote root of the pair.</param>
    /// <param name="subtree">Relative folder to start from (<c>""</c> = whole pair).</param>
    public async Task<RemoteSnapshot> CrawlAsync(string remoteRoot, string subtree = "", IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var entries = new Dictionary<string, RemoteEntry>(StringComparer.Ordinal);
        var listed = new HashSet<string>(StringComparer.Ordinal);
        var skipped = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new List<string>();
        var queue = new Queue<string>();
        queue.Enqueue(subtree);
        var requests = 0;

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var relDir = queue.Dequeue();
            RemoteListing listing;
            try
            {
                listing = await store.ListAsync(PathKey.ToRemoteFull(remoteRoot, relDir), ct).ConfigureAwait(false);
            }
            catch (RemoteException ex) when (ex.Kind == RemoteErrorKind.NotFound && relDir != subtree)
            {
                // Vanished mid-crawl: the parent listing is stale, so it can no longer prove deletions.
                listed.Remove(PathKey.FromPath(PathKey.Parent(relDir)));
                warnings.Add($"Remote folder '{relDir}' disappeared during the crawl.");
                continue;
            }
            catch (RemoteException ex) when (!ex.StopsCycle && ex.Kind != RemoteErrorKind.NotFound)
            {
                warnings.Add($"Cannot list remote folder '{relDir}': {ex.Message}");
                logger.LogWarning(ex, "Cannot list remote folder {Folder}", relDir);
                continue;
            }

            progress?.Report(++requests);
            if (listing.Complete)
            {
                listed.Add(PathKey.FromPath(relDir));
            }
            else
            {
                warnings.AddRange(listing.Problems.Select(p => $"Incomplete remote listing of '{relDir}': {p}"));
            }

            foreach (var item in listing.Children.OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                var name = item.Name;
                var relPath = PathKey.Combine(relDir, name);
                var key = PathKey.FromPath(relPath);
                switch (filter.Check(key, name))
                {
                    case FilterVerdict.Ignore:
                        continue;
                    case FilterVerdict.Exclude:
                        skipped.Add(key);
                        continue;
                }

                var invalid = NameRules.WindowsInvalidReason(name);
                if (invalid is not null)
                {
                    skipped.Add(key);
                    warnings.Add($"Not downloaded: remote '{relPath}' — {invalid} on Windows.");
                    continue;
                }

                if (entries.ContainsKey(key))
                {
                    skipped.Add(key + "\u0000" + name);
                    warnings.Add($"Not synced: remote '{relPath}' differs from another item only in case.");
                    continue;
                }

                entries[key] = item.ToEntry(relPath);
                if (item.IsDir)
                {
                    queue.Enqueue(relPath);
                }
            }
        }

        logger.LogInformation(
            "Remote crawl of {Root}/{Subtree}: {Entries} entries, {Listed} folders listed, {Requests} requests, {Warnings} warnings",
            remoteRoot, subtree, entries.Count, listed.Count, requests, warnings.Count);
        return new RemoteSnapshot(entries, listed, skipped, warnings);
    }
}
