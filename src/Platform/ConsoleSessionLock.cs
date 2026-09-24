#if WINDOWS
using System;
using System.Runtime.InteropServices;

namespace Nexus.Service.Platform;

/// <summary>
/// Lock state of the interactive console session, readable from session 0.
/// <see cref="Displays.DdcGate"/>'s equivalent asks about the calling process's
/// OWN session, which only answers inside the user-session helper; the service
/// sits in session 0, where that query is always "unlocked".
/// </summary>
internal static class ConsoleSessionLock
{
    /// <summary>
    /// True/false when the console session's lock state could be read, null when
    /// it could not (no console session, or the query failed). Callers must treat
    /// null as "no answer" rather than as unlocked.
    /// </summary>
    internal static bool? IsLocked()
    {
        try
        {
            var sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == 0xFFFFFFFF) return null;
            if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, WTSSessionInfoEx, out var buf, out var len)
                || buf == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                if (len < (uint)Marshal.SizeOf<WTSINFOEX_PREFIX>()) return null;
                var info = Marshal.PtrToStructure<WTSINFOEX_PREFIX>(buf);
                if (info.Level != 1) return null;
                if (info.SessionFlags == WTS_SESSIONSTATE_LOCK) return true;
                if (info.SessionFlags == WTS_SESSIONSTATE_UNLOCK) return false;
                return null;
            }
            finally { WTSFreeMemory(buf); }
        }
        catch
        {
            return null;
        }
    }

    // WTS_INFO_CLASS.WTSSessionInfoEx
    private const int WTSSessionInfoEx = 25;
    // WTSINFOEX_LEVEL1_W.SessionFlags, Win10+ semantics (the documented Win7
    // lock/unlock inversion predates the 19041 floor). UNKNOWN is 0xFFFFFFFF.
    private const int WTS_SESSIONSTATE_LOCK = 0;
    private const int WTS_SESSIONSTATE_UNLOCK = 1;

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformationW(
        IntPtr hServer, uint sessionId, int infoClass, out IntPtr buffer, out uint bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    // Leading fields of WTSINFOEXW (x64): Level at 0; the Data union starts at 8
    // because WTSINFOEX_LEVEL1_W carries LARGE_INTEGER members (8-byte
    // alignment). Only the fields ahead of the union's WCHAR arrays are mapped.
    [StructLayout(LayoutKind.Explicit)]
    private struct WTSINFOEX_PREFIX
    {
        [FieldOffset(0)] public uint Level;
        [FieldOffset(8)] public uint SessionId;
        [FieldOffset(12)] public int SessionState;
        [FieldOffset(16)] public int SessionFlags;
    }
}
#endif
