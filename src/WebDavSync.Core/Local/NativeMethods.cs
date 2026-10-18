using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace WebDavSync.Core.Local;

[SupportedOSPlatform("windows")]
internal static partial class NativeMethods
{
    private const uint FileReadAttributes = 0x80;
    private const uint FileShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    private const int FileIoPriorityHintInfo = 12;
    private const int IoPriorityHintVeryLow = 0;

    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;
    private const ushort FofWantNukeWarning = 0x4000;

    public readonly record struct FileIdentity(ulong FileId, uint VolumeSerial, uint Links);

    /// <summary>NTFS file reference number (the legacy <c>inode</c>), volume serial and hard link count.</summary>
    public static FileIdentity? GetFileIdentity(string fullPath)
    {
        using var handle = CreateFileW(
            LongPath(fullPath), FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info))
        {
            return null;
        }

        return new FileIdentity(((ulong)info.FileIndexHigh << 32) | info.FileIndexLow, info.VolumeSerialNumber, info.NumberOfLinks);
    }

    /// <summary>Very low I/O priority for one open file (<c>FileIoPriorityHintInfo</c>); best effort.</summary>
    public static void SetLowIoPriority(SafeFileHandle handle)
    {
        var hint = IoPriorityHintVeryLow;
        _ = SetFileInformationByHandle(handle, FileIoPriorityHintInfo, ref hint, sizeof(int));
    }

    /// <summary>Sends a file or folder to the Recycle Bin. Returns false when the shell refused.</summary>
    public static bool SendToRecycleBin(string fullPath)
    {
        if (fullPath.Length >= 260)
        {
            return false;
        }

        var op = new ShFileOpStruct
        {
            Func = FoDelete,
            From = fullPath + "\0",
            Flags = (ushort)(FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi | FofWantNukeWarning),
        };
        var result = SHFileOperationW(ref op);
        return result == 0 && !op.AnyOperationsAborted && !File.Exists(fullPath) && !Directory.Exists(fullPath);
    }

    private static string LongPath(string fullPath) =>
        fullPath.Length < 260 || fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? fullPath
            : fullPath.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + fullPath[2..]
                : @"\\?\" + fullPath;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref int info, uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition,
        uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

#pragma warning disable SYSLIB1054 // SHFILEOPSTRUCT has string fields; classic marshalling is simpler here.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref ShFileOpStruct fileOp);
#pragma warning restore SYSLIB1054

    // FILETIME is two DWORDs (4-byte aligned): Pack = 4 keeps the native 52-byte layout.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr Hwnd;
        public uint Func;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string From;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? ProgressTitle;
    }
}
