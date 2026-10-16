using WebDavSync.Core.Config;
using WebDavSync.Core.Model;

namespace WebDavSync.Core.Local;

/// <summary>Small file-system helpers shared by the scanner and the executor.</summary>
public static class LocalFs
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x40000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;

    public static ulong? GetFileId(string fullPath) =>
        OperatingSystem.IsWindows() ? NativeMethods.GetFileIdentity(fullPath)?.FileId : null;

    public static uint? GetVolumeSerial(string fullPath) =>
        OperatingSystem.IsWindows() ? NativeMethods.GetFileIdentity(fullPath)?.VolumeSerial : null;

    /// <summary>Reason the item is not synced (§12.4), or null.</summary>
    public static string? UnsupportedReason(FileAttributes attributes)
    {
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return "symlink, junction or placeholder (reparse point)";
        }

        if (attributes.HasFlag(FileAttributes.Offline) || (attributes & (RecallOnOpen | RecallOnDataAccess)) != 0)
        {
            return "offline or cloud placeholder file";
        }

        return null;
    }

    public static LocalEntry? Stat(string root, string relativePath)
    {
        var full = PathKey.ToLocalFull(root, relativePath);
        FileSystemInfo info = new FileInfo(full);
        if (!info.Exists)
        {
            info = new DirectoryInfo(full);
            if (!info.Exists)
            {
                return null;
            }
        }

        var isDir = info is DirectoryInfo;
        return new LocalEntry(
            PathKey.FromPath(relativePath),
            relativePath,
            isDir,
            isDir ? 0 : ((FileInfo)info).Length,
            isDir ? default : info.LastWriteTimeUtc,
            GetFileId(full));
    }

    /// <summary>True when a local file still has the size and mtime seen at plan time.</summary>
    public static bool SameFile(LocalEntry? actual, LocalEntry? expected)
    {
        if (actual is null || expected is null)
        {
            return actual is null && expected is null;
        }

        if (actual.IsDir || expected.IsDir)
        {
            return actual.IsDir == expected.IsDir;
        }

        return actual.Size == expected.Size && actual.MtimeUtc == expected.MtimeUtc;
    }

    public static string NewTempPath(StatePaths state, string opId)
    {
        Directory.CreateDirectory(state.TmpDir);
        return Path.Combine(state.TmpDir, opId + ".part");
    }

    public static void CleanTemp(StatePaths state)
    {
        if (!Directory.Exists(state.TmpDir))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(state.TmpDir, "*.part"))
        {
            TryDelete(file);
        }
    }

    public static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static bool IsDiskFull(IOException ex) =>
        (ex.HResult & 0xFFFF) is 0x70 or 0x27;

    public static bool IsSharingViolation(IOException ex) =>
        (ex.HResult & 0xFFFF) is 0x20 or 0x21;
}

/// <summary>Local deletes and overwrites: Recycle Bin, or <c>.webdavsync\trash\&lt;date&gt;\</c> as a fallback (§9 #5).</summary>
public sealed class LocalTrash(StatePaths state, LocalDeleteTarget target)
{
    public string Trash(string relativePath)
    {
        var full = PathKey.ToLocalFull(state.LocalRoot, relativePath);
        if (target == LocalDeleteTarget.RecycleBin && OperatingSystem.IsWindows() && NativeMethods.SendToRecycleBin(full))
        {
            return "Recycle Bin";
        }

        var destination = PathKey.ToLocalFull(
            Path.Combine(state.TrashDir, DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
            relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        destination = Unique(destination);
        if (Directory.Exists(full))
        {
            Directory.Move(full, destination);
        }
        else
        {
            File.Move(full, destination);
        }

        return destination;
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return path;
        }

        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
