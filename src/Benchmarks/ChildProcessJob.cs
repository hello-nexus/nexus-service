using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Nexus.Service.Benchmarks;

/// <summary>Windows job object with KILL_ON_JOB_CLOSE: assigned processes die with this service, including on taskkill /F.</summary>
internal static partial class ChildProcessJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    // Never closed explicitly: the OS closes it when this process exits, which is what kills the members.
    private static readonly Lazy<nint> Job = new(Create);

    /// <summary>Puts <paramref name="process"/> in the job; a no-op off Windows or when the job could not be created.</summary>
    public static void Assign(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var job = Job.Value;
        if (job == 0)
        {
            return;
        }
        try
        {
            if (!AssignProcessToJobObject(job, process.Handle))
            {
                Console.Error.WriteLine($"[benchmark] job assign failed for pid {process.Id}: win32 {Marshal.GetLastPInvokeError()}");
            }
        }
        catch (InvalidOperationException)
        {
            // No process associated with this Process object.
        }
    }

    /// <summary>True when <paramref name="process"/> is a member of this service's job; false off Windows.</summary>
    internal static bool Contains(Process process)
    {
        if (!OperatingSystem.IsWindows() || Job.Value == 0)
        {
            return false;
        }
        return IsProcessInJob(process.Handle, Job.Value, out var inJob) && inJob;
    }

    private static nint Create()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }
        var job = CreateJobObjectW(0, 0);
        if (job == 0)
        {
            return 0;
        }
        var info = new JobObjectExtendedLimitInformationData();
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformationData>()))
        {
            CloseHandle(job);
            return 0;
        }
        return job;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationData
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    private static partial nint CreateJobObjectW(nint jobAttributes, nint name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int infoClass, ref JobObjectExtendedLimitInformationData info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInJob(nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
