using System;
using System.Runtime.InteropServices;

namespace Nexus.Service.Lighting.Rgb;

/// <summary>
/// Asks the OpenRGB daemon to shut down on its own so controller destructors
/// run (some drivers hand the device back to its firmware there). Unix sends
/// SIGTERM, which the daemon's server loop handles; Windows signals the named
/// event the daemon waits on, since it runs without a console.
/// </summary>
internal static partial class OpenRgbGracefulStop
{
    private const int SigTerm = 15;
    private const uint EventModifyState = 0x0002;

    /// <summary>True when the request was delivered; the caller still waits for exit and falls back to a kill.</summary>
    internal static bool TryRequest(int pid)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var handle = OpenEventW(EventModifyState, 0, $"Local\\OpenRGBHeadlessStop-{pid}");
                if (handle == 0)
                {
                    return false;
                }
                try
                { return SetEvent(handle); }
                finally { CloseHandle(handle); }
            }

            if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            {
                return kill(pid, SigTerm) == 0;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // No native entry point on this platform: the caller kills.
        }

        return false;
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int kill(int pid, int sig);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint OpenEventW(uint desiredAccess, int inheritHandle, string name);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetEvent(nint handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
