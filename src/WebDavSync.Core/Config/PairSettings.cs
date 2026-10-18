using System.Text.Json;
using System.Text.Json.Serialization;
using WebDavSync.Core.Model;
using WebDavSync.Core.Planning;

namespace WebDavSync.Core.Config;

public enum SyncMode
{
    DryRun,
    ApproveAll,
    Guarded,
    Auto,
}

public enum ConflictWinner
{
    Local,
    Remote,
    Newer,
}

public enum LocalDeleteTarget
{
    RecycleBin,
    StateTrash,
}

/// <summary>How imported (legacy) base entries are matched against the first remote crawl (§19.5 #5).</summary>
public enum ImportedBaseMatch
{
    SizeOnly,
    SizeAndMtime,
}

/// <summary>One sync pair as stored in <c>data\settings.json</c>. Schema: §15.5.</summary>
public sealed class PairSettings
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public string LocalRoot { get; set; } = string.Empty;

    /// <summary>WebDAV endpoint, e.g. <c>https://webdav.cloud.mail.ru/</c>.</summary>
    public string RemoteUrl { get; set; } = string.Empty;

    /// <summary>Remote root folder under the endpoint, e.g. <c>/Cloud</c>; <c>/</c> for the whole account.</summary>
    public string RemoteRoot { get; set; } = "/";

    public string User { get; set; } = string.Empty;

    public SyncMode Mode { get; set; } = SyncMode.ApproveAll;

    public int? MaxOpsPerCycle { get; set; }

    public ConflictWinner ConflictWinner { get; set; } = ConflictWinner.Local;

    public int MassDeleteCount { get; set; } = 50;

    public double MassDeletePercent { get; set; } = 5;

    public long MaxCompareDownloadBytes { get; set; } = 64L * 1024 * 1024;

    public long MinFreeDiskSpace { get; set; } = 2L * 1024 * 1024 * 1024;

    public bool KeepOverwrittenVersions { get; set; } = true;

    public LocalDeleteTarget LocalDeleteTarget { get; set; } = LocalDeleteTarget.RecycleBin;

    public ImportedBaseMatch ImportedBaseMatch { get; set; } = ImportedBaseMatch.SizeOnly;

    /// <summary>
    /// PROPFIND property that carries the content hash (§3.3) in Clark notation, e.g. <c>{DAV:}getetag</c> on Mail.ru
    /// (§4.4); found by the probe. <c>null</c>: the remote hash is unknown.
    /// </summary>
    public string? RemoteHashProperty { get; set; }

    public double MaxRequestsPerSecond { get; set; } = 5;

    /// <summary>Listings in flight during a remote crawl (§4.3, §11), within <see cref="MaxRequestsPerSecond"/>.</summary>
    public int MaxParallelRequests { get; set; } = 4;

    public bool SetLocalMtimeFromRemote { get; set; } = true;

    /// <summary>
    /// Mail.ru endpoint only: list the remote tree in one request and check it for changes with the Mail.ru API (§4.7).
    /// Needs an app password with full access; without it (or when the API fails) everything goes over WebDAV.
    /// </summary>
    public bool UseMailRuApi { get; set; } = true;

    /// <summary>
    /// How often the remote change token is checked between polls (§8), when the remote has one (Mail.ru API); a change
    /// starts a cycle. Zero: off.
    /// </summary>
    public TimeSpan RemoteCheckInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Excluded folders, relative paths (any depth); top-level ones form the selective sync (§12.2).</summary>
    public List<string> ExcludedFolders { get; set; } = [];

    /// <summary>User glob patterns (§12.3). Without <c>/</c> matched against the name, otherwise against the path.</summary>
    public List<string> ExcludePatterns { get; set; } = [];

    /// <summary>Wait this long after the last local change of a burst before a cycle (§8).</summary>
    public TimeSpan LocalQuietPeriod { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Run a cycle at most this long after the first local change of a burst, even if changes continue (§8).</summary>
    public TimeSpan MaxBatchDelay { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Interval of cycles without local changes, to pick up remote changes (§8; a full crawl for now, §4.3).</summary>
    public TimeSpan RemotePollInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Rolling verify (§5.3): every synced local file is hashed again once per this interval, a slice per cycle, to
    /// find content changes that kept size and mtime. <c>null</c>: off.
    /// </summary>
    public TimeSpan? LocalVerifyInterval { get; set; }

    /// <summary>Read rate limit for hashing local files, MB/s (§11); 0 = unlimited.</summary>
    public double HashLimitMBps { get; set; }

    /// <summary>"Approve and remember" rules (§10): matching ops run without asking in every mode except union mode.</summary>
    public List<AutoApproveRule> AutoApprove { get; set; } = [];

    public string RemoteRootNormalized => "/" + RemoteRoot.Trim('/');
}

/// <summary>
/// Auto-approves ops of one type inside one folder (§10), e.g. downloads into <c>Camera Uploads</c>. Guard trips
/// (§9) and union-mode plans (§7.2) still need a human.
/// </summary>
public sealed class AutoApproveRule
{
    public OpType Type { get; set; }

    /// <summary>Relative folder path; the rule covers its whole subtree. Empty: the whole pair.</summary>
    public string Folder { get; set; } = string.Empty;

    /// <summary>Also cover uploads/downloads that replace an existing item (otherwise only new items).</summary>
    public bool Overwrite { get; set; }

    public bool Matches(SyncOp op)
    {
        // A download/upload rule also creates the folders it needs on the target side.
        var typeMatches = op.Type == Type ||
                          (Type == OpType.Download && op.Type == OpType.MkdirLocal) ||
                          (Type == OpType.Upload && op.Type == OpType.MkdirRemote);
        if (!typeMatches || op.IsBaseOnly || (op.Overwrite && !Overwrite))
        {
            return false;
        }

        // Moves must stay inside the folder; a conflict's NewKey is its copy next to the item.
        var folder = PathKey.FromPath(Folder.Replace('\\', '/').Trim('/'));
        return PathKey.IsSelfOrUnder(op.Key, folder) &&
               (op.NewKey is null || op.Type == OpType.Conflict || PathKey.IsSelfOrUnder(op.NewKey, folder));
    }

    public override string ToString() =>
        $"{Type} in {(Folder.Length == 0 ? "<whole pair>" : "'" + Folder + "'")}{(Overwrite ? ", incl. overwrites" : string.Empty)}";
}

public sealed class AppSettings
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public List<PairSettings> Pairs { get; set; } = [];

    /// <summary>Tray app: run in Windows background mode (low CPU, disk and memory priority, §11).</summary>
    public bool LowPriority { get; set; } = true;

    /// <summary>Tray app: hard cap of its CPU use, percent of the whole machine; 0 = no cap (§11).</summary>
    public int CpuLimitPercent { get; set; }

    /// <summary>Tray app: how often to look for a new release (§14.3).</summary>
    public Updates.UpdateCheckInterval UpdateCheck { get; set; } = Updates.UpdateCheckInterval.Daily;

    /// <summary>Tray app: when the last update check succeeded.</summary>
    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>Tray app: the newest version already announced, so each release is announced once.</summary>
    public string? NotifiedVersion { get; set; }

    public static AppSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new AppSettings();
        }

        return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    public PairSettings? Find(string idOrName) =>
        Pairs.FirstOrDefault(p => string.Equals(p.Id, idOrName, StringComparison.OrdinalIgnoreCase)) ??
        Pairs.FirstOrDefault(p => string.Equals(p.Name, idOrName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>App data next to the executable (§15.1).</summary>
public sealed class AppPaths(string dataDir)
{
    public string DataDir { get; } = Path.GetFullPath(dataDir);

    public string SettingsFile => Path.Combine(DataDir, "settings.json");

    public string LogsDir => Path.Combine(DataDir, "logs");

    public static AppPaths Default()
    {
        var overridden = Environment.GetEnvironmentVariable("WDS_DATA_DIR");
        return new AppPaths(string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(AppContext.BaseDirectory, "data")
            : overridden);
    }
}
