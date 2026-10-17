using Microsoft.Extensions.Logging;
using WebDavSync.Core.Config;
using WebDavSync.Core.Model;

namespace WebDavSync.Core.Local;

public enum LocalHashMode
{
    /// <summary>Never hash (only the base cache is used).</summary>
    None,

    /// <summary>Hash files changed vs base and files the caller asks for (e.g. present on the remote).</summary>
    Needed,

    /// <summary>Hash every file not covered by the base cache.</summary>
    All,
}

public sealed record ScanProgress(int Files, int Folders, long HashedBytes);

/// <summary>
/// Local snapshot (§5): FileId, size, mtime and content hash. Hashes are reused from the base when
/// (FileId, size, mtime) are unchanged (§3.3).
/// </summary>
public sealed class LocalScanner(SyncFilter filter, ILogger logger)
{
    /// <summary>Keys hashed again even when the base cache matches (the rolling verify, §5.3).</summary>
    public Func<string, bool>? ForceHash { get; init; }

    /// <summary>Read rate limit for hashing (§11).</summary>
    public ByteRateLimiter? HashLimiter { get; init; }

    private static readonly EnumerationOptions ListOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    public async Task<LocalSnapshot> ScanAsync(
        string root,
        IReadOnlyDictionary<string, BaseEntry> baseEntries,
        LocalHashMode hashMode,
        Func<string, bool>? needsHash = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Local root '{root}' does not exist.");
        }

        var entries = new Dictionary<string, LocalEntry>(StringComparer.Ordinal);
        var listed = new HashSet<string>(StringComparer.Ordinal);
        var skipped = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new List<string>();
        var stack = new Stack<string>();
        stack.Push(string.Empty);
        int files = 0, folders = 0;
        long hashedBytes = 0;

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var relDir = stack.Pop();
            var children = new List<FileSystemInfo>();
            try
            {
                children.AddRange(new DirectoryInfo(PathKey.ToLocalFull(root, relDir)).EnumerateFileSystemInfos("*", ListOptions));
                listed.Add(PathKey.FromPath(relDir));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Cannot list local folder '{relDir}': {ex.Message}");
                logger.LogWarning(ex, "Cannot list local folder {Folder}", relDir);
            }

            foreach (var info in children)
            {
                var relPath = PathKey.Combine(relDir, info.Name);
                var key = PathKey.FromPath(relPath);
                switch (filter.Check(key, info.Name))
                {
                    case FilterVerdict.Ignore:
                        continue;
                    case FilterVerdict.Exclude:
                        skipped.Add(key);
                        continue;
                }

                var unsupported = LocalFs.UnsupportedReason(info.Attributes);
                if (unsupported is not null)
                {
                    skipped.Add(key);
                    warnings.Add($"Not synced: '{relPath}' — {unsupported}.");
                    continue;
                }

                var fullPath = info.FullName;
                var fileId = LocalFs.GetFileId(fullPath);
                if (info is DirectoryInfo)
                {
                    folders++;
                    entries[key] = new LocalEntry(key, relPath, true, 0, default, fileId);
                    stack.Push(relPath);
                    continue;
                }

                files++;
                var file = (FileInfo)info;
                var entry = new LocalEntry(key, relPath, false, file.Length, file.LastWriteTimeUtc, fileId);
                baseEntries.TryGetValue(key, out var b);
                var force = ForceHash?.Invoke(key) == true;
                if (!force && b is { IsDir: false, LocalHash: not null } && b.LocalSize == entry.Size &&
                    Mtime.Equal(entry.MtimeUtc, b.LocalMtimeUtc) &&
                    (b.LocalFileId is null || fileId is null || b.LocalFileId == fileId))
                {
                    entry = entry with { Hash = b.LocalHash };
                }
                else if (force || hashMode == LocalHashMode.All ||
                         (hashMode == LocalHashMode.Needed && (b is not null || needsHash?.Invoke(key) == true)))
                {
                    try
                    {
                        entry = entry with { Hash = await ContentHash.ComputeFileAsync(fullPath, ct, HashLimiter).ConfigureAwait(false) };
                        hashedBytes += entry.Size;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        entry = entry with { Locked = true };
                        warnings.Add($"Cannot read '{relPath}' (will retry next cycle): {ex.Message}");
                    }
                }

                entries[key] = entry;
                if ((files & 0x3FF) == 0)
                {
                    progress?.Report(new ScanProgress(files, folders, hashedBytes));
                }
            }
        }

        progress?.Report(new ScanProgress(files, folders, hashedBytes));
        logger.LogInformation(
            "Local scan of {Root}: {Files} files, {Folders} folders, {Skipped} skipped, hashed {Bytes} bytes",
            root, files, folders, skipped.Count, hashedBytes);
        return new LocalSnapshot(entries, listed, skipped, warnings);
    }
}
