using System.Globalization;
using Microsoft.Data.Sqlite;
using WebDavSync.Core.Model;
using WebDavSync.Core.Planning;

namespace WebDavSync.Core.State;

/// <summary>Base mutations, applied in the same transaction as the journal record (§6.1 step 7).</summary>
public abstract record BaseMutation;

public sealed record UpsertBase(BaseEntry Entry) : BaseMutation;

public sealed record RemoveBaseSubtree(string Key) : BaseMutation;

/// <summary>Same transformation the planner simulates for moves (<see cref="BaseEntry.Rebase"/>).</summary>
public sealed record RebaseSubtree(string OldKey, string NewKey, string NewLocalPath, string NewRemotePath) : BaseMutation;

public enum JournalStatus
{
    Pending,
    Running,
    Done,
    Failed,
    Skipped,
    Interrupted,
}

public static class MetaKeys
{
    public const string SchemaVersion = "schema_version";
    public const string PairId = "pair_id";
    public const string InitialSyncDone = "initial_sync_done";
    public const string LegacyImport = "legacy_import";
    public const string Capabilities = "capabilities";
    public const string LastCycle = "last_cycle";
    public const string RemoteMarkerCreated = "remote_marker_created";
    public const string LastFullCrawl = "last_full_crawl";
    public const string RemoteChangeToken = "remote_change_token";
    public const string ExcludedFolders = "excluded_folders";
    public const string VerifyAt = "local_verify_at";
    public const string VerifyCursor = "local_verify_cursor";
}

