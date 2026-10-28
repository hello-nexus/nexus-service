#if WINDOWS
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Nexus.Service.Helper.Domains;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper;

/// <summary>
/// Helper-side foreground-window poller. Polls GetForegroundWindow every
/// 2s (jittered), tracks the active app, and emits envelopes to the
/// service on focus boundaries:
///
///   <c>screenTime.focus</c>  - current focus changed (or no focus).
///   <c>screenTime.session</c> - the prior focus session ended; the service
///                               persists it via IScreenTimeStore.
///
/// Runs in the user session: the LocalSystem service in Session 0 cannot see
/// the foreground window. 3-minute idle clip, lock-free apply path; the sink
/// is HelperOutbound.SendAsync.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScreenTimePoller : IDisposable
{
    private const long IdleThresholdMs = 3 * 60 * 1000;

    private readonly HelperOutbound _outbound;
    private readonly JitteredPeriodicTimer _timer;
    private readonly object _lock = new();

    private string _currentApp = "";
    private int _currentPid;
    private long _sessionStartUtcMs;
    private long _lastPollUtcMs;
    private bool _wasConnected;
    private string? _currentExePath;
    private int _currentWinW;
    private int _currentWinH;
    private string? _currentMonitorDevice;

    public ScreenTimePoller(HelperOutbound outbound)
    {
        _outbound = outbound;
        _lastPollUtcMs = NowUtcMs();
        _timer = new JitteredPeriodicTimer(periodMs: 2000, jitterMs: 200, Poll);
    }

    private void Poll()
    {
        if (HelperPollerDiagnostics.IsDisabled(HelperPollerDiagnostics.ScreenTime)) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var identityRead = false;
        try
        {
            // Pipe reconnect: the helper-side cached focus survives across
            // reconnects, but the service's in-memory state was wiped on
            // restart and the change-only emit won't fire until the user
            // switches windows. Force a re-broadcast so downstream
            // consumers (e.g. the FPS provider) recover.
            var connected = _outbound.IsConnected;
            var justReconnected = connected && !_wasConnected;
            _wasConnected = connected;
            if (justReconnected) RebroadcastCurrentFocus();

            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return;

            int currentPid;
            lock (_lock) { currentPid = _currentPid; }
            if ((int)pid == currentPid)
            {
                ApplyFocus("", (int)pid, null, 0, 0, null);
                return;
            }

            identityRead = true;
            var exePath = TryGetExePath(pid);
            var appName = exePath is null ? "" : ScreenTimeProcessName.ProcessNameFromPath(exePath);
            if (appName.Length == 0)
            {
                // Path unreadable: Process.ProcessName also resolves names the
                // image-path query cannot, at the cost of a process snapshot.
                try
                {
                    using var proc = Process.GetProcessById((int)pid);
                    appName = proc.ProcessName;
                }
                catch { return; }
            }

            var (winW, winH, monitorDevice) = ReadWindowGeometry(hwnd);

            ApplyFocus(appName, (int)pid, exePath, winW, winH, monitorDevice);
        }
        catch { }
        finally
        {
            sw.Stop();
            if (sw.ElapsedMilliseconds >= HelperPollerDiagnostics.SlowPassMs
                && HelperPollerDiagnostics.TryFormatSlowPass(
                    HelperPollerDiagnostics.ScreenTime, sw.Elapsed.TotalMilliseconds, 1, out var slow,
                    identityRead ? "identity read" : "same pid, no reads"))
            {
                Nexus.Service.Platform.HelperLog.Write(slow);
            }
        }
    }

    private void RebroadcastCurrentFocus()
    {
        string app;
        int pid;
        long started;
        string? exePath;
        int winW;
        int winH;
        string? monitorDevice;
        lock (_lock)
        {
            if (string.IsNullOrEmpty(_currentApp)) return;
            app = _currentApp;
            pid = _currentPid;
            started = _sessionStartUtcMs;
            exePath = _currentExePath;
            winW = _currentWinW;
            winH = _currentWinH;
            monitorDevice = _currentMonitorDevice;
        }
        _ = _outbound.SendAsync(
            "screenTime.focus",
            new ScreenTimeFocusPayload
            {
                App = app,
                Pid = pid,
                StartedUtcMs = started,
                ExePath = exePath,
                WinW = winW,
                WinH = winH,
                MonitorDevice = monitorDevice,
            },
            AppJsonContext.Default.ScreenTimeFocusPayload);
    }

    private void ApplyFocus(string appName, int pid, string? exePath, int winW, int winH, string? monitorDevice)
    {
        // appName, exePath, winW, winH and monitorDevice are read only when the pid
        // changed: Poll's same-pid fast path passes placeholders.
        FireMode mode;
        string priorApp = "";
        long priorStart = 0;
        long priorEnd = 0;
        int newPid = 0;
        long newStart = 0;

        lock (_lock)
        {
            var now = NowUtcMs();
            var idleGap = now - _lastPollUtcMs;

            if (pid != _currentPid && !string.IsNullOrEmpty(appName))
            {
                priorApp = _currentApp;
                priorStart = _sessionStartUtcMs;
                priorEnd = idleGap > IdleThresholdMs ? _lastPollUtcMs : now;
                _currentApp = appName;
                _currentPid = pid;
                _sessionStartUtcMs = now;
                _currentExePath = exePath;
                _currentWinW = winW;
                _currentWinH = winH;
                _currentMonitorDevice = monitorDevice;
                newPid = pid;
                newStart = now;
                mode = string.IsNullOrEmpty(priorApp) ? FireMode.FocusOnly : FireMode.SessionAndFocus;
            }
            else if (idleGap > IdleThresholdMs && !string.IsNullOrEmpty(_currentApp))
            {
                priorApp = _currentApp;
                priorStart = _sessionStartUtcMs;
                priorEnd = _lastPollUtcMs;
                _sessionStartUtcMs = now;
                newPid = _currentPid;
                newStart = now;
                mode = FireMode.SessionOnly;
            }
            else
            {
                mode = FireMode.None;
            }

            _lastPollUtcMs = now;
        }

        if (mode == FireMode.None) return;

        // Fire outside the lock - the pipe write is async and might block
        // on a flush; we don't want to hold the focus lock through that.
        if (mode is FireMode.SessionOnly or FireMode.SessionAndFocus
            && !string.IsNullOrEmpty(priorApp))
        {
            _ = _outbound.SendAsync(
                "screenTime.session",
                new ScreenTimeSessionPayload
                {
                    App = priorApp,
                    StartedUtcMs = priorStart,
                    EndedUtcMs = priorEnd,
                },
                AppJsonContext.Default.ScreenTimeSessionPayload);
        }

        if (mode is FireMode.FocusOnly or FireMode.SessionAndFocus)
        {
            _ = _outbound.SendAsync(
                "screenTime.focus",
                new ScreenTimeFocusPayload
                {
                    App = appName,
                    Pid = newPid,
                    StartedUtcMs = newStart,
                    ExePath = exePath,
                    WinW = winW,
                    WinH = winH,
                    MonitorDevice = monitorDevice,
                },
                AppJsonContext.Default.ScreenTimeFocusPayload);
        }
    }

    private enum FireMode { None, FocusOnly, SessionOnly, SessionAndFocus }

    public void Dispose()
    {
        _timer.Dispose();

        // Flush the in-flight session on shutdown so the user doesn't lose
        // an open VSCode/Chrome session whenever they log off.
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(_currentApp))
            {
                _ = _outbound.SendAsync(
                    "screenTime.session",
                    new ScreenTimeSessionPayload
                    {
                        App = _currentApp,
                        StartedUtcMs = _sessionStartUtcMs,
                        EndedUtcMs = NowUtcMs(),
                    },
                    AppJsonContext.Default.ScreenTimeSessionPayload);
            }
        }
    }

    private static long NowUtcMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // PROCESS_QUERY_LIMITED_INFORMATION (not PROCESS_QUERY_INFORMATION) works
    // across integrity levels without PROCESS_VM_READ, so it can resolve the
    // path of an elevated or EAC-protected title this helper cannot otherwise
    // introspect. Null on failure rather than throwing - denial is expected
    // for some titles, not exceptional.
    internal static string? TryGetExePath(uint pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new System.Text.StringBuilder(1024);
            var size = (uint)buffer.Capacity;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString(0, (int)size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // The helper sets no process-wide DPI awareness, so without this
    // GetClientRect/GetMonitorInfoW return 96-DPI-virtualized units on a
    // scaled display (see WindowsDisplayTopologyProvider.TrySetPerMonitorAwareV2).
    private static (int Width, int Height, string? MonitorDevice) ReadWindowGeometry(IntPtr hwnd)
    {
        var previousContext = TrySetPerMonitorAwareV2();
        try
        {
            var (winW, winH) = GetClientRect(hwnd, out var rect) ? (rect.Right - rect.Left, rect.Bottom - rect.Top) : (0, 0);
            return (winW, winH, ResolveMonitorDevice(hwnd));
        }
        finally
        {
            RestoreThreadDpiContext(previousContext);
        }
    }

    private static IntPtr TrySetPerMonitorAwareV2()
    {
        try { return SetThreadDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2); }
        catch { return IntPtr.Zero; }
    }

    private static void RestoreThreadDpiContext(IntPtr previous)
    {
        if (previous == IntPtr.Zero) return;
        try { SetThreadDpiAwarenessContext(previous); } catch { }
    }

    // Resolves to the same stable id space as RawDisplayInfo.Id
    // (WindowsDisplayTopologyProvider uses the identical ResolveIdentity call
    // on its own GetMonitorInfoW szDevice), so FpsSessionRecorder can join a
    // session's monitor directly against a display topology query.
    private static string? ResolveMonitorDevice(IntPtr hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return null;

        var info = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfoW(monitor, ref info)) return null;

        return Nexus.Service.Platform.Displays.WindowsDisplayIdentity.ResolveIdentity(info.szDevice).Id;
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint MonitorDefaultToNearest = 2;
    private const int CchDeviceName = 32;
    private static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new(-4);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, System.Text.StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchDeviceName)] public string szDevice;
    }
}
#endif
