using System;
using System.Globalization;

namespace Nexus.Service.Peripherals.Hid;

/// <summary>
/// Reads the USB <c>vid_XXXX&amp;pid_XXXX</c> a Windows HID interface path carries,
/// so a VID/PID lookup can skip a foreign device without opening it.
/// </summary>
internal static class HidPathIds
{
    /// <summary>False only when the path names a different USB VID/PID; a path without one (Bluetooth, virtual) needs the attribute check.</summary>
    public static bool MayMatch(string devicePath, int vendorId, int productId) =>
        !TryParse(devicePath, out var vid, out var pid) || (vid == vendorId && pid == productId);

    public static bool TryParse(string devicePath, out int vendorId, out int productId)
    {
        vendorId = 0;
        productId = 0;
        var i = devicePath.IndexOf("vid_", StringComparison.OrdinalIgnoreCase);
        if (i < 0 || i + 17 > devicePath.Length) return false;
        var span = devicePath.AsSpan(i);
        return span.Slice(8, 5).Equals("&pid_", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(span.Slice(4, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out vendorId)
            && int.TryParse(span.Slice(13, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out productId);
    }
}