/// <summary><c>state.db</c> (§15.3): SQLite in WAL mode, raw SQL.</summary>
public sealed class StateStore : IDisposable
{
    private const int SchemaVersion = 2;

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS items(
            key TEXT PRIMARY KEY,
            is_dir INTEGER NOT NULL,
            local_path TEXT NOT NULL,
            remote_path TEXT NOT NULL,
            local_file_id INTEGER NULL,
            local_size INTEGER NOT NULL,
            local_mtime INTEGER NULL,
            local_hash TEXT NULL,
            remote_etag TEXT NULL,
            remote_size INTEGER NOT NULL,
            remote_mtime INTEGER NULL,
            remote_hash TEXT NULL,
            remote_confirmed INTEGER NOT NULL,
            synced_at INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS journal(
            op_id TEXT PRIMARY KEY,
            cycle_id TEXT NOT NULL,
            type TEXT NOT NULL,
            path TEXT NULL,
            path2 TEXT NULL,
            status TEXT NOT NULL,
            attempts INTEGER NOT NULL DEFAULT 0,
            error TEXT NULL,
            created_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS journal_status ON journal(status);
        CREATE TABLE IF NOT EXISTS conflicts(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            key TEXT NOT NULL,
            local_copy TEXT NULL,
            remote_copy TEXT NULL,
            detected_at INTEGER NOT NULL,
            resolved_at INTEGER NULL);
        CREATE TABLE IF NOT EXISTS held(
            key TEXT NOT NULL,
            type TEXT NOT NULL,
            path TEXT NOT NULL,
            reason TEXT NOT NULL,
            state TEXT NOT NULL,
            held_at INTEGER NOT NULL,
            PRIMARY KEY(key, type));
        CREATE TABLE IF NOT EXISTS meta(
            key TEXT PRIMARY KEY,
            value TEXT NULL);
        """;

    private readonly SqliteConnection _db;

    private StateStore(SqliteConnection db) => _db = db;

    public string Path { get; private init; } = string.Empty;

    public static StateStore Open(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        var store = new StateStore(db) { Path = path };
        try
        {
            store.Exec("PRAGMA journal_mode=WAL;");
            store.Exec("PRAGMA synchronous=NORMAL;");
            store.Exec("PRAGMA busy_timeout=5000;");
            var check = store.Scalar("PRAGMA quick_check;") as string;
            if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"state.db integrity check failed: {check}. Restore from backup\\ or reset the pair state.");
            }

            // Schema 1 had an unused held(key, reason, held_at) table.
            if (store.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'meta'") is > 0L &&
                store.GetMeta(MetaKeys.SchemaVersion) == "1")
            {
                store.Exec("DROP TABLE IF EXISTS held;");
            }

            store.Exec(Schema);
            store.SetMeta(MetaKeys.SchemaVersion, SchemaVersion.ToString(CultureInfo.InvariantCulture));
            return store;
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    // ---------------------------------------------------------------- meta

    public string? GetMeta(string key)
    {
        using var cmd = Command("SELECT value FROM meta WHERE key = $k", ("$k", key));
        return cmd.ExecuteScalar() as string;
    }

    public void SetMeta(string key, string? value)
    {
        using var cmd = Command("INSERT INTO meta(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value", ("$k", key), ("$v", value));
        cmd.ExecuteNonQuery();
    }

    /// <summary>When the last complete full remote crawl finished (§8 start-up trust).</summary>
    public DateTime? LastFullCrawlUtc
    {
        get => long.TryParse(GetMeta(MetaKeys.LastFullCrawl), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
            ? new DateTime(ticks, DateTimeKind.Utc)
            : null;
        set => SetMeta(MetaKeys.LastFullCrawl, value?.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>When the last rolling-verify slice was taken (§5.3).</summary>
    public DateTime? VerifyAtUtc
    {
        get => long.TryParse(GetMeta(MetaKeys.VerifyAt), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
            ? new DateTime(ticks, DateTimeKind.Utc)
            : null;
        set => SetMeta(MetaKeys.VerifyAt, value?.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
    }

    public bool InitialSyncDone
    {
        get => GetMeta(MetaKeys.InitialSyncDone) == "1";
        set => SetMeta(MetaKeys.InitialSyncDone, value ? "1" : "0");
    }

    // ---------------------------------------------------------------- base

    public int BaseCount => Convert.ToInt32(Scalar("SELECT COUNT(*) FROM items"), CultureInfo.InvariantCulture);

    public Dictionary<string, BaseEntry> LoadBase()
    {
        var result = new Dictionary<string, BaseEntry>(StringComparer.Ordinal);
        using var cmd = Command("SELECT key, is_dir, local_path, remote_path, local_file_id, local_size, local_mtime, local_hash, remote_etag, remote_size, remote_mtime, remote_hash, remote_confirmed, synced_at FROM items");
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var entry = ReadEntry(reader);
            result[entry.Key] = entry;
        }

        return result;
    }

    public List<BaseEntry> GetSubtree(string key)
    {
        using var cmd = Command("SELECT key FROM items WHERE key = $k OR substr(key, 1, $n) = $prefix", ("$k", key), ("$n", key.Length + 1), ("$prefix", key + "/"));
        using var reader = cmd.ExecuteReader();
        var keys = new List<string>();
        while (reader.Read())
        {
            keys.Add(reader.GetString(0));
        }

        return keys.Select(k => Get(k)!).ToList();
    }

    /// <summary>Replaces the whole base (legacy import).</summary>
    public void ReplaceBase(IEnumerable<BaseEntry> entries)
    {
        using var tx = _db.BeginTransaction();
        Exec("DELETE FROM items", tx);
        foreach (var e in entries)
        {
            Upsert(e, tx);
        }

        tx.Commit();
    }

    public void ApplyBase(IEnumerable<BaseMutation> mutations)
    {
        using var tx = _db.BeginTransaction();
        foreach (var m in mutations)
        {
            Apply(m, tx);
        }

        tx.Commit();
    }

    // ---------------------------------------------------------------- journal (§6.7)

    public void JournalStart(string opId, string cycleId, string type, string? path, string? path2)
    {
        var now = Now();
        using var cmd = Command(
            "INSERT INTO journal(op_id, cycle_id, type, path, path2, status, attempts, created_at, updated_at) VALUES($id, $c, $t, $p, $p2, $s, 0, $now, $now)",
            ("$id", opId), ("$c", cycleId), ("$t", type), ("$p", path), ("$p2", path2), ("$s", nameof(JournalStatus.Running)), ("$now", now));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Finishes an op: journal status and base mutations in one transaction.</summary>
    public void JournalFinish(string opId, JournalStatus status, int attempts, string? error, IEnumerable<BaseMutation>? mutations = null)
    {
        using var tx = _db.BeginTransaction();
        foreach (var m in mutations ?? [])
        {
            Apply(m, tx);
        }

        using (var cmd = Command("UPDATE journal SET status = $s, attempts = $a, error = $e, updated_at = $now WHERE op_id = $id", tx,
                   ("$s", status.ToString()), ("$a", attempts), ("$e", error), ("$now", Now()), ("$id", opId)))
        {
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>Marks ops left <c>Running</c> by a crash as interrupted; the next plan re-validates them (§6.7).</summary>
    public int RecoverJournal()
    {
        using var cmd = Command("UPDATE journal SET status = $i, updated_at = $now WHERE status = $r",
            ("$i", nameof(JournalStatus.Interrupted)), ("$now", Now()), ("$r", nameof(JournalStatus.Running)));
        return cmd.ExecuteNonQuery();
    }

    public void AddConflict(string key, string? localCopy, string? remoteCopy)
    {
        using var cmd = Command("INSERT INTO conflicts(key, local_copy, remote_copy, detected_at) VALUES($k, $l, $r, $now)",
            ("$k", key), ("$l", localCopy), ("$r", remoteCopy), ("$now", Now()));
        cmd.ExecuteNonQuery();
    }

    public int OpenConflictCount => Convert.ToInt32(Scalar("SELECT COUNT(*) FROM conflicts WHERE resolved_at IS NULL"), CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- held list (§10)

    public List<HeldItem> LoadHeld()
    {
        using var cmd = Command("SELECT key, type, path, reason, state, held_at FROM held ORDER BY key, type");
        using var reader = cmd.ExecuteReader();
        var result = new List<HeldItem>();
        while (reader.Read())
        {
            if (Enum.TryParse<OpType>(reader.GetString(1), out var type))
            {
                result.Add(new HeldItem(reader.GetString(0), type, reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    new DateTime(reader.GetInt64(5), DateTimeKind.Utc)));
            }
        }

        return result;
    }

    public void Hold(IEnumerable<HeldItem> items)
    {
        using var tx = _db.BeginTransaction();
        foreach (var h in items)
        {
            using var cmd = Command("INSERT OR REPLACE INTO held(key, type, path, reason, state, held_at) VALUES($k, $t, $p, $r, $s, $at)", tx,
                ("$k", h.Key), ("$t", h.Type.ToString()), ("$p", h.DisplayPath), ("$r", h.Reason), ("$s", h.State), ("$at", h.HeldAtUtc.ToUniversalTime().Ticks));
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>Releases the given items; returns how many were held.</summary>
    public int Release(IEnumerable<HeldItem> items)
    {
        using var tx = _db.BeginTransaction();
        var count = 0;
        foreach (var h in items)
        {
            using var cmd = Command("DELETE FROM held WHERE key = $k AND type = $t", tx, ("$k", h.Key), ("$t", h.Type.ToString()));
            count += cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return count;
    }

    /// <summary>Daily copy via the SQLite online backup API, keeping the newest <paramref name="keep"/> (§15.2).</summary>
    public void BackupDaily(string backupDir, int keep = 3)
    {
        Directory.CreateDirectory(backupDir);
        var target = System.IO.Path.Combine(backupDir, $"state-{DateTime.Now:yyyyMMdd}.db");
        if (!File.Exists(target))
        {
            using var dest = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString());
            dest.Open();
            _db.BackupDatabase(dest);
        }

        foreach (var old in Directory.GetFiles(backupDir, "state-*.db").OrderDescending().Skip(keep))
        {
            File.Delete(old);
        }
    }

    public void Dispose() => _db.Dispose();

    // ---------------------------------------------------------------- internals

    private void Apply(BaseMutation mutation, SqliteTransaction tx)
    {
        switch (mutation)
        {
            case UpsertBase u:
                Upsert(u.Entry, tx);
                break;
            case RemoveBaseSubtree r:
                using (var cmd = Command("DELETE FROM items WHERE key = $k OR substr(key, 1, $n) = $prefix", tx,
                           ("$k", r.Key), ("$n", r.Key.Length + 1), ("$prefix", r.Key + "/")))
                {
                    cmd.ExecuteNonQuery();
                }

                break;
            case RebaseSubtree m:
                Rebase(m, tx);
                break;
        }
    }

    private void Rebase(RebaseSubtree m, SqliteTransaction tx)
    {
        var all = new List<BaseEntry>();
        using (var cmd = Command("SELECT key FROM items WHERE key = $k OR substr(key, 1, $n) = $prefix", tx,
                   ("$k", m.OldKey), ("$n", m.OldKey.Length + 1), ("$prefix", m.OldKey + "/")))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                all.Add(Get(reader.GetString(0), tx)!);
            }
        }

        var root = all.FirstOrDefault(e => e.Key == m.OldKey);
        if (root is null)
        {
            return;
        }

        Apply(new RemoveBaseSubtree(m.OldKey), tx);
        foreach (var e in all)
        {
            Upsert(e.Rebase(root, m.NewKey, m.NewLocalPath, m.NewRemotePath), tx);
        }
    }

    public BaseEntry? Get(string key, SqliteTransaction? tx = null)
    {
        using var cmd = Command("SELECT key, is_dir, local_path, remote_path, local_file_id, local_size, local_mtime, local_hash, remote_etag, remote_size, remote_mtime, remote_hash, remote_confirmed, synced_at FROM items WHERE key = $k", tx, ("$k", key));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadEntry(reader) : null;
    }

    private static BaseEntry ReadEntry(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetInt64(1) != 0,
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : unchecked((ulong)reader.GetInt64(4)),
            reader.GetInt64(5),
            reader.IsDBNull(6) ? null : new DateTime(reader.GetInt64(6), DateTimeKind.Utc),
            reader.IsDBNull(7) ? null : ContentHash.ParseOrNull(reader.GetString(7)),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetInt64(9),
            reader.IsDBNull(10) ? null : new DateTime(reader.GetInt64(10), DateTimeKind.Utc),
            reader.IsDBNull(11) ? null : ContentHash.ParseOrNull(reader.GetString(11)),
            reader.GetInt64(12) != 0,
            new DateTime(reader.GetInt64(13), DateTimeKind.Utc));

    private void Upsert(BaseEntry e, SqliteTransaction tx)
    {
        using var cmd = Command(
            """
            INSERT OR REPLACE INTO items(key, is_dir, local_path, remote_path, local_file_id, local_size, local_mtime, local_hash,
                remote_etag, remote_size, remote_mtime, remote_hash, remote_confirmed, synced_at)
            VALUES($key, $dir, $lp, $rp, $lid, $ls, $lm, $lh, $re, $rs, $rm, $rh, $rc, $at)
            """,
            tx,
            ("$key", e.Key),
            ("$dir", e.IsDir ? 1 : 0),
            ("$lp", e.LocalPath),
            ("$rp", e.RemotePath),
            ("$lid", e.LocalFileId is { } id ? unchecked((long)id) : null),
            ("$ls", e.LocalSize),
            ("$lm", e.LocalMtimeUtc?.ToUniversalTime().Ticks),
            ("$lh", e.LocalHash?.Hex),
            ("$re", e.RemoteETag),
            ("$rs", e.RemoteSize),
            ("$rm", e.RemoteMtimeUtc?.ToUniversalTime().Ticks),
            ("$rh", e.RemoteHash?.Hex),
            ("$rc", e.RemoteConfirmed ? 1 : 0),
            ("$at", e.SyncedAtUtc.ToUniversalTime().Ticks));
        cmd.ExecuteNonQuery();
    }

    private static long Now() => DateTime.UtcNow.Ticks;

    private void Exec(string sql, SqliteTransaction? tx = null)
    {
        using var cmd = Command(sql, tx);
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var cmd = Command(sql);
        return cmd.ExecuteScalar();
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters) => Command(sql, null, parameters);

    private SqliteCommand Command(string sql, SqliteTransaction? tx, params (string Name, object? Value)[] parameters)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return cmd;
    }
}
