#if WINDOWS
using System;
using System.Runtime.InteropServices;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Shared monitor-identity resolution for the Windows display providers.
/// The stable id is the EDID-derived portion of the monitor's PnP DeviceID
/// (survives reboots and cable shuffles) and is the join key across
/// GET /displays, GET /displays/topology, and panel assignments - any change
/// here is a breaking id migration. nexus-overlay duplicates this extraction
/// in its DisplayIdentity; keep the two in sync.
/// </summary>
internal static class WindowsDisplayIdentity
{
    internal const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x00000001;

    /// <summary>
    /// Returns (stableId, friendlyName, manufacturer3, model, isInternal) for
    /// the first monitor child of a GDI adapter device (\\.\DISPLAYn). Falls
    /// back to the adapter+index identifier when EnumDisplayDevices doesn't
    /// expose the PnP id.
    /// </summary>
    internal static (string Id, string Name, string Manufacturer, string Model, bool IsInternal) ResolveIdentity(string adapterDeviceName)
    {
        var monitor = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        bool ok = EnumDisplayDevicesW(adapterDeviceName, 0, ref monitor, EDD_GET_DEVICE_INTERFACE_NAME);
        if (!ok)
        {
            // Retry without the interface-name flag for older drivers.
            monitor = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            ok = EnumDisplayDevicesW(adapterDeviceName, 0, ref monitor, 0);
        }
        if (!ok)
        {
            return (adapterDeviceName, adapterDeviceName, "", "", false);
        }
        var deviceId = monitor.DeviceID ?? "";
        var friendly = string.IsNullOrWhiteSpace(monitor.DeviceString) ? adapterDeviceName : monitor.DeviceString;

        // DeviceID looks like \\?\DISPLAY#DEL41B7#5&abc&0&UID12345#{...guid...}
        // Take the part between the first and last '#' as the EDID-stable id.
        var stable = deviceId;
        var firstHash = deviceId.IndexOf('#');
        var lastHash = deviceId.LastIndexOf('#');
        var manufacturer = "";
        var model = "";
        if (firstHash > 0 && lastHash > firstHash)
        {
            stable = deviceId.Substring(firstHash + 1, lastHash - firstHash - 1);
            // "DEL41B7" -> "DEL" manufacturer EISA id, "41B7" hex product code
            var firstSegEnd = stable.IndexOf('#');
            if (firstSegEnd >= 7)
            {
                var seg = stable.Substring(0, firstSegEnd);
                manufacturer = seg.Substring(0, 3);
                model = seg.Length > 3 ? seg.Substring(3) : "";
            }
        }
        if (string.IsNullOrEmpty(stable))
        {
            // EnumDisplayDevices left DeviceID empty (some virtual / non-PnP
            // displays). Fall back to the adapter's trailing DISPLAYn segment
            // so the row still gets a stable handle for brightness round-trips.
            stable = AdapterIndexFallback(adapterDeviceName);
        }
        // Replace URL-hostile characters so the id round-trips through HTTP
        // path segments without escaping headaches.
        stable = SanitizeId(stable);

        // "Generic PnP Monitor" is the default DeviceString; prefer something
        // more user-friendly when we have manufacturer + model identifiers.
        if (!string.IsNullOrEmpty(manufacturer) && !string.IsNullOrEmpty(model))
        {
            friendly = $"{manufacturer} {model}";
        }

        // Heuristic: classify a panel as internal by the absence of HDMI/DP
        // signal in the friendly name (laptop internal panels show as
        // "Built-in" / PnP IDs like LEN/AAP / output technology "internal").
        var isInternal = friendly.IndexOf("internal", StringComparison.OrdinalIgnoreCase) >= 0
                      || friendly.IndexOf("built-in", StringComparison.OrdinalIgnoreCase) >= 0;

        return (stable, friendly, manufacturer, model, isInternal);
    }

    /// <summary>
    /// Raw monitor PnP DeviceID (e.g. \\?\DISPLAY#RTK0004#...) for the first
    /// child monitor of an adapter - used to match a controller name before we
    /// collapse it to the sanitized stable id.
    /// </summary>
    internal static string ReadMonitorDeviceId(string adapterDeviceName)
    {
        var monitor = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        if (!EnumDisplayDevicesW(adapterDeviceName, 0, ref monitor, EDD_GET_DEVICE_INTERFACE_NAME))
        {
            monitor = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevicesW(adapterDeviceName, 0, ref monitor, 0)) return "";
        }
        return monitor.DeviceID ?? "";
    }

    /// <summary>
    /// Monitor device interface path (\\?\DISPLAY#...#{guid}) via
    /// EDD_GET_DEVICE_INTERFACE_NAME only, with no flags=0 fallback: callers
    /// that need the interface form specifically (Digimon registry values)
    /// must never receive the different, non-interface DeviceID the flags=0
    /// retry in ReadMonitorDeviceId returns. Empty when unavailable.
    /// </summary>
    internal static string ReadMonitorInterfacePath(string adapterDeviceName)
    {
        var monitor = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        return EnumDisplayDevicesW(adapterDeviceName, 0, ref monitor, EDD_GET_DEVICE_INTERFACE_NAME)
            ? monitor.DeviceID ?? ""
            : "";
    }

    internal static string SanitizeId(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        var buf = new char[raw.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            buf[i] = (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.') ? c : '-';
        }
        return new string(buf);
    }

    internal static string AdapterIndexFallback(string adapterDeviceName)
    {
        if (string.IsNullOrEmpty(adapterDeviceName)) return "display-unknown";
        var lastSlash = adapterDeviceName.LastIndexOf('\\');
        var tail = lastSlash >= 0 ? adapterDeviceName[(lastSlash + 1)..] : adapterDeviceName;
        return string.IsNullOrEmpty(tail) ? "display-unknown" : tail.ToLowerInvariant();
    }

    /// <summary>"\\.\DISPLAY3" -> 3; 0 when the tail isn't a number.</summary>
    internal static int AdapterNumber(string adapterDeviceName)
    {
        if (string.IsNullOrEmpty(adapterDeviceName)) return 0;
        var i = adapterDeviceName.Length;
        while (i > 0 && char.IsAsciiDigit(adapterDeviceName[i - 1])) i--;
        return i < adapterDeviceName.Length && int.TryParse(adapterDeviceName[i..], out var n) ? n : 0;
    }

    // Win32 DISPLAY_DEVICEW ABI: WCHAR DeviceName[32] (NOT 128 - the
    // brightness provider's old private copy used 128, which skews every
    // subsequent field offset so DeviceID unmarshals empty and ids degrade
    // to adapter fallbacks). Matches nexus-overlay's PanelDisplay struct.
    private const int CCHDEVICENAME = 32;
    private const int CCHDEVICESTRING = 128;
    private const int CCHDEVICEID = 128;
    private const int CCHDEVICEKEY = 128;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICESTRING)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICEID)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICEKEY)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool EnumDisplayDevicesW(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);
}
#endif
