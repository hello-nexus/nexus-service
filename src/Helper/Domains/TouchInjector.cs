#if WINDOWS
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Nexus.Service.Models.Panel;
using Nexus.Service.Platform.Displays;

namespace Nexus.Service.Helper.Domains;

/// <summary>
/// Injects touch onto one monitor from the user's session. Coordinates arrive relative to the
/// monitor and are placed on the desktop in physical pixels, so the thread runs per-monitor
/// DPI aware while it reads the monitor's position and injects.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class TouchInjector
{
    private const uint PointerTypeTouch = 2;
    private const uint FlagInRange = 0x2;
    private const uint FlagInContact = 0x4;
    private const uint FlagDown = 0x10000;
    private const uint FlagUpdate = 0x20000;
    private const uint FlagUp = 0x40000;
    private const uint MaskContactOrientationPressure = 0x7;
    private const uint FeedbackDefault = 1;
    private const int MaxContacts = 10;
    private const uint EnumCurrentSettings = unchecked((uint)-1);
    private const uint AttachedToDesktop = 0x1;
    private const long BoundsTtlMs = 2000;
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    private static readonly object Gate = new();
    private static bool _initialized;
    private static string? _boundsAdapter;
    private static long _boundsAt;
    private static (int Left, int Top, int Width, int Height) _bounds;

    public static bool Inject(TouchInjectBody body)
    {
        uint flags = body.Phase switch
        {
            "down" => FlagInRange | FlagInContact | FlagDown,
            "move" => FlagInRange | FlagInContact | FlagUpdate,
            "up" => FlagUp,
            _ => 0,
        };
        if (flags == 0)
        {
            return false;
        }
        lock (Gate)
        {
            var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
            try
            {
                if (!_initialized)
                {
                    if (!InitializeTouchInjection(MaxContacts, FeedbackDefault))
                    {
                        return false;
                    }
                    _initialized = true;
                }
                if (!TryGetBounds(body.Adapter, out var bounds))
                {
                    return false;
                }
                int x = bounds.Left + Math.Clamp(body.X, 0, bounds.Width - 1);
                int y = bounds.Top + Math.Clamp(body.Y, 0, bounds.Height - 1);
                var info = new POINTER_TOUCH_INFO
                {
                    pointerInfo = new POINTER_INFO
                    {
                        pointerType = PointerTypeTouch,
                        pointerId = body.PointerId,
                        pointerFlags = flags,
                        ptPixelLocation = new POINT { X = x, Y = y },
                    },
                    touchMask = MaskContactOrientationPressure,
                    rcContact = new RECT { Left = x - 2, Top = y - 2, Right = x + 2, Bottom = y + 2 },
                    orientation = 90,
                    pressure = 512,
                };
                return InjectTouchInput(1, &info);
            }
            finally
            {
                if (previous != IntPtr.Zero)
                {
                    SetThreadDpiAwarenessContext(previous);
                }
            }
        }
    }

    private static bool TryGetBounds(string adapter, out (int Left, int Top, int Width, int Height) bounds)
    {
        long now = Environment.TickCount64;
        if (_boundsAdapter == adapter && now - _boundsAt < BoundsTtlMs)
        {
            bounds = _bounds;
            return true;
        }
        bounds = default;
        for (uint i = 0; ; i++)
        {
            var device = new WindowsDisplayIdentity.DISPLAY_DEVICE { cb = Marshal.SizeOf<WindowsDisplayIdentity.DISPLAY_DEVICE>() };
            if (!WindowsDisplayIdentity.EnumDisplayDevicesW(null!, i, ref device, 0))
            {
                return false;
            }
            if ((device.StateFlags & AttachedToDesktop) == 0
                || !string.Equals(device.DeviceString, adapter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var mode = new DEVMODEW { dmSize = (ushort)Marshal.SizeOf<DEVMODEW>() };
            if (!EnumDisplaySettingsW(device.DeviceName, EnumCurrentSettings, ref mode) || mode.dmPelsWidth == 0)
            {
                return false;
            }
            bounds = (mode.dmPositionX, mode.dmPositionY, (int)mode.dmPelsWidth, (int)mode.dmPelsHeight);
            _bounds = bounds;
            _boundsAdapter = adapter;
            _boundsAt = now;
            return true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_INFO
    {
        public uint pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public POINT ptPixelLocation;
        public POINT ptHimetricLocation;
        public POINT ptPixelLocationRaw;
        public POINT ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int inputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint touchFlags;
        public uint touchMask;
        public RECT rcContact;
        public RECT rcContactRaw;
        public uint orientation;
        public uint pressure;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODEW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
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
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InitializeTouchInjection(uint maxCount, uint dwMode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InjectTouchInput(uint count, POINTER_TOUCH_INFO* contacts);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string lpszDeviceName, uint iModeNum, ref DEVMODEW lpDevMode);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
}
#endif
