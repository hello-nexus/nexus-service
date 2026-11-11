using System;
using System.Runtime.InteropServices;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// Milliseconds since the OS booted, including time spent asleep. Environment.TickCount64
/// stopped counting sleep on Windows in .NET 11, so anything that derives a boot time from
/// it (reboot detection, the once-per-boot id) must read this instead or a resume would
/// read as a new boot. Other platforms keep the runtime value.
/// </summary>
internal static partial class SystemUptime
{
    public static long BootElapsedMs => OperatingSystem.IsWindows() ? (long)GetTickCount64() : Environment.TickCount64;

    [LibraryImport("kernel32.dll")]
    private static partial ulong GetTickCount64();
}
