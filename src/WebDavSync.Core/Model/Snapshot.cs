namespace WebDavSync.Core.Model;

/// <summary>
/// One side's tree. <see cref="ListedFolders"/> holds keys of folders whose child listing is complete
/// (<c>""</c> = root); an item missing from <see cref="Entries"/> counts as deleted only when it is
/// "known absent" (invariant #1). <see cref="SkippedKeys"/> exist on this side but are not synced
/// (invalid names, user-excluded, reparse points) — their ancestors are never deleted recursively.
/// </summary>
public class Snapshot<TEntry>
    where TEntry : ISnapshotEntry
{
    public Snapshot(
        IReadOnlyDictionary<string, TEntry> entries,
        IReadOnlySet<string> listedFolders,
        IReadOnlySet<string>? skippedKeys = null,
        IReadOnlyList<string>? warnings = null)
    {
        Entries = entries;
        ListedFolders = listedFolders;
        SkippedKeys = skippedKeys ?? new HashSet<string>(StringComparer.Ordinal);
        Warnings = warnings ?? [];
    }

    public IReadOnlyDictionary<string, TEntry> Entries { get; }

    public IReadOnlySet<string> ListedFolders { get; }

    public IReadOnlySet<string> SkippedKeys { get; }

    public IReadOnlyList<string> Warnings { get; }

    /// <summary>True when the root and every folder of this snapshot were listed completely.</summary>
    public bool IsComplete =>
        ListedFolders.Contains(string.Empty) &&
        Entries.Values.Where(e => e.IsDir).All(e => ListedFolders.Contains(e.Key));
}

public sealed class LocalSnapshot(
    IReadOnlyDictionary<string, LocalEntry> entries,
    IReadOnlySet<string> listedFolders,
    IReadOnlySet<string>? skippedKeys = null,
    IReadOnlyList<string>? warnings = null)
    : Snapshot<LocalEntry>(entries, listedFolders, skippedKeys, warnings);

public sealed class RemoteSnapshot(
    IReadOnlyDictionary<string, RemoteEntry> entries,
    IReadOnlySet<string> listedFolders,
    IReadOnlySet<string>? skippedKeys = null,
    IReadOnlyList<string>? warnings = null)
    : Snapshot<RemoteEntry>(entries, listedFolders, skippedKeys, warnings)
{
    /// <summary>
    /// The remote root's change token at the time of this crawl (§4.3 #0); <c>null</c> when the remote has none or the
    /// crawl did not come from a tree listing of the whole pair.
    /// </summary>
    public string? ChangeToken { get; init; }
}
