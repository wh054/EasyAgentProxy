using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AppProxyHelper;

/// <summary>
/// Wraps a Windows Job Object configured with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>.
/// When this object is disposed (or the process exits), the OS automatically terminates
/// all processes assigned to the job, preventing orphan child processes.
/// </summary>
internal sealed class ChildProcessJob : IDisposable
{
    private nint _handle;

    private ChildProcessJob(nint handle)
    {
        _handle = handle;
    }

    /// <summary>
    /// Creates a new Job Object with <c>KILL_ON_JOB_CLOSE</c> enabled.
    /// Returns <c>null</c> if the Job Object could not be created (e.g. on non-Windows platforms).
    /// </summary>
    public static ChildProcessJob? Create()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        nint handle;
        try
        {
            handle = CreateJobObject(IntPtr.Zero, null);
        }
        catch
        {
            return null;
        }

        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            }
        };

        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        if (!SetInformationJobObject(handle, JobObjectInfoClass.ExtendedLimitInformation, ref info, size))
        {
            CloseHandle(handle);
            return null;
        }

        return new ChildProcessJob(handle);
    }

    /// <summary>
    /// Assigns a process to this Job Object. If the assignment fails, the process
    /// continues to run normally (graceful degradation).
    /// </summary>
    public void AssignProcess(Process process)
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            AssignProcessToJobObject(_handle, process.Handle);
        }
        catch
        {
            // Graceful degradation: the child will not be auto-killed on parent exit,
            // but the rest of the application continues to work.
        }
    }

    public void Dispose()
    {
        var handle = _handle;
        if (handle != IntPtr.Zero)
        {
            _handle = IntPtr.Zero;
            CloseHandle(handle);
        }
    }

    // --- Win32 P/Invoke ---

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    private enum JobObjectInfoClass
    {
        ExtendedLimitInformation = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        nint hJob,
        JobObjectInfoClass jobObjectInfoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo,
        int cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);
}
