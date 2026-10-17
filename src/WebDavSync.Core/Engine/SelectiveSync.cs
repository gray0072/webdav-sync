using System.Text.Json;
using WebDavSync.Core.Config;
using WebDavSync.Core.Model;
using WebDavSync.Core.Remote;
using WebDavSync.Core.State;

namespace WebDavSync.Core.Engine;

/// <summary>A top-level folder of a pair for the folder selection (§12.2).</summary>
public sealed record TopFolder(string Name, string Key, bool Excluded, bool OnLocal, bool? OnRemote);

/// <summary>Selective sync by top-level folder (§12.2).</summary>
public static class SelectiveSync
{
    /// <summary>
    /// Union of the local and remote top-level folders (the remote root is listed live; <see cref="TopFolder.OnRemote"/>
    /// is null when that fails) plus excluded names found on neither side.
    /// </summary>
    public static async Task<(IReadOnlyList<TopFolder> Folders, string? RemoteError)> ListAsync(PairSettings pair, IRemoteStore remote, CancellationToken ct)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var local = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in Directory.EnumerateDirectories(pair.LocalRoot))
        {
            var name = Path.GetFileName(dir);
            if (!SyncFilter.IsBuiltInIgnored(name))
            {
                var key = PathKey.FromPath(name);
                names.TryAdd(key, name);
                local.Add(key);
            }
        }

        HashSet<string>? remoteKeys = null;
        string? error = null;
        try
        {
            var listing = await remote.ListAsync(pair.RemoteRootNormalized, ct).ConfigureAwait(false);
            remoteKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var child in listing.Children.Where(c => c.IsDir && !SyncFilter.IsBuiltInIgnored(c.Name)))
            {
                var key = PathKey.FromPath(child.Name);
                names.TryAdd(key, child.Name);
                remoteKeys.Add(key);
            }
        }
        catch (RemoteException ex)
        {
            error = ex.Message;
        }

        var excluded = ExcludedTopLevel(pair);
        foreach (var (key, name) in excluded)
        {
            names.TryAdd(key, name);
        }

        var folders = names
            .Select(kv => new TopFolder(kv.Value, kv.Key, excluded.ContainsKey(kv.Key), local.Contains(kv.Key), remoteKeys?.Contains(kv.Key)))
            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return (folders, error);
    }

    /// <summary>Sets the excluded top-level folders; deeper excluded paths are kept. Returns the new list.</summary>
    public static List<string> WithTopLevelExcluded(PairSettings pair, IEnumerable<string> excludedNames) =>
        [
            .. pair.ExcludedFolders.Where(f => f.Replace('\\', '/').Trim('/').Contains('/')),
            .. excludedNames.Select(n => n.Trim('/', '\\')).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase),
        ];

    /// <summary>
    /// Applies a changed selection to the state (§12.2), before planning: the base entries of every folder whose
    /// exclusion changed are dropped. While excluded, nothing below it is synced or deleted; once included again, the
    /// subtree has no base, so it merges like an initial sync (identical files are adopted, differences become
    /// conflicts, nothing is deleted; safety invariant 10). Returns the keys that changed.
    /// </summary>
    public static IReadOnlyList<string> ApplyChanges(PairSettings pair, StateStore store)
    {
        var current = new SyncFilter(pair.ExcludedFolders).ExcludedFolderKeys.ToHashSet(StringComparer.Ordinal);
        var stored = store.GetMeta(MetaKeys.ExcludedFolders) is { } json
            ? JsonSerializer.Deserialize<List<string>>(json)?.ToHashSet(StringComparer.Ordinal)
            : null;
        store.SetMeta(MetaKeys.ExcludedFolders, JsonSerializer.Serialize(current.Order(StringComparer.Ordinal).ToList()));
        if (stored is null)
        {
            return [];
        }

        var changed = current.Except(stored).Concat(stored.Except(current)).Order(StringComparer.Ordinal).ToList();
        if (changed.Count > 0)
        {
            store.ApplyBase(changed.Select(k => new RemoveBaseSubtree(k)));
        }

        return changed;
    }

    private static Dictionary<string, string> ExcludedTopLevel(PairSettings pair) =>
        pair.ExcludedFolders
            .Select(f => f.Replace('\\', '/').Trim('/'))
            .Where(f => f.Length > 0 && !f.Contains('/'))
            .GroupBy(PathKey.FromPath)
            .ToDictionary(g => g.Key, g => g.First());
}
