using System.Text.Json;

namespace WebDavSync.Core.Config;

/// <summary>
/// <c>pair.json</c> — the root marker, stored in <c>&lt;local root&gt;\.webdavsync\</c> and in
/// <c>&lt;remote root&gt;/.webdavsync/</c> (§9 #1, §15.2).
/// </summary>
public sealed class PairMarker
{
    public string PairId { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public string RemoteUrl { get; set; } = string.Empty;

    public string RemoteRoot { get; set; } = string.Empty;

    public uint? VolumeSerial { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this, AppSettings.JsonOptions);

    public static PairMarker? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<PairMarker>(json, AppSettings.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Paths inside <c>&lt;local root&gt;\.webdavsync\</c> (§15.2).</summary>
public sealed class StatePaths(string localRoot)
{
    public const string DirName = ".webdavsync";

    public string LocalRoot { get; } = Path.GetFullPath(localRoot);

    public string Dir => Path.Combine(LocalRoot, DirName);

    public string PairFile => Path.Combine(Dir, "pair.json");

    public string StateDb => Path.Combine(Dir, "state.db");

    public string BackupDir => Path.Combine(Dir, "backup");

    public string TmpDir => Path.Combine(Dir, "tmp");

    public string TrashDir => Path.Combine(Dir, "trash");

    public string EngineLock => Path.Combine(Dir, "engine.lock");

    public static string RemoteMarkerDir(string remoteRoot) => remoteRoot.TrimEnd('/') + "/" + DirName;

    public static string RemoteMarkerFile(string remoteRoot) => RemoteMarkerDir(remoteRoot) + "/pair.json";

    public PairMarker? ReadMarker() =>
        File.Exists(PairFile) ? PairMarker.FromJson(File.ReadAllText(PairFile)) : null;

    public void EnsureDir()
    {
        var info = Directory.CreateDirectory(Dir);
        if (OperatingSystem.IsWindows() && !info.Attributes.HasFlag(FileAttributes.Hidden))
        {
            info.Attributes |= FileAttributes.Hidden;
        }
    }

    public void WriteMarker(PairMarker marker)
    {
        EnsureDir();
        File.WriteAllText(PairFile, marker.ToJson());
    }
}
