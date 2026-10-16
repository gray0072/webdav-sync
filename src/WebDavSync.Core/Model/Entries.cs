namespace WebDavSync.Core.Model;

public interface ISnapshotEntry
{
    string Key { get; }

    string Path { get; }

    bool IsDir { get; }
}

/// <summary>A local item. <see cref="Hash"/> is null when not computed; <see cref="Locked"/> when it could not be read.</summary>
public sealed record LocalEntry(
    string Key,
    string Path,
    bool IsDir,
    long Size,
    DateTime MtimeUtc,
    ulong? FileId,
    ContentHash? Hash = null,
    bool Locked = false) : ISnapshotEntry;

/// <summary>A remote item as returned by PROPFIND.</summary>
public sealed record RemoteEntry(
    string Key,
    string Path,
    bool IsDir,
    long Size,
    DateTime? MtimeUtc,
    string? ETag,
    ContentHash? Hash = null) : ISnapshotEntry;

/// <summary>
/// Last state known to be identical on both sides (§1.3). Paths are kept per side because names may
/// differ in case or Unicode normalization. <see cref="RemoteConfirmed"/> is false for entries imported from
/// the legacy client until a remote crawl adopts the remote identity (§19.5 #5).
/// </summary>
public sealed record BaseEntry(
    string Key,
    bool IsDir,
    string LocalPath,
    string RemotePath,
    ulong? LocalFileId,
    long LocalSize,
    DateTime? LocalMtimeUtc,
    ContentHash? LocalHash,
    string? RemoteETag,
    long RemoteSize,
    DateTime? RemoteMtimeUtc,
    ContentHash? RemoteHash,
    bool RemoteConfirmed,
    DateTime SyncedAtUtc)
{
    public ContentHash? Hash => LocalHash ?? RemoteHash;

    public static BaseEntry From(string key, LocalEntry local, RemoteEntry remote, ContentHash? hash, DateTime now) =>
        new(
            key,
            local.IsDir,
            local.Path,
            remote.Path,
            local.FileId,
            local.IsDir ? 0 : local.Size,
            local.IsDir ? null : local.MtimeUtc,
            local.IsDir ? null : hash ?? local.Hash,
            remote.ETag,
            remote.IsDir ? 0 : remote.Size,
            remote.MtimeUtc,
            remote.IsDir ? null : remote.Hash ?? hash ?? local.Hash,
            true,
            now);

    /// <summary>Moves a self-or-descendant entry of <paramref name="root"/> to a new key and paths.</summary>
    public BaseEntry Rebase(BaseEntry root, string newKey, string newLocalPath, string newRemotePath) =>
        this with
        {
            Key = PathKey.Rebase(Key, root.Key, newKey),
            LocalPath = PathKey.Rebase(LocalPath, root.LocalPath, newLocalPath),
            RemotePath = PathKey.Rebase(RemotePath, root.RemotePath, newRemotePath),
        };
}

public static class Mtime
{
    /// <summary>
    /// Exact equality, or equality to the second when one value has no sub-second part
    /// (legacy import and WebDAV store whole seconds).
    /// </summary>
    public static bool Equal(DateTime? value, DateTime? baseValue)
    {
        if (value is null || baseValue is null)
        {
            return value is null && baseValue is null;
        }

        var a = value.Value.ToUniversalTime().Ticks;
        var b = baseValue.Value.ToUniversalTime().Ticks;
        if (a == b)
        {
            return true;
        }

        return (b % TimeSpan.TicksPerSecond == 0 || a % TimeSpan.TicksPerSecond == 0) &&
               a / TimeSpan.TicksPerSecond == b / TimeSpan.TicksPerSecond;
    }

    public static DateTime FromUnixSeconds(long seconds) => DateTime.UnixEpoch.AddSeconds(seconds);
}
