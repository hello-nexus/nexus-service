using System.Runtime.InteropServices;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// Windows-only PawnIO driver detection. Checks two things:
///
/// 1. IsInstalled - checks the kernel service registry key at
///    HKLM\SYSTEM\CurrentControlSet\Services\PawnIO. Returns true if the
///    PawnIO kernel driver service is registered (regardless of whether
///    it was installed by us via PawnIoInstaller or by the user via
///    PawnIO_setup.exe). Uses Microsoft.Win32.Registry which is AOT-safe.
///
/// 2. IsOpen - true when a \Device\PawnIO object exists (the driver is
///    loaded and running). Delegates to PawnIoInstaller.IsDeviceAvailable,
///    the probe the installer's self-heal path keys on.
///
/// On non-Windows platforms both properties return false.
/// </summary>
public sealed class PawnIoProvider : IPawnIoProvider
{
    public bool IsInstalled
    {
        get
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\PawnIO");
                return key is not null;
            }
            catch { return false; }
        }
    }

    public bool IsOpen => PawnIoInstaller.IsDeviceAvailable();
}
