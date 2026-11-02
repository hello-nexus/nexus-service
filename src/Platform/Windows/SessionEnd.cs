#if WINDOWS
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Service.Platform.Windows;

/// <summary>
/// Asks a process to close the way a user logoff does: WM_QUERYENDSESSION, then
/// WM_ENDSESSION, with ENDSESSION_CLOSEAPP, to each of its top-level windows (hidden
/// ones included). Only reaches windows on the caller's own desktop, so it runs in
/// the user-session helper.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SessionEnd
{
    private const uint WmQueryEndSession = 0x0011;
    private const uint WmEndSession = 0x0016;
    private const uint EndSessionCloseApp = 0x1;
    private const uint SmtoAbortIfHung = 0x2;
    private const uint SendTimeoutMs = 3000;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    /// <summary>Windows found for <paramref name="pid"/> and how many accepted the end message; a window of a process running at a higher integrity level refuses it. The query goes to every window first and the end message to every window after it, whatever the query answered: the measured iCUE exit needs both, in that order.</summary>
    public static (int Windows, int Accepted) SendToProcess(int pid)
    {
        var windows = new List<IntPtr>();
        EnumWindowsProc proc = (hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var windowPid);
            if (windowPid == (uint)pid) windows.Add(hwnd);
            return true;
        };
        EnumWindows(proc, IntPtr.Zero);
        GC.KeepAlive(proc);
        foreach (var hwnd in windows)
        {
            SendMessageTimeout(hwnd, WmQueryEndSession, IntPtr.Zero, (IntPtr)EndSessionCloseApp, SmtoAbortIfHung, SendTimeoutMs, out _);
        }
        var accepted = 0;
        var lastError = 0;
        foreach (var hwnd in windows)
        {
            if (SendMessageTimeout(hwnd, WmEndSession, (IntPtr)1, (IntPtr)EndSessionCloseApp, SmtoAbortIfHung, SendTimeoutMs, out _) != IntPtr.Zero) accepted++;
            else lastError = Marshal.GetLastWin32Error();
        }
        if (accepted < windows.Count)
        {
            Nexus.Service.Platform.ServiceLog.Info($"[conflicts] session-end pid={pid}: {accepted} of {windows.Count} windows accepted (last error {lastError})");
        }
        return (windows.Count, accepted);
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);
}
#endif
