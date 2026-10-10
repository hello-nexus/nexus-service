#if WINDOWS
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Service.Platform.Windows;

/// <summary>
/// The active power scheme's "turn off display after" setting for the power
/// source in use. The service reads it from Session 0; the scheme is
/// machine-wide, so that sees the same value the user does.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsScreenOffTimeout
{
    private static readonly Guid VideoSubgroup = new("7516b95f-f776-4464-8c53-06167f40cc99");
    private static readonly Guid VideoIdle = new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    /// <summary>Seconds; 0 is Never; null when the scheme cannot be read.</summary>
    public static int? Read()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var schemePtr) != 0 || schemePtr == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var scheme = Marshal.PtrToStructure<Guid>(schemePtr);
            var subgroup = VideoSubgroup;
            var setting = VideoIdle;
            // 255 is "unknown", which is what a desktop reports: treat as mains.
            var onBattery = GetSystemPowerStatus(out var status) && status.ACLineStatus == 0;
            var rc = onBattery
                ? PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out var dc)
                : PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out dc);
            if (rc != 0)
            {
                return null;
            }
            return (int)Math.Min(dc, int.MaxValue);
        }
        finally
        {
            LocalFree(schemePtr);
        }
    }
}
#endif
