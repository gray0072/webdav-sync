using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WebDavSync.App;

/// <summary>
/// CPU and I/O limits of the tray process (§11): Windows background mode (low CPU, disk and memory priority, so a
/// scan yields to everything else) and a hard CPU cap through a job object.
/// </summary>
internal static partial class ProcessLimits
{
    private const uint ProcessModeBackgroundBegin = 0x00100000;
    private const uint ProcessModeBackgroundEnd = 0x00200000;
    private const int JobObjectCpuRateControlInformation = 15;
    private const uint CpuRateControlEnable = 0x1;
    private const uint CpuRateControlHardCap = 0x4;

    private static IntPtr _job;

    public static void SetBackground(bool on)
    {
        if (!SetPriorityClass(GetCurrentProcess(), on ? ProcessModeBackgroundBegin : ProcessModeBackgroundEnd))
        {
            var error = Marshal.GetLastPInvokeError();

            // ERROR_PROCESS_MODE_ALREADY_BACKGROUND / _NOT_IN_BACKGROUND: already as requested.
            if (error is not (402 or 403))
            {
                throw new Win32Exception(error);
            }
        }
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetPriorityClass(IntPtr process, uint priorityClass);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr CreateJobObjectW(IntPtr attributes, IntPtr name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(IntPtr job, int infoClass, ref CpuRateControl info, uint length);
}
