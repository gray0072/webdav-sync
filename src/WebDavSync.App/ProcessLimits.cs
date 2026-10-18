using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WebDavSync.App;

/// <summary>
/// CPU and I/O limits of the tray process (§11): low priority for the sync work only, and a hard CPU cap through a job
/// object. Windows background mode is not used: it also covers the UI thread and empties the working set, so the tray
/// menu answered a click only after its code was paged back in at very low disk priority.
/// </summary>
internal static partial class ProcessLimits
{
    private const uint NormalPriorityClass = 0x00000020;
    private const uint BelowNormalPriorityClass = 0x00004000;
    private const int ThreadPriorityNormal = 0;
    private const int ThreadPriorityHighest = 2;
    private const int JobObjectCpuRateControlInformation = 15;
    private const uint CpuRateControlEnable = 0x1;
    private const uint CpuRateControlHardCap = 0x4;

    private static IntPtr _job;

    /// <summary>
    /// Low priority on: the process runs below normal (worker threads at 6, under ordinary programs at 8), the calling
    /// UI thread is raised back to 8, and bulk file I/O gets very low disk priority (<see cref="Core.Local.LocalFs.LowIoPriority"/>).
    /// Call on the UI thread.
    /// </summary>
    public static void SetBackground(bool on)
    {
        if (!SetPriorityClass(GetCurrentProcess(), on ? BelowNormalPriorityClass : NormalPriorityClass) ||
            !SetThreadPriority(GetCurrentThread(), on ? ThreadPriorityHighest : ThreadPriorityNormal))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        Core.Local.LocalFs.LowIoPriority = on;
    }

    /// <summary>Caps the process at <paramref name="percent"/> of the total CPU time; 0 removes the cap.</summary>
    public static void SetCpuLimit(int percent)
    {
        if (_job == IntPtr.Zero)
        {
            if (percent <= 0)
            {
                return;
            }

            _job = CreateJobObjectW(IntPtr.Zero, IntPtr.Zero);
            if (_job == IntPtr.Zero || !AssignProcessToJobObject(_job, GetCurrentProcess()))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }

        var info = new CpuRateControl
        {
            ControlFlags = percent > 0 ? CpuRateControlEnable | CpuRateControlHardCap : 0,
            CpuRate = (uint)(Math.Clamp(percent, 1, 100) * 100),
        };
        if (!SetInformationJobObject(_job, JobObjectCpuRateControlInformation, ref info, (uint)Marshal.SizeOf<CpuRateControl>()))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CpuRateControl
    {
        public uint ControlFlags;
        public uint CpuRate;
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentThread();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetPriorityClass(IntPtr process, uint priorityClass);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadPriority(IntPtr thread, int priority);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr CreateJobObjectW(IntPtr attributes, IntPtr name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(IntPtr job, int infoClass, ref CpuRateControl info, uint length);
}
