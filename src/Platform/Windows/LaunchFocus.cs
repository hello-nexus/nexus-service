#if WINDOWS
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;

namespace Nexus.Service.Platform.Windows;

/// <summary>
/// Brings the window of an app the helper just launched to the front. A process
/// started from the helper has no right to take focus, so Windows opens it
/// behind the focused app. Only a window whose process started after
/// <see cref="Arm"/> is touched, once, and only while focus has not moved.
/// </summary>
[SupportedOSPlatform("windows")]
public static class LaunchFocus
{
    private static readonly TimeSpan ArmedFor = TimeSpan.FromSeconds(5);

    private const int HSHELL_WINDOWCREATED = 1;
    private const int HSHELL_WINDOWACTIVATED = 4;
    private const int HSHELL_RUDEAPPACTIVATED = 0x8004;
    private const int HSHELL_FLASH = 0x8006;

    private static readonly object s_lock = new();
    private static WndProcDelegate? s_proc;
    private static uint s_shellHookMsg;
    private static Action<IntPtr, IntPtr>? s_focusElevated;
    private static long s_armedUntil;
    private static DateTime s_armedAt;
    private static IntPtr s_focusedAtArm;

    /// <summary>Registers the shell hook; <paramref name="focusElevated"/> gets (window, window focused at arm) when the helper is refused.</summary>
    public static void Start(Action<IntPtr, IntPtr> focusElevated)
    {
        s_focusElevated = focusElevated;
        new Thread(Run) { IsBackground = true, Name = "launch-focus" }.Start();
    }

    /// <summary>Call immediately before starting a user-requested launch.</summary>
    public static void Arm()
    {
        lock (s_lock)
        {
            s_armedAt = DateTime.Now;
            s_armedUntil = Environment.TickCount64 + (long)ArmedFor.TotalMilliseconds;
            s_focusedAtArm = GetForegroundWindow();
        }
    }

    private static void Run()
    {
        try
        {
            s_proc = WndProc;
            var cls = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = s_proc,
                lpszClassName = "NexusLaunchFocusWnd",
                hInstance = GetModuleHandle(null),
            };
            if (RegisterClassEx(ref cls) == 0)
            {
                return;
            }

            var hwnd = CreateWindowEx(0, cls.lpszClassName, "", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, cls.hInstance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            s_shellHookMsg = RegisterWindowMessage("SHELLHOOK");
            if (!RegisterShellHookWindow(hwnd))
            {
                return;
            }

            while (GetMessage(out var msg, IntPtr.Zero, 0, 0))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            HelperLog.Write($"[launch-focus] hook thread failed: {ex.Message}");
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == s_shellHookMsg && s_shellHookMsg != 0)
        {
            try { OnShellEvent((int)wParam.ToInt64(), lParam); }
            catch { /* best-effort */ }
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static void OnShellEvent(int code, IntPtr window)
    {
        IntPtr focusedAtArm;
        lock (s_lock)
        {
            if (s_armedUntil == 0)
            {
                return;
            }
            if (Environment.TickCount64 > s_armedUntil)
            {
                s_armedUntil = 0;
                return;
            }

            // Focus moved on (the user, or the app taking it itself): stand down.
            if (code is HSHELL_WINDOWACTIVATED or HSHELL_RUDEAPPACTIVATED)
            {
                if (window != s_focusedAtArm)
                {
                    s_armedUntil = 0;
                }
                return;
            }
            // A refused focus request flashes before the window is shown; the shown event follows.
            if (code is not (HSHELL_WINDOWCREATED or HSHELL_FLASH) || !IsWindowVisible(window)
                || IsStoreAppContent(window) || !StartedSinceArm(window))
            {
                return;
            }

            s_armedUntil = 0;
            focusedAtArm = s_focusedAtArm;
            if (GetForegroundWindow() != focusedAtArm)
            {
                return;
            }
        }

        ForegroundNudge.TryForeground(window);
        // Refused when the window is elevated (Task Manager, admin apps): UIPI blocks this medium-integrity helper.
        if (GetForegroundWindow() != window)
        {
            HelperLog.Write("[launch-focus] focus refused, asking the service to focus it elevated");
            s_focusElevated?.Invoke(window, focusedAtArm);
        }
        else
        {
            HelperLog.Write("[launch-focus] focused the launched window");
        }
    }

    // A Store app's content window moves into an ApplicationFrameHost frame; focusing it leaves nothing focused.
    private static bool IsStoreAppContent(IntPtr window)
    {
        var cls = new StringBuilder(64);
        return GetClassName(window, cls, cls.Capacity) > 0 && cls.ToString() == "Windows.UI.Core.CoreWindow";
    }

    private static bool StartedSinceArm(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var pid);
        if (pid == 0)
        {
            return false;
        }
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            return proc.StartTime >= s_armedAt;
        }
        catch
        {
            return false;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public int style;
        [MarshalAs(UnmanagedType.FunctionPtr)] public WndProcDelegate lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public int ptX, ptY;
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX cls);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32")] private static extern bool RegisterShellHookWindow(IntPtr hwnd);
    [DllImport("user32")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32")] private static extern bool GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int maxCount);
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
#endif
