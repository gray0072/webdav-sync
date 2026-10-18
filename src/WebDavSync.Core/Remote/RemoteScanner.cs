using Microsoft.Extensions.Logging;
using WebDavSync.Core.Config;
using WebDavSync.Core.Model;

namespace WebDavSync.Core.Remote;

/// <summary>
/// Breadth-first <c>Depth: 1</c> crawl (§4.3): full, or a refresh of some folders of a cached snapshot. A folder is added
/// to <see cref="Snapshot{TEntry}.ListedFolders"/> only when its listing is complete (§6.8).
/// Up to <paramref name="parallel"/> listings are in flight (within the client's request rate limit); results are
/// processed one at a time, so the snapshot does not depend on the order in which listings arrive.
/// A crawl from a store with a tree listing (<see cref="IRemoteTreeLister"/>, §4.3 #0) takes the folders' listings from
/// one tree request and lists only the folders missing from it; without a tree it lists every folder.
/// </summary>
public sealed class RemoteScanner(IRemoteStore store, SyncFilter filter, ILogger logger, int parallel = 1)
{
    /// <param name="remoteRoot">Absolute remote root of the pair.</param>
    /// <param name="subtree">Relative folder to start from (<c>""</c> = whole pair).</param>
    public async Task<RemoteSnapshot> CrawlAsync(string remoteRoot, string subtree = "", IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var state = new CrawlState();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var tree = store is IRemoteTreeLister lister
            ? await lister.ListTreeAsync(PathKey.ToRemoteFull(remoteRoot, subtree), ct).ConfigureAwait(false)
            : null;
        await RunAsync(
            remoteRoot,
            [subtree],
            state,
            (relDir, listing) => AddChildren(state, relDir, listing, _ => true),
            relDir => relDir != subtree && Vanished(state, relDir),
            progress,
            ct,
            tree).ConfigureAwait(false);

        logger.LogInformation(
            "Remote crawl of {Root}/{Subtree}: {Entries} entries, {Listed} folders listed ({FromTree} from the tree listing), {Requests} requests, {Warnings} warnings in {Seconds:0.0} s ({Parallel} in parallel)",
            remoteRoot, subtree, state.Entries.Count, state.Listed.Count, state.TreeListings, state.Requests, state.Warnings.Count, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds, parallel);
        return state.ToSnapshot(subtree.Length == 0 ? tree?.ChangeToken : null);
    }

    /// <summary>
    /// Re-lists <paramref name="folderKeys"/> of a cached snapshot (§4.3 #3): their direct children are replaced, new
    /// subfolders are crawled, vanished subfolders are dropped with their subtrees; everything else stays as cached.
    /// A key that is not a cached folder is replaced by its nearest cached ancestor folder. Returns <c>null</c> when a
    /// re-listed folder no longer exists: the caller falls back to a full crawl.
    /// </summary>
    public async Task<RemoteSnapshot?> RefreshAsync(RemoteSnapshot cached, string remoteRoot, IEnumerable<string> folderKeys, CancellationToken ct = default)
    {
        var state = CrawlState.From(cached);
        var folders = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in folderKeys)
        {
            var folder = key;
            while (folder.Length > 0 && !(cached.Entries.TryGetValue(folder, out var e) && e.IsDir))
            {
                folder = PathKey.Parent(folder);
            }

            folders.TryAdd(folder, folder.Length == 0 ? string.Empty : cached.Entries[folder].Path);
        }

