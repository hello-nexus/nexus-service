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
    private const int ErrorAccessDenied = 5;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    /// <summary>Windows found for <paramref name="pid"/> and how many took the end message. The query goes to every window, then the end message to every window whatever the query answered, as iCUE's measured exit needs.</summary>
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
        foreach (var hwnd in windows)
        {
            // A timeout or a destroyed window still counts as delivered: only access denied (a higher-integrity target) is a refusal.
            if (SendMessageTimeout(hwnd, WmEndSession, (IntPtr)1, (IntPtr)EndSessionCloseApp, SmtoAbortIfHung, SendTimeoutMs, out _) != IntPtr.Zero
                || Marshal.GetLastWin32Error() != ErrorAccessDenied)
            {
                accepted++;
            }
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
