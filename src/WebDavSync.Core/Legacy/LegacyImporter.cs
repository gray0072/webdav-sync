using System.Globalization;
using Microsoft.Data.Sqlite;
using WebDavSync.Core.Model;

namespace WebDavSync.Core.Legacy;

public sealed record LegacyItem(string Path, string Key, bool IsDir, ulong? Inode, DateTime? Mtime, long Size, ContentHash? Hash, bool Ignore);

public sealed record LegacyImportResult(
    IReadOnlyDictionary<string, string?> GeneralInfo,
    IReadOnlyDictionary<string, LegacyItem> LocalTree,
    IReadOnlyDictionary<string, LegacyItem> RemoteTree,
    IReadOnlyList<BaseEntry> Base,
    IReadOnlyList<string> IgnoredFolders,
    IReadOnlyList<string> Warnings)
{
    public int LocalFiles => LocalTree.Values.Count(i => !i.IsDir);

    public int LocalFolders => LocalTree.Values.Count(i => i.IsDir);

    public int RemoteFiles => RemoteTree.Values.Count(i => !i.IsDir);

    public int RemoteFolders => RemoteTree.Values.Count(i => i.IsDir);

    public int BothEqual => Base.Count;

    public int BothDifferent => LocalTree.Keys.Count(k => RemoteTree.ContainsKey(k)) - Base.Count;

    public int LocalOnly => LocalTree.Keys.Count(k => !RemoteTree.ContainsKey(k));

    public int RemoteOnly => RemoteTree.Keys.Count(k => !LocalTree.ContainsKey(k));
}

/// <summary>
/// Read-only importer of the legacy client's <c>.cloud.db</c> (§3). Always works on a copy (invariant #9).
/// </summary>
public static class LegacyImporter
{
    public const string DbFileName = ".cloud.db";

    public static LegacyImportResult Import(string cloudDbPath, string tempDir)
    {
        Directory.CreateDirectory(tempDir);
        var copy = Path.Combine(tempDir, $"cloud-{Guid.NewGuid():N}.db");
        File.Copy(cloudDbPath, copy);
        try
        {
            return ImportCopy(copy);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            LocalFsTryDelete(copy);
        }
    }

    private static LegacyImportResult ImportCopy(string copy)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        var warnings = new List<string>();

        var info = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Query(db, "SELECT attribute, value FROM GENERAL_INFO"))
        {
            info[(string)row[0]!] = row[1] as string;
        }

        var folders = new Dictionary<long, (ulong? Inode, string Name, string? Mapping, bool Ignore)>();
        foreach (var r in Query(db, "SELECT id, inode, name, mapping_name, ignore FROM FOLDERS_RECORDS"))
        {
            folders[(long)r[0]!] = (Inode(r[1]), (string)r[2]!, r[3] as string, Convert.ToInt64(r[4], CultureInfo.InvariantCulture) != 0);
        }

        var files = new Dictionary<long, (ulong? Inode, string Name, string? Mapping, long Time, long Size, string Hash)>();
        foreach (var r in Query(db, "SELECT id, inode, name, mapping_name, time, size, hash FROM FILES_RECORDS"))
        {
            files[(long)r[0]!] = (Inode(r[1]), (string)r[2]!, r[3] as string, (long)r[4]!, (long)r[5]!, (string)r[6]!);
        }

        var children = new Dictionary<long, List<(long Id, bool IsFolder)>>();
        foreach (var r in Query(db, "SELECT parent_id, child_id, child_is_folder FROM CLOSURE_TABLE"))
        {
            var parent = (long)r[0]!;
            (children.TryGetValue(parent, out var list) ? list : children[parent] = []).Add(((long)r[1]!, (long)r[2]! != 0));
        }

        Dictionary<string, LegacyItem> BuildTree(string rootAttribute, bool local)
        {
            var tree = new Dictionary<string, LegacyItem>(StringComparer.Ordinal);
            if (!info.TryGetValue(rootAttribute, out var rootText) || !long.TryParse(rootText, CultureInfo.InvariantCulture, out var rootId))
            {
                warnings.Add($"GENERAL_INFO.{rootAttribute} is missing.");
                return tree;
            }

            var visited = new HashSet<long>();
            var stack = new Stack<(long Id, string Path)>();
            stack.Push((rootId, string.Empty));
            while (stack.Count > 0)
            {
                var (folderId, folderPath) = stack.Pop();
                if (!visited.Add(folderId) || !children.TryGetValue(folderId, out var list))
                {
                    continue;
                }

                foreach (var (childId, isFolder) in list)
                {
                    string name;
                    LegacyItem item;
                    if (isFolder)
                    {
                        if (!folders.TryGetValue(childId, out var f))
                        {
                            warnings.Add($"Dangling folder id {childId} under '{folderPath}'.");
                            continue;
                        }

                        name = local && !string.IsNullOrEmpty(f.Mapping) ? f.Mapping : f.Name;
                        var path = PathKey.Combine(folderPath, name);
                        item = new LegacyItem(path, PathKey.FromPath(path), true, f.Inode, null, 0, null, f.Ignore);
                        stack.Push((childId, path));
                    }
                    else
                    {
                        if (!files.TryGetValue(childId, out var f))
                        {
                            warnings.Add($"Dangling file id {childId} under '{folderPath}'.");
                            continue;
                        }

                        name = local && !string.IsNullOrEmpty(f.Mapping) ? f.Mapping : f.Name;
                        var path = PathKey.Combine(folderPath, name);
                        var hash = ContentHash.ParseOrNull(f.Hash);
                        if (hash is null)
                        {
                            warnings.Add($"Invalid hash for '{path}'.");
                        }

                        item = new LegacyItem(path, PathKey.FromPath(path), false, f.Inode, Mtime.FromUnixSeconds(f.Time), f.Size, hash, false);
                    }

                    if (!tree.TryAdd(item.Key, item))
                    {
                        warnings.Add($"Duplicate key '{item.Key}' in the {(local ? "local" : "remote")} tree.");
                    }
                }
            }

            return tree;
        }

        var localTree = BuildTree("l_root_id", local: true);
        var remoteTree = BuildTree("r_root_id", local: false);

        var now = DateTime.UtcNow;
        var baseEntries = new List<BaseEntry>();
        foreach (var (key, l) in localTree)
        {
            if (!remoteTree.TryGetValue(key, out var r) || l.IsDir != r.IsDir)
            {
                continue;
            }

            if (l.IsDir)
            {
                baseEntries.Add(new BaseEntry(key, true, l.Path, r.Path, l.Inode, 0, null, null, null, 0, null, null, false, now));
            }
            else if (l.Size == r.Size && l.Hash is not null && l.Hash == r.Hash)
            {
                baseEntries.Add(new BaseEntry(key, false, l.Path, r.Path, l.Inode, l.Size, l.Mtime, l.Hash, null, r.Size, r.Mtime, r.Hash, false, now));
            }
        }

        var ignored = localTree.Values.Concat(remoteTree.Values)
            .Where(i => i is { IsDir: true, Ignore: true })
            .Select(i => i.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToList();

        return new LegacyImportResult(info, localTree, remoteTree, baseEntries, ignored, warnings);
    }

    private static ulong? Inode(object? value)
    {
        var v = Convert.ToInt64(value ?? 0L, CultureInfo.InvariantCulture);
        return v == 0 ? null : unchecked((ulong)v);
    }

    private static List<object?[]> Query(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    private static void LocalFsTryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
