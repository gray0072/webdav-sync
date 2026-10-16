using System.Text.Json;
using System.Text.Json.Serialization;

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

    public double MaxRequestsPerSecond { get; set; } = 5;

    public bool SetLocalMtimeFromRemote { get; set; } = true;

    /// <summary>Excluded folders, relative paths (any depth); top-level ones form the selective sync (§12.2).</summary>
    public List<string> ExcludedFolders { get; set; } = [];

    /// <summary>User glob patterns (§12.3). Without <c>/</c> matched against the name, otherwise against the path.</summary>
    public List<string> ExcludePatterns { get; set; } = [];

    public string RemoteRootNormalized => "/" + RemoteRoot.Trim('/');
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
