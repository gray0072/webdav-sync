using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WebDavSync.Core.Config;

/// <summary>
/// App passwords in the Windows Credential Manager (§15.1): generic credentials <c>WebDavSync/&lt;pair id&gt;</c>, stored
/// for the current user on this machine, outside the (portable) app folder. Passwords are never logged.
/// </summary>
public sealed partial class SecretStore(string prefix = "WebDavSync")
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const string NeedsWindows = "The Credential Manager needs Windows.";

    /// <summary>Target name in the Credential Manager, e.g. <c>WebDavSync/e0450b9c…</c>.</summary>
    public string Target(string pairId) => $"{prefix}/{pairId.ToLowerInvariant()}";

    public string? Get(string pairId)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(NeedsWindows);
        }

        if (!CredReadW(Target(pairId), CredTypeGeneric, 0, out var handle))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == ErrorNotFound ? null : throw new Win32Exception(error, $"Cannot read the password of pair {pairId}.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(handle);
            return credential.CredentialBlobSize == 0
                ? null
                : Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(handle);
        }
    }

    /// <param name="user">Shown in the Credential Manager next to the target (the login).</param>
    public void Set(string pairId, string password, string? user = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(NeedsWindows);
        }

        var target = Marshal.StringToHGlobalUni(Target(pairId));
        var userName = Marshal.StringToHGlobalUni(user ?? string.Empty);
        var blob = Marshal.StringToHGlobalUni(password);
        try
        {
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)(password.Length * 2),
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = userName,
            };
            if (!CredWriteW(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Cannot store the password of pair {pairId}.");
            }
        }
        finally
        {
            Marshal.ZeroFreeGlobalAllocUnicode(blob);
            Marshal.FreeHGlobal(target);
            Marshal.FreeHGlobal(userName);
        }
    }

    public bool Remove(string pairId)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(NeedsWindows);
        }

        if (CredDeleteW(Target(pairId), CredTypeGeneric, 0))
        {
            return true;
        }

        var error = Marshal.GetLastPInvokeError();
        return error == ErrorNotFound ? false : throw new Win32Exception(error, $"Cannot delete the password of pair {pairId}.");
    }

    /// <summary><c>CREDENTIALW</c>; <c>LastWritten</c> (FILETIME) as two DWORDs keeps the native 4-byte alignment.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public uint LastWrittenLow;
        public uint LastWrittenHigh;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredReadW(string target, uint type, uint flags, out IntPtr credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWriteW(ref Credential credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDeleteW(string target, uint type, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(IntPtr buffer);
}
