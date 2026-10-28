#if WINDOWS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Nexus.Service.Activity;
using Nexus.Service.Helper.Domains;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper;

/// <summary>
/// Helper-side visible-window poller. Session 0 (the LocalSystem service)
/// cannot enumerate the interactive desktop's windows, so this runs here and
/// pushes the set of process ids owning a visible top-level window on a
/// jittered timer, matching ScreenTimePoller's cadence. Consumed by
/// WindowsWindowSetProvider for the processes frame's isApp classification.
/// Sends only on change, plus once on reconnect: a service restart wipes
/// WindowsWindowSetProvider's in-memory snapshot, but this poller's own
/// _lastSent survives the pipe reconnect that follows, so an unchanged
/// window set would otherwise never get resent to repopulate it.
///
/// Set NEXUS_WINDOW_DIAG=1 to emit a [window-diag] ServiceLog line per
/// enumerated window (process identity, the IsCountableWindow inputs, and
/// the classification result) - off by default, see WindowDiagnostics.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowSetPoller : IDisposable
{
    /// <summary>
    /// 5 s, matching MonitoringEventCollector's own poll - it is the only
    /// always-on consumer, so anything faster was oversampling. The other
    /// consumer (the processes frame's App/Background split) is demand-gated
    /// UI, where a newly launched app settling into its group a few seconds
    /// later is not perceptible.
    /// </summary>
    private const int PeriodMs = 5000;

    private readonly HelperOutbound _outbound;
    private readonly JitteredPeriodicTimer _timer;
    private readonly bool _diagEnabled;
    private HashSet<int> _lastSent = new();
    private bool _wasConnected;

    /// <summary>
    /// Set from the service over windowSet.wanted. Defaults true so a helper
    /// talking to a service that never sends it keeps the old behaviour rather
    /// than silently losing the App/Background split.
    /// </summary>
    private volatile bool _wanted = true;

    // Pinned callback - EnumWindows requires a delegate that isn't GC'd
    // mid-enumeration (same guard as TrayIcon._pinnedEnumProc). The timer
    // only re-arms after Poll returns, so one instance field is safe: no two
    // enumerations for this poller are ever in flight together.
    private EnumWindowsProc? _pinnedEnumProc;

    /// <summary>Windows walked by the last EnumWindows pass - the size of the
    /// per-window Win32 work the slow-pass line is reporting on.</summary>
    private int _enumerated;
    private int _titleReads;

    private readonly WindowCloakCache _cloakCache;
    private readonly WindowCloakWatcher _cloakWatcher;

    public WindowSetPoller(HelperOutbound outbound)
    {
        _outbound = outbound;
        _diagEnabled = WindowDiagnostics.IsEnabled(Environment.GetEnvironmentVariable(WindowDiagnostics.EnvVarName));
        _cloakCache = new WindowCloakCache(hwnd => IsCloaked(new IntPtr(hwnd)));
        _cloakWatcher = new WindowCloakWatcher(_cloakCache);
        _timer = new JitteredPeriodicTimer(periodMs: PeriodMs, jitterMs: 500, Poll);
    }

    /// <summary>
    /// Service-driven demand. Clearing _lastSent on the way back up matters:
    /// the service's snapshot went stale while we were off, and the change-only
    /// send would otherwise never resend an unchanged window set.
    /// </summary>
    public void SetWanted(bool wanted)
    {
        if (wanted && !_wanted) _lastSent = new HashSet<int>();
        _wanted = wanted;
    }

    private void Poll()
    {
        if (!_wanted) return;
        if (HelperPollerDiagnostics.IsDisabled(HelperPollerDiagnostics.WindowSet)) return;
        try
        {
            var connected = _outbound.IsConnected;
            var justReconnected = connected && !_wasConnected;
            _wasConnected = connected;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var current = SnapshotWindowedPids();
            sw.Stop();
            if (sw.ElapsedMilliseconds >= HelperPollerDiagnostics.SlowPassMs
                && HelperPollerDiagnostics.TryFormatSlowPass(
                    HelperPollerDiagnostics.WindowSet, sw.Elapsed.TotalMilliseconds, _enumerated, out var slow))
            {
                Nexus.Service.Platform.HelperLog.Write(
                    $"{slow} dwm={_cloakCache.Reads} titles={_titleReads} cached={_cloakCache.Count}");
            }
            if (!justReconnected && current.SetEquals(_lastSent)) return;

            _lastSent = current;
            _ = _outbound.SendAsync(
                "windowSet.snapshot",
                new WindowSetSnapshotPayload { Pids = current.ToList() },
                AppJsonContext.Default.WindowSetSnapshotPayload);
        }
        catch { }
    }

    private HashSet<int> SnapshotWindowedPids()
    {
        var pids = new HashSet<int>();
        var enumerated = 0;
        _titleReads = 0;
        _cloakCache.BeginPass();

        // Computed once per poll, not per window: the bounding box of every
        // monitor, for the off-screen-window exclusion.
        var virtualLeft = GetSystemMetrics(SmXvirtualscreen);
        var virtualTop = GetSystemMetrics(SmYvirtualscreen);
        var virtualRight = virtualLeft + GetSystemMetrics(SmCxvirtualscreen);
        var virtualBottom = virtualTop + GetSystemMetrics(SmCyvirtualscreen);

        // Also once per poll: whatever the user has focused right now is a
        // real app window by definition, so it overrides the cloak/title
        // heuristics below (see WindowClassification.IsCountableWindow) -
        // unless it is the desktop or taskbar itself, which also take OS
        // foreground focus (clicking the desktop, Win+D, an empty taskbar
        // click) without being an app the user is running.
        var foregroundHwnd = GetForegroundWindow();
        if (IsShellChromeWindow(foregroundHwnd))
        {
            foregroundHwnd = IntPtr.Zero;
        }

        _pinnedEnumProc = (hwnd, _) =>
        {
            enumerated++;
            // Cheap, local win32k reads first. These alone decide the outcome
            // for the large majority of windows, and IsCountableWindow's own
            // short-circuit order is what makes that safe to rely on.
            var isForegroundWindow = hwnd == foregroundHwnd;
            var owner = GetWindow(hwnd, GwOwner);
            var isToolWindow = (GetWindowLong(hwnd, GwlExstyle) & WsExToolwindow) != 0;
            var isVisible = IsWindowVisible(hwnd);
            var hasOnScreenBounds = GetWindowRect(hwnd, out var rect) &&
                WindowClassification.HasOnScreenBounds(
                    rect.Left, rect.Top, rect.Right, rect.Bottom,
                    virtualLeft, virtualTop, virtualRight, virtualBottom);

            // DwmGetWindowAttribute is an RPC into dwm.exe, so cloak state comes from
            // WindowCloakCache. The title read is InternalGetWindowText, which
            // reads the kernel-stored caption; GetWindowTextLength sends
            // WM_GETTEXTLENGTH to the owning UI thread and stalls on a hung one.
            // Both run only when IsCountableWindow would reach the terms they feed.
            var isCloaked = false;
            var coversMonitor = false;
            var titleLength = 0;
            var hasTitle = false;
            if (_diagEnabled
                || (owner == IntPtr.Zero && hasOnScreenBounds && !isForegroundWindow
                    && isVisible && !isToolWindow))
            {
                isCloaked = _cloakCache.IsCloaked(hwnd.ToInt64(), rect.Left, rect.Top, rect.Right, rect.Bottom);
                // Only load-bearing through IsCountableWindow's (!isCloaked ||
                // coversMonitor) check - skip the extra monitor lookup when
                // isCloaked is false and the value cannot change the outcome.
                coversMonitor = isCloaked &&
                    TryGetMonitorBounds(hwnd, out var monitorRect) &&
                    WindowClassification.CoversMonitor(
                        rect.Left, rect.Top, rect.Right, rect.Bottom,
                        monitorRect.Left, monitorRect.Top, monitorRect.Right, monitorRect.Bottom);
                titleLength = InternalGetWindowText(hwnd, _titleBuffer, _titleBuffer.Length);
                _titleReads++;
                hasTitle = titleLength > 0;
            }

            var isCountable = WindowClassification.IsCountableWindow(
                isVisible, owner, isToolWindow, isCloaked, hasTitle, hasOnScreenBounds,
                coversMonitor, titleBlockedByUipi: false, isForegroundWindow);

            if (isCountable || _diagEnabled)
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                if (isCountable && pid != 0)
                {
                    pids.Add((int)pid);
                }
                if (_diagEnabled)
                {
                    LogDiagnostic(
                        (int)pid, isVisible, owner, isToolWindow, isCloaked,
                        hasTitle, titleLength, 0, hasOnScreenBounds,
                        coversMonitor, isForegroundWindow, isCountable);
                }
            }
            return true;
        };
        EnumWindows(_pinnedEnumProc, IntPtr.Zero);
        _cloakCache.EndPass();
        _enumerated = enumerated;
        if (_diagEnabled)
        {
            Nexus.Service.Platform.HelperLog.Write(
                $"[window-diag] pass items={enumerated} dwm={_cloakCache.Reads} titles={_titleReads} pids={string.Join(",", pids.Order())}");
        }
        return pids;
    }

    // DWMWA_CLOAKED reports non-zero for a window Explorer keeps alive but
    // never shows (background UWP frames, DWM-hidden helper windows) -
    // IsWindowVisible alone still reports these as visible.
    private static bool IsCloaked(IntPtr hwnd)
    {
        var hr = DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int));
        return hr == 0 && cloaked != 0;
    }

    // Delegates the class-name match to WindowClassification.IsShellChromeClassName
    // (platform-neutral, unit tested) - this method only extracts the class
    // name via Win32.
    private static bool IsShellChromeWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }
        var sb = new StringBuilder(64);
        if (GetClassNameW(hwnd, sb, sb.Capacity) <= 0)
        {
            return false;
        }
        return WindowClassification.IsShellChromeClassName(sb.ToString());
    }

    // A cloaked window that fully covers its own monitor is an exclusive-
    // fullscreen app (DWM cloaks those too), not a hidden background window.
    private static bool TryGetMonitorBounds(IntPtr hwnd, out Rect monitorRect)
    {
        var hMonitor = MonitorFromWindow(hwnd, MonitorDefaulttonearest);
        if (hMonitor == IntPtr.Zero)
        {
            monitorRect = default;
            return false;
        }
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(hMonitor, ref info))
        {
            monitorRect = default;
            return false;
        }
        monitorRect = info.Monitor;
        return true;
    }

    private static void LogDiagnostic(
        int pid,
        bool isVisible,
        IntPtr owner,
        bool isToolWindow,
        bool isCloaked,
        bool hasTitle,
        int titleLength,
        int titleReadError,
        bool hasOnScreenBounds,
        bool coversMonitor,
        bool isForegroundWindow,
        bool isCountable)
    {
        var processName = "?";
        try
        {
            using var process = Process.GetProcessById(pid);
            processName = process.ProcessName;
        }
        catch
        {
            // Process exited, or its name is inaccessible, between
            // GetWindowThreadProcessId and this lookup - keep the placeholder.
        }
        // ServiceLog is a no-op in this process: the helper short-circuits in
        // CommandLineEntry.TryEarlyExit, long before Program.cs initializes it.
        Nexus.Service.Platform.HelperLog.Write(WindowDiagnostics.FormatLine(
            pid, processName, isVisible, owner != IntPtr.Zero, isToolWindow, isCloaked,
            hasTitle, titleLength, titleReadError, hasOnScreenBounds, coversMonitor, isForegroundWindow, isCountable));
    }

    public void Dispose()
    {
        _timer.Dispose();
        _cloakWatcher.Dispose();
    }

    private const uint GwOwner = 4;
    private const int GwlExstyle = -20;
    private const int WsExToolwindow = 0x00000080;
    private const int DwmwaCloaked = 14;
    private const int SmXvirtualscreen = 76;
    private const int SmYvirtualscreen = 77;
    private const int SmCxvirtualscreen = 78;
    private const int SmCyvirtualscreen = 79;
    private const uint MonitorDefaulttonearest = 2;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    // Only the poll thread calls this, one pass at a time, so one buffer is safe.
    private readonly char[] _titleBuffer = new char[256];

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int InternalGetWindowText(IntPtr hWnd, [Out] char[] lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MonitorInfo lpmi);
}
#endif
