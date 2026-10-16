using Microsoft.Data.Sqlite;

namespace WebDavSync.Core.Tests.Support;

/// <summary>
/// Builds a small <c>.cloud.db</c> with the legacy schema (§3.1, §19.5 #8). The real file has personal data and
/// is never committed.
/// </summary>
internal sealed class LegacyFixture
{
    private readonly List<(long Id, string Name, string? Mapping, long Inode, bool Ignore, int Tree)> _folders = [];
    private readonly List<(long Id, string Name, string? Mapping, long Inode, long Time, long Size, string Hash)> _files = [];
    private readonly List<(long Parent, long Child, bool IsFolder)> _closure = [];
    private long _nextFolderId = 1;
    private long _nextFileId = 1;

    public LegacyFixture()
    {
        LocalRoot = AddFolderRow("/", 0, false, 1);
        RemoteRoot = AddFolderRow("/", 0, false, 2);
    }

    public long LocalRoot { get; }

    public long RemoteRoot { get; }

    public long Folder(long parent, string name, long inode = 0, bool ignore = false, int tree = 1, string? mapping = null)
    {
        var id = AddFolderRow(name, inode, ignore, tree, mapping);
        _closure.Add((parent, id, true));
        return id;
    }

    public long File(long parent, string name, long size, string hash, long time = 1_767_261_600, long inode = 0, string? mapping = null)
    {
        var id = _nextFileId++;
        _files.Add((id, name, mapping, inode, time, size, hash));
        _closure.Add((parent, id, false));
        return id;
    }

    public void Write(string path)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        Exec(db, """
            CREATE TABLE "CLOSURE_TABLE" ("parent_id" INTEGER NOT NULL ,"child_id" INTEGER NOT NULL ,"child_is_folder" INTEGER NOT NULL );
            CREATE TABLE "FILES_RECORDS" ("id" INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL ,"inode" INTEGER NOT NULL DEFAULT "0" ,"key" TEXT NOT NULL ,"name" TEXT NOT NULL ,"mapping_name" TEXT NULL ,"changed" INTEGER NOT NULL ,"needs_update" INTEGER NOT NULL ,"time" INTEGER NOT NULL ,"size" INTEGER NOT NULL ,"hash" TEXT NOT NULL );
            CREATE TABLE "TREE_REV_INFO" ("id" INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL ,"tree_id" TEXT NOT NULL ,"rev_new" INTEGER NOT NULL ,"rev_old" INTEGER NOT NULL ,"rev_local" INTEGER NOT NULL ,"access" INTEGER NOT NULL ,"tree_type" INTEGER NOT NULL );
            CREATE TABLE "FOLDERS_RECORDS" ("id" INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL ,"inode" INTEGER NOT NULL DEFAULT "0" ,"key" TEXT NOT NULL ,"name" TEXT NOT NULL ,"mapping_name" TEXT NULL ,"changed" INTEGER NOT NULL ,"ignore" INTEGER NOT NULL ,"rev_info_id" INTEGER NOT NULL, FOREIGN KEY ("rev_info_id") REFERENCES "TREE_REV_INFO" ("id") );
            CREATE TABLE "GENERAL_INFO" ("attribute" TEXT NOT NULL NULL ,"value" TEXT NULL );
            INSERT INTO TREE_REV_INFO(tree_id, rev_new, rev_old, rev_local, access, tree_type) VALUES ('6C6F63616C', 0, 0, 0, 0, 3), ('72656D6F7465', 5, 4, 0, 0, 0);
            """);
        Exec(db, "INSERT INTO GENERAL_INFO VALUES ('db_version', '0518'), ('client_version', '15.0.0'), ('email', 'user@example.com'), ('r_root_id', $r), ('l_root_id', $l)",
            ("$r", RemoteRoot.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("$l", LocalRoot.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        foreach (var f in _folders)
        {
            Exec(db, "INSERT INTO FOLDERS_RECORDS(id, inode, key, name, mapping_name, changed, ignore, rev_info_id) VALUES ($id, $inode, $key, $name, $map, 0, $ignore, $tree)",
                ("$id", f.Id), ("$inode", f.Inode), ("$key", f.Name == "/" ? string.Empty : f.Name.ToLowerInvariant()), ("$name", f.Name), ("$map", f.Mapping), ("$ignore", f.Ignore ? 1 : 0), ("$tree", f.Tree));
        }

        foreach (var f in _files)
        {
            Exec(db, "INSERT INTO FILES_RECORDS(id, inode, key, name, mapping_name, changed, needs_update, time, size, hash) VALUES ($id, $inode, $key, $name, $map, 0, 0, $time, $size, $hash)",
                ("$id", f.Id), ("$inode", f.Inode), ("$key", f.Name.ToLowerInvariant()), ("$name", f.Name), ("$map", f.Mapping), ("$time", f.Time), ("$size", f.Size), ("$hash", f.Hash));
        }

        foreach (var c in _closure)
        {
            Exec(db, "INSERT INTO CLOSURE_TABLE VALUES ($p, $c, $f)", ("$p", c.Parent), ("$c", c.Child), ("$f", c.IsFolder ? 1 : 0));
        }
    }

    private long AddFolderRow(string name, long inode, bool ignore, int tree, string? mapping = null)
    {
        var id = _nextFolderId++;
        _folders.Add((id, name, mapping, inode, ignore, tree));
        return id;
    }

    private static void Exec(SqliteConnection db, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        cmd.ExecuteNonQuery();
    }
}

/// <summary>Temporary folder, deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wds-tests", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string relative, string content, DateTime? mtime = null)
    {
        var full = System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        if (mtime is { } t)
        {
            System.IO.File.SetLastWriteTimeUtc(full, t);
        }

        return full;
    }

    public string Dir(string relative) =>
        Directory.CreateDirectory(System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar))).FullName;

    public string Full(string relative) => System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>All files as relative path → content, excluding the state folder.</summary>
    public Dictionary<string, string> Files() =>
        Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories)
            .Select(f => System.IO.Path.GetRelativePath(Path, f).Replace(System.IO.Path.DirectorySeparatorChar, '/'))
            .Where(f => !f.StartsWith(".webdavsync/", StringComparison.Ordinal))
            .ToDictionary(f => f, f => System.IO.File.ReadAllText(Full(f)), StringComparer.Ordinal);

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
