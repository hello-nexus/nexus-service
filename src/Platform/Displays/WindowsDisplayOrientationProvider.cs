#if WINDOWS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Nexus.Service.Peripherals.Hyte.Y70Display;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Rotates the HYTE Y70 panel via <c>ChangeDisplaySettingsEx</c>. Runs inside
/// the user-session helper so the change takes effect on the user's desktop.
/// Identifies the Y70 by the same set of panel-controller hardware names
/// nexus-overlay uses (<see cref="Y70DisplayProtocol.DdcPanelHardwareNames"/>),
/// matched as a substring of the monitor's PnP DeviceID.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDisplayOrientationProvider : IDisplayOrientationProvider
{

    public (bool Ok, string Error) SetY70Orientation(string orientation)
    {
        var device = FindY70AdapterDevice();
        if (string.IsNullOrEmpty(device))
        {
            // Not an error: the Y70 simply is not attached. The persisted
            // orientation will be re-applied when it next connects.
            return (true, "y70 panel not attached");
        }
        // No cover: the Y70 kiosk reconciles its bounds through a slower,
        // separate path (two service HTTP round-trips, not the in-process
        // refit promoted-monitor kiosks use - see nexus-overlay
        // PanelKioskWindow's WM_DISPLAYCHANGE handling), so the hold would not
        // span its repaint and a black cover would just add its own visible
        // flash.
        return ApplyToAdapter(device, orientation, coverColorHex: null);
    }

    public (bool Ok, string Error) SetDisplayOrientation(string displayId, string orientation, string coverColorHex)
    {
        if (string.IsNullOrEmpty(displayId)) return (false, "missing display id");
        foreach (var adapterName in EnumerateMonitorAdapters())
        {
            var (id, _, _, _, _) = WindowsDisplayIdentity.ResolveIdentity(adapterName);
            if (string.Equals(id, displayId, StringComparison.Ordinal))
            {
                return ApplyToAdapter(adapterName, orientation, coverColorHex);
            }
        }
        // Unlike the Y70 path, the caller targeted a specific monitor.
        return (false, "display not found");
    }

    /// <summary>
    /// Shared ChangeDisplaySettingsEx core for both rotation paths.
    /// <paramref name="coverColorHex"/> null disables the pre-rotation cover
    /// entirely (the Y70 path); a non-null string (possibly empty) attempts
    /// it, with empty meaning "no themed colour, use opaque black".
    /// </summary>
    private static (bool Ok, string Error) ApplyToAdapter(string device, string orientation, string? coverColorHex)
    {
        if (!TryParseOrientation(orientation, out var dmdo))
        {
            return (false, $"unknown orientation '{orientation}'");
        }

        var devMode = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettingsW(device, ENUM_CURRENT_SETTINGS, ref devMode))
        {
            return (false, "EnumDisplaySettings failed");
        }
        var fromOrientation = devMode.dmDisplayOrientation;
        if (fromOrientation == dmdo) return (true, $"already {orientation}");

        // Rotating between landscape <-> portrait flips the active resolution
        // axes. Failing to swap dmPelsWidth/dmPelsHeight makes Windows reject
        // the call with DISP_CHANGE_BADMODE.
        var fromPortrait = fromOrientation == DMDO_90 || fromOrientation == DMDO_270;
        var toPortrait = dmdo == DMDO_90 || dmdo == DMDO_270;
        var swapsAxes = fromPortrait != toPortrait;
        var oldWidth = devMode.dmPelsWidth;
        var oldHeight = devMode.dmPelsHeight;
        if (swapsAxes)
        {
            (devMode.dmPelsWidth, devMode.dmPelsHeight) = (devMode.dmPelsHeight, devMode.dmPelsWidth);
        }
        devMode.dmDisplayOrientation = dmdo;
        devMode.dmFields |= DM_DISPLAYORIENTATION | DM_PELSWIDTH | DM_PELSHEIGHT;

        // Only an axis swap exposes new desktop area at the monitor's origin;
        // a 180-degree flip keeps the same rect, so there is nothing to cover.
        RotationCover? cover = null;
        Log($"apply {device} {fromOrientation}->{dmdo} swapsAxes={swapsAxes} cover={(coverColorHex is null ? "disabled" : "requested")}");
        if (swapsAxes && coverColorHex is not null)
        {
            cover = RotationCover.Start(
                device, devMode.dmPositionX, devMode.dmPositionY,
                oldWidth, oldHeight, devMode.dmPelsWidth, devMode.dmPelsHeight,
                coverColorHex);
        }
        try
        {
            var rc = ChangeDisplaySettingsExW(device, ref devMode, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
            // Nothing rotated, so the cover has nothing to hide and would just
            // blank the panel for the rest of its hold.
            if (rc != DISP_CHANGE_SUCCESSFUL) cover?.Abort();
            return rc switch
            {
                DISP_CHANGE_SUCCESSFUL => (true, $"applied {orientation} from={fromOrientation} set={devMode.dmPelsWidth}x{devMode.dmPelsHeight}"),
                DISP_CHANGE_RESTART    => (true, "restart required"),
                DISP_CHANGE_BADMODE    => (false, "mode not supported"),
                DISP_CHANGE_BADFLAGS   => (false, "bad flags"),
                DISP_CHANGE_BADPARAM   => (false, "bad parameter"),
                DISP_CHANGE_FAILED     => (false, "ChangeDisplaySettings failed"),
                DISP_CHANGE_NOTUPDATED => (false, "registry not updated"),
                _                       => (false, $"unknown DISP_CHANGE {rc}"),
            };
        }
        catch
        {
            cover?.Abort();
            throw;
        }
    }

    /// <summary>
    /// How long the cover stays up across a rotation. Every signal Windows
    /// offers for "the rotation happened" fires immediately - the
    /// ChangeDisplaySettingsEx return, EnumDisplaySettings, the window's clip
    /// box, and WM_DISPLAYCHANGE were all measured at &lt;=31ms on the bench
    /// (Xeneon Edge, T1) - but the screen itself does not carry the new mode
    /// until ~870ms, with the panel drawing its own content ~130ms after that.
    /// There is nothing to wait on, so this spans the measured worst case. It
    /// runs on the cover's own thread, so it does not delay the rotation call.
    /// </summary>
    private const int CoverHoldMs = 1300;

    /// <summary>Repaint cadence while the cover is held.</summary>
    private const int CoverRepaintIntervalMs = 33;

    /// <summary>
    /// Cap on waiting for the cover thread to get its window up before the
    /// rotation goes ahead. Creation is a handful of Win32 calls (bench: the
    /// cover was up ~5ms after the rotation request reached the helper), so
    /// this only bounds a thread that never got scheduled.
    /// </summary>
    private const int CoverStartTimeoutMs = 500;

    /// <summary>
    /// Owns the cover window on a thread of its own for a whole rotation.
    /// Two constraints force this: a window can only be destroyed by the thread
    /// that created it, so the hold and the teardown have to share a thread;
    /// and the hold must not run on the caller's, because that caller is the
    /// helper RPC the rotation route blocks on before broadcasting the panel's
    /// new orientation (and it is the helper's only pipe reader, so every other
    /// helper RPC queues behind it too).
    /// </summary>
    private sealed class RotationCover
    {
        // Not disposed: on the start-timeout path the cover thread still has a
        // Set() to make, and disposing under it would throw on that thread.
        private readonly ManualResetEventSlim _up = new(false);
        private volatile bool _abort;
        private volatile bool _created;

        /// <summary>
        /// Returns a cover whose window is already up, so the rotation cannot
        /// outrun it; null if the window was not created, or was not up within
        /// <see cref="CoverStartTimeoutMs"/>. Either way the cover tears itself
        /// down - a null return means the caller has nothing left to abort.
        /// </summary>
        public static RotationCover? Start(
            string device, int x, int y, uint oldWidth, uint oldHeight, uint newWidth, uint newHeight, string colorHex)
        {
            var cover = new RotationCover();
            new Thread(() => cover.Run(device, x, y, oldWidth, oldHeight, newWidth, newHeight, colorHex))
            {
                IsBackground = true,
                Name = "nexus-rotation-cover",
            }.Start();
            if (!cover._up.Wait(CoverStartTimeoutMs))
            {
                // Abandoning it without this leaves a cover that comes up after
                // the rotation and blanks the panel for the whole hold, with no
                // handle left to stop it: the rotation goes uncovered AND the
                // panel goes dark, the inverse of the point of the cover.
                cover.Abort();
                Log($"cover skipped: window not up within {CoverStartTimeoutMs}ms");
                return null;
            }
            return cover._created ? cover : null;
        }

        /// <summary>Tears the cover down early; the rotation did not take.</summary>
        public void Abort() => _abort = true;

        private void Run(
            string device, int x, int y, uint oldWidth, uint oldHeight, uint newWidth, uint newHeight, string colorHex)
        {
            try
            {
                var hwnd = TryCreateRotationCover(device, x, y, oldWidth, oldHeight, newWidth, newHeight, colorHex);
                _created = hwnd != IntPtr.Zero;
                _up.Set();
                if (hwnd == IntPtr.Zero) return;
                try
                {
                    // Aborted before the hold means Start gave up waiting and the
                    // caller never took this cover; holding it would blank the
                    // panel behind the caller's back.
                    if (_abort)
                    {
                        Log("cover dropped: aborted before hold");
                        return;
                    }
                    var start = Environment.TickCount64;
                    long displayChangeAt = -1;
                    // Repaint on a cadence: the rotation reallocates the display
                    // surfaces partway through and discards whatever was painted
                    // before, so no single paint survives it.
                    while (!_abort && Environment.TickCount64 - start < CoverHoldMs)
                    {
                        PaintCover(hwnd);
                        if (displayChangeAt < 0 && DisplayChangeSeen(hwnd)) displayChangeAt = Environment.TickCount64 - start;
                        PumpFor(CoverRepaintIntervalMs);
                    }
                    Log($"cover held: {Environment.TickCount64 - start}ms displayChangeAt={displayChangeAt}ms abort={_abort}");
                }
                finally
                {
                    DestroyRotationCover(hwnd);
                }
            }
            catch (Exception ex)
            {
                // This is the thread's entry point, so an escape takes the whole
                // helper process down rather than failing one rotation.
                Log($"cover thread failed: {ex.GetType().Name} {ex.Message}");
            }
        }
    }

    /// <summary>True once the cover's WndProc has taken a WM_DISPLAYCHANGE.</summary>
    private static bool DisplayChangeSeen(IntPtr cover) => GetWindowLongPtrW(cover, DisplayChangeSlot) != IntPtr.Zero;

    private static bool TryParseOrientation(string value, out uint dmdo)
    {
        switch (value)
        {
            case "Landscape":         dmdo = DMDO_DEFAULT; return true;
            case "Portrait":          dmdo = DMDO_90;      return true;
            case "LandscapeFlipped":  dmdo = DMDO_180;     return true;
            case "PortraitFlipped":   dmdo = DMDO_270;     return true;
            default:                  dmdo = DMDO_DEFAULT; return false;
        }
    }

    /// <summary>
    /// Walks <c>EnumDisplayMonitors</c> + <c>EnumDisplayDevices</c> to find the
    /// adapter <c>\\.\DISPLAYn</c> hosting a HYTE panel. Mirrors
    /// <c>nexus-overlay/src/PanelDisplay.cs</c>. Calling
    /// <c>EnumDisplayDevicesW(null, ...)</c> to enumerate adapters is unreliable
    /// across CLR null-string marshaling paths; the EDM callback gives us a
    /// non-null <c>szDevice</c> for every active monitor.
    /// </summary>
    private static string FindY70AdapterDevice()
    {
        var monitors = EnumerateMonitorAdapters();
        foreach (var adapterName in monitors)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevicesW(adapterName, 0, ref dd, 0)) continue;
            var deviceId = dd.DeviceID ?? "";
            foreach (var name in Y70DisplayProtocol.DdcPanelHardwareNames)
            {
                if (deviceId.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return adapterName;
                }
            }
        }
        return "";
    }

    private static List<string> EnumerateMonitorAdapters()
    {
        var list = new List<string>();
        bool Cb(IntPtr hMonitor, IntPtr _, IntPtr __, IntPtr ___)
        {
            var info = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfoW(hMonitor, ref info) && !string.IsNullOrEmpty(info.szDevice))
            {
                list.Add(info.szDevice);
            }
            return true;
        }
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Cb, IntPtr.Zero);
        return list;
    }

    /// <summary>
    /// Shows a solid-colour, topmost, non-activating window over the union of
    /// <paramref name="device"/>'s pre- and post-rotation rect, so the strip
    /// ChangeDisplaySettingsEx exposes never shows raw desktop wallpaper.
    /// Skipped (returns <see cref="IntPtr.Zero"/>) whenever that union would
    /// spill onto a neighbouring monitor: a flash on this panel is the
    /// existing bug, a flash on the user's OTHER display would be worse.
    /// </summary>
    private static IntPtr TryCreateRotationCover(
        string device, int x, int y, uint oldWidth, uint oldHeight, uint newWidth, uint newHeight, string coverColorHex)
    {
        // Held so the catch can tear down a window this method created but has
        // not handed back yet; cleared once ownership passes to the caller.
        var created = IntPtr.Zero;
        try
        {
            var width = (int)Math.Max(oldWidth, newWidth);
            var height = (int)Math.Max(oldHeight, newHeight);
            if (UnionHitsAnotherMonitor(device, x, y, x + width, y + height))
            {
                Log($"cover skipped: union ({x},{y}) {width}x{height} hits another monitor");
                return IntPtr.Zero;
            }
            if (!EnsureCoverWindowClassRegistered())
            {
                Log($"cover skipped: RegisterClassEx failed err={Marshal.GetLastWin32Error()}");
                return IntPtr.Zero;
            }

            var hwnd = CreateWindowExW(
                WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                CoverWindowClassName, "", WS_POPUP,
                x, y, width, height,
                IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                Log($"cover skipped: CreateWindowEx failed err={Marshal.GetLastWin32Error()}");
                return IntPtr.Zero;
            }
            created = hwnd;

            // The colour rides on the window rather than a static so concurrent
            // rotations cannot paint each other's cover; WndProc reads it back
            // per message. DestroyRotationCover owns deleting it.
            var brush = CreateSolidBrush(ToColorRef(coverColorHex));
            SetLastError(0);
            if (SetWindowLongPtrW(hwnd, GWLP_USERDATA, brush) == IntPtr.Zero
                && Marshal.GetLastWin32Error() != 0)
            {
                // Nothing holds the brush handle but this call, and every paint
                // reads it back from the window, so a cover that lost it would
                // hold up a stock-black window for the whole rotation and look
                // like a working cover on a dark panel.
                Log($"cover skipped: SetWindowLongPtr failed err={Marshal.GetLastWin32Error()}");
                DeleteObject(brush);
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            }
            SetWindowPos(hwnd, HWND_TOPMOST, x, y, width, height, SWP_SHOWWINDOW | SWP_NOACTIVATE);
            PaintCover(hwnd);
            Log($"cover up: ({x},{y}) {width}x{height} color={(coverColorHex.Length == 0 ? "black" : coverColorHex)}");
            created = IntPtr.Zero;
            return hwnd;
        }
        catch (Exception ex)
        {
            Log($"cover skipped: {ex.GetType().Name} {ex.Message}");
            // ApplyToAdapter's finally only tears down a cover it was handed, so
            // a throw after the window is shown would strand a fullscreen
            // topmost window over the panel until the helper restarts.
            if (created != IntPtr.Zero) DestroyRotationCover(created);
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Cover decisions land in the helper's log: this provider only ever runs
    /// inside the user-session helper, and a silently skipped cover is
    /// indistinguishable on-glass from one that never painted.
    /// </summary>
    private static void Log(string message) => HelperLog.Write($"[rotation] {message}");

    /// <summary>True when the given rect overlaps any monitor OTHER than <paramref name="device"/>.</summary>
    private static bool UnionHitsAnotherMonitor(string device, int left, int top, int right, int bottom)
    {
        var hit = false;
        bool Cb(IntPtr hMonitor, IntPtr _, IntPtr __, IntPtr ___)
        {
            var info = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfoW(hMonitor, ref info)) return true;
            if (string.Equals(info.szDevice, device, StringComparison.OrdinalIgnoreCase)) return true;
            var m = info.rcMonitor;
            if (left < m.Right && right > m.Left && top < m.Bottom && bottom > m.Top)
            {
                hit = true;
                return false; // ok to stop early: EnumDisplayMonitors ends on a false return
            }
            return true;
        }
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Cb, IntPtr.Zero);
        return hit;
    }

    /// <summary>
    /// Fills whatever part of the cover is on a monitor right now. A window DC
    /// is clipped to the window's visible region, so a call made before the
    /// rotation cannot reach the strip it is about to expose however large a
    /// rect it passes; the cadence calls after it are what fill the strip.
    /// </summary>
    private static void PaintCover(IntPtr hwnd)
    {
        var brush = GetWindowLongPtrW(hwnd, GWLP_USERDATA);
        if (brush == IntPtr.Zero || !GetClientRect(hwnd, out var rect)) return;
        var hdc = GetDC(hwnd);
        if (hdc == IntPtr.Zero) return;
        FillRect(hdc, ref rect, brush);
        ReleaseDC(hwnd, hdc);
    }

    /// <summary>
    /// Dispatches messages for <paramref name="milliseconds"/>. The cover
    /// window is owned by this thread, so its paints only run while this
    /// pumps - a plain sleep here leaves the newly exposed strip unpainted
    /// for the whole hold, which is the flash the cover exists to prevent.
    /// </summary>
    private static void PumpFor(int milliseconds)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (true)
        {
            while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0) return;
            // Wakes on the next message or the deadline, whichever lands first.
            MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, (uint)remaining, QS_ALLINPUT, 0);
        }
    }

    private static void DestroyRotationCover(IntPtr hwnd)
    {
        var brush = GetWindowLongPtrW(hwnd, GWLP_USERDATA);
        try { DestroyWindow(hwnd); } catch { }
        if (brush != IntPtr.Zero) DeleteObject(brush);
    }

    /// <summary>"#rrggbb" to a COLORREF (0x00bbggrr); unparsable or empty falls back to opaque black.</summary>
    private static uint ToColorRef(string hex)
    {
        if (hex.Length == 7 && hex[0] == '#'
            && byte.TryParse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)
            && byte.TryParse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
            && byte.TryParse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return (uint)((b << 16) | (g << 8) | r);
        }
        return 0; // black
    }

    private static bool _coverClassRegistered;
    private static readonly object _coverClassLock = new();
    private static WndProcDelegate? _pinnedCoverProc;

    /// <summary>
    /// Registers the cover window class once per process; every call after
    /// the first is a no-op. The class background is a stock black brush -
    /// PaintCover immediately overpaints with the resolved colour, but any
    /// erase that lands before that call (a fresh HWND's first composite)
    /// still shows solid black rather than whatever was behind it, matching
    /// nexus-overlay's PanelKioskWindow class background for the same
    /// newly-exposed-region race.
    /// </summary>
    private static bool EnsureCoverWindowClassRegistered()
    {
        lock (_coverClassLock)
        {
            if (_coverClassRegistered) return true;
            _pinnedCoverProc = CoverWndProc;
            var cls = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = _pinnedCoverProc,
                lpszClassName = CoverWindowClassName,
                cbWndExtra = IntPtr.Size,
                hInstance = GetModuleHandleW(null),
                hbrBackground = GetStockObject(BLACK_BRUSH),
            };
            if (RegisterClassExW(ref cls) == 0) return false;
            _coverClassRegistered = true;
            return true;
        }
    }

    /// <summary>
    /// Erases with the cover colour rather than the class brush, so a region
    /// that only becomes visible after the mode change (the exposed strip)
    /// comes up in the panel's background colour instead of the desktop
    /// showing through an unpainted window.
    /// </summary>
    private static IntPtr CoverWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_DISPLAYCHANGE)
        {
            // Marks the instant the new display surfaces are live: paints made
            // before this land on the outgoing surface and are dropped.
            SetWindowLongPtrW(hwnd, DisplayChangeSlot, new IntPtr(1));
        }
        if (msg == WM_ERASEBKGND)
        {
            var brush = GetWindowLongPtrW(hwnd, GWLP_USERDATA);
            if (brush != IntPtr.Zero && GetClientRect(hwnd, out var rect))
            {
                FillRect(wParam, ref rect, brush);
                return new IntPtr(1);
            }
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    // -- Win32 ---------------------------------------------------------------

    private const int CCHDEVICENAME = 32;
    private const int CCHDEVICESTRING = 128;
    private const int CCHDEVICEID = 128;
    private const int CCHDEVICEKEY = 128;

    private const uint ENUM_CURRENT_SETTINGS = unchecked((uint)-1);

    private const uint DMDO_DEFAULT = 0;
    private const uint DMDO_90 = 1;
    private const uint DMDO_180 = 2;
    private const uint DMDO_270 = 3;

    private const uint DM_PELSWIDTH = 0x00080000;
    private const uint DM_PELSHEIGHT = 0x00100000;
    private const uint DM_DISPLAYORIENTATION = 0x00800000;

    private const uint CDS_UPDATEREGISTRY = 0x00000001;

    private const int DISP_CHANGE_SUCCESSFUL = 0;
    private const int DISP_CHANGE_RESTART = 1;
    private const int DISP_CHANGE_FAILED = -1;
    private const int DISP_CHANGE_BADMODE = -2;
    private const int DISP_CHANGE_NOTUPDATED = -3;
    private const int DISP_CHANGE_BADFLAGS = -4;
    private const int DISP_CHANGE_BADPARAM = -5;

    private const string CoverWindowClassName = "NexusRotationCoverWnd";
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int BLACK_BRUSH = 4;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_DISPLAYCHANGE = 0x007E;
    // Per-window slot (cbWndExtra) holding the WM_DISPLAYCHANGE flag, so
    // concurrent rotations cannot observe each other's cover.
    private const int DisplayChangeSlot = 0;
    private const int GWLP_USERDATA = -21;
    private const uint PM_REMOVE = 0x0001;
    private const uint QS_ALLINPUT = 0x04FF;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICESTRING)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICEID)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICEKEY)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprcMonitor, IntPtr dwData);

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

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string lpszDeviceName, uint iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(
        string lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern void SetLastError(uint dwErrCode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint crColor);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int fnObject);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    // SetWindowLongPtrW/GetWindowLongPtrW are exported by name only on 64-bit
    // user32; this build is x64/arm64 only.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern uint MsgWaitForMultipleObjectsEx(
        uint nCount, IntPtr pHandles, uint dwMilliseconds, uint dwWakeMask, uint dwFlags);
}
#endif