        // Children that were cached folders keep their cached subtrees; the others are new and get crawled.
        var oldDirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var folder in folders.Keys)
        {
            foreach (var child in state.Entries.Values.Where(e => PathKey.Parent(e.Key) == folder).ToList())
            {
                if (child.IsDir)
                {
                    oldDirs.Add(child.Key);
                }

                state.Entries.Remove(child.Key);
            }

            state.Skipped.RemoveWhere(s => PathKey.Parent(s.Split('\u0000')[0]) == folder);
            state.Listed.Remove(folder);
        }

        var seeds = folders.Values.ToHashSet(StringComparer.Ordinal);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        int requests;
        try
        {
            requests = await RunAsync(
                remoteRoot,
                seeds,
                state,
                (relDir, listing) =>
                {
                    var newDirs = AddChildren(state, relDir, listing, key => !oldDirs.Contains(key));
                    if (seeds.Contains(relDir))
                    {
                        // Cached subfolders that vanished or became files lose their cached subtrees.
                        var dirKey = PathKey.FromPath(relDir);
                        foreach (var gone in oldDirs.Where(d => PathKey.Parent(d) == dirKey && !(state.Entries.TryGetValue(d, out var e) && e.IsDir)).ToList())
                        {
                            state.RemoveBelow(gone);
                        }
                    }

                    return newDirs;
                },
                relDir => !seeds.Contains(relDir) && Vanished(state, relDir),
                null,
                ct).ConfigureAwait(false);
        }
        catch (RemoteException ex) when (ex.Kind == RemoteErrorKind.NotFound)
        {
            logger.LogInformation("Remote refresh: a re-listed folder is gone ({Message}); full crawl needed", ex.Message);
            return null;
        }

        logger.LogInformation(
            "Remote refresh of {Root}: {Folders} folders re-listed, {Requests} requests, {Entries} entries in {Seconds:0.0} s",
            remoteRoot, folders.Count, requests, state.Entries.Count, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds);
        return state.ToSnapshot();
    }

    /// <summary>Lists the seeds and every folder <paramref name="onListing"/> returns; returns the number of listings.</summary>
    private async Task<int> RunAsync(
        string remoteRoot,
        IEnumerable<string> seeds,
        CrawlState state,
        Func<string, RemoteListing, IEnumerable<string>> onListing,
        Func<string, bool> onVanished,
        IProgress<int>? progress,
        CancellationToken ct,
        RemoteTree? tree = null)
    {
        var queue = new Queue<string>(seeds);
        var requests = 0;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = new Dictionary<Task<RemoteListing>, string>();
        try
        {
            while (queue.Count > 0 || running.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                while (queue.Count > 0 && running.Count < Math.Max(1, parallel))
                {
                    var dir = queue.Dequeue();
                    running[ListAsync(dir)] = dir;
                }

                var task = await Task.WhenAny(running.Keys).ConfigureAwait(false);
                var relDir = running[task];
                running.Remove(task);
                RemoteListing listing;
                try
                {
                    listing = task.GetAwaiter().GetResult();
                }
                catch (RemoteException ex) when (ex.Kind == RemoteErrorKind.NotFound && onVanished(relDir))
                {
                    continue;
                }
                catch (RemoteException ex) when (!ex.StopsCycle && ex.Kind != RemoteErrorKind.NotFound)
                {
                    state.Warnings.Add($"Cannot list remote folder '{relDir}': {ex.Message}");
                    logger.LogWarning(ex, "Cannot list remote folder {Folder}", relDir);
                    continue;
                }

                progress?.Report(++requests);
                foreach (var child in onListing(relDir, listing))
                {
                    queue.Enqueue(child);
                }
            }
        }
        finally
        {
            if (running.Count > 0)
            {
                await cts.CancelAsync().ConfigureAwait(false);
                try
                {
                    await Task.WhenAll(running.Keys).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is RemoteException or OperationCanceledException)
                {
                    // The crawl already failed; outstanding listings are abandoned.
                }
            }
        }

        return requests;

        // Async wrapper: a store that throws synchronously still yields a faulted task.
        async Task<RemoteListing> ListAsync(string relDir)
        {
            var path = PathKey.ToRemoteFull(remoteRoot, relDir);
            if (tree is not null && tree.Listings.TryGetValue(path, out var listed))
            {
                Interlocked.Increment(ref state.TreeListings);
                return listed;
            }

            Interlocked.Increment(ref state.Requests);
            return await store.ListAsync(path, cts.Token).ConfigureAwait(false);
        }
    }

    /// <summary>Vanished mid-crawl: the parent listing is stale, so it can no longer prove deletions.</summary>
    private static bool Vanished(CrawlState state, string relDir)
    {
        state.Listed.Remove(PathKey.FromPath(PathKey.Parent(relDir)));
        state.Warnings.Add($"Remote folder '{relDir}' disappeared during the crawl.");
        return true;
    }

    /// <summary>Adds a listing's children; returns the child folders to crawl (those <paramref name="crawl"/> accepts).</summary>
    private List<string> AddChildren(CrawlState state, string relDir, RemoteListing listing, Func<string, bool> crawl)
    {
        if (listing.Complete)
        {
            state.Listed.Add(PathKey.FromPath(relDir));
        }
        else
        {
            state.Warnings.AddRange(listing.Problems.Select(p => $"Incomplete remote listing of '{relDir}': {p}"));
        }

        var folders = new List<string>();
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
                    state.Skipped.Add(key);
                    continue;
            }

            var invalid = NameRules.WindowsInvalidReason(name);
            if (invalid is not null)
            {
                state.Skipped.Add(key);
                state.Warnings.Add($"Not downloaded: remote '{relPath}' — {invalid} on Windows.");
                continue;
            }

            if (state.Entries.ContainsKey(key))
            {
                state.Skipped.Add(key + "\u0000" + name);
                state.Warnings.Add($"Not synced: remote '{relPath}' differs from another item only in case.");
                continue;
            }

            state.Entries[key] = item.ToEntry(relPath);
            if (item.IsDir && crawl(key))
            {
                folders.Add(relPath);
            }
        }

        return folders;
    }

    private sealed class CrawlState
    {
        public Dictionary<string, RemoteEntry> Entries { get; private init; } = new(StringComparer.Ordinal);

        public HashSet<string> Listed { get; private init; } = new(StringComparer.Ordinal);

        public HashSet<string> Skipped { get; private init; } = new(StringComparer.Ordinal);

        public List<string> Warnings { get; } = [];

        /// <summary>Per-folder listing requests (those not answered by the tree listing).</summary>
        public int Requests;

        /// <summary>Folders whose listing came from the tree listing.</summary>
        public int TreeListings;

        public static CrawlState From(RemoteSnapshot snapshot) => new()
        {
            Entries = new Dictionary<string, RemoteEntry>(snapshot.Entries, StringComparer.Ordinal),
            Listed = new HashSet<string>(snapshot.ListedFolders, StringComparer.Ordinal),
            Skipped = new HashSet<string>(snapshot.SkippedKeys, StringComparer.Ordinal),
        };

        /// <summary>Drops everything strictly below <paramref name="key"/>.</summary>
        public void RemoveBelow(string key)
        {
            foreach (var k in Entries.Keys.Where(k => PathKey.IsUnder(k, key)).ToList())
            {
                Entries.Remove(k);
            }

            Listed.RemoveWhere(k => PathKey.IsUnder(k, key) || k == key);
            Skipped.RemoveWhere(k => PathKey.IsUnder(k.Split('\u0000')[0], key));
        }

        public RemoteSnapshot ToSnapshot(string? changeToken = null) => new(Entries, Listed, Skipped, Warnings) { ChangeToken = changeToken };
    }
}
