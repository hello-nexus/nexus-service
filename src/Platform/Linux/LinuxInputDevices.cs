using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nexus.Service.Devices.Handlers;

namespace Nexus.Service.Platform.Linux;

/// <summary>Kernel input nodes read from sysfs, to tell the Y70 digitizer from other touch devices.</summary>
internal static class LinuxInputDevices
{
    private const int CorsairVendor = 0x1B1C;
    private const int CorsairEdgeEndpoint = 0x1D0D;

    /// <summary>
    /// Kernel names of event nodes carrying a Y70 digitizer id. The Xeneon Edge shares the
    /// 27C0:0859 id and is told apart by a Corsair endpoint on the same internal hub.
    /// </summary>
    internal static List<string> ReadY70TouchNames(string sysRoot = "/sys")
    {
        var names = new List<string>();
        try
        {
            var input = Path.Combine(sysRoot, "class", "input");
            if (!Directory.Exists(input))
                return names;
            foreach (var dir in Directory.GetDirectories(input, "event*"))
            {
                var device = Path.Combine(dir, "device");
                var vid = LinuxSysfs.ReadHex(Path.Combine(device, "id", "vendor"));
                var pid = LinuxSysfs.ReadHex(Path.Combine(device, "id", "product"));
                if (vid is null || pid is null || !Y70Handler.TouchDigitizers.Any(t => t.VendorId == vid && t.ProductId == pid))
                    continue;
                if (LinuxSysfs.ReadText(Path.Combine(device, "name")) is not { Length: > 0 } name)
                    continue;
                if (HasEdgeCompanion(dir))
                    continue;
                if (!names.Contains(name, StringComparer.Ordinal))
                    names.Add(name);
            }
        }
        catch (Exception)
        {
            return [];
        }
        return names;
    }

    /// <summary>
    /// <paramref name="eventDir"/> is the class entry, itself a symlink; its relative <c>device</c> link
    /// only resolves from the real directory.
    /// </summary>
    internal static bool HasEdgeCompanion(string eventDir)
    {
        var realEvent = Directory.ResolveLinkTarget(eventDir, returnFinalTarget: true)?.FullName ?? eventDir;
        var deviceLink = Path.Combine(realEvent, "device");
        var real = Directory.ResolveLinkTarget(deviceLink, returnFinalTarget: true)?.FullName ?? deviceLink;
        var usb = new DirectoryInfo(real);
        while (usb is not null && !File.Exists(Path.Combine(usb.FullName, "idVendor")))
            usb = usb.Parent;
        if (usb?.Parent is not { } hub)
            return false;
        foreach (var sibling in hub.GetDirectories())
        {
            if (LinuxSysfs.ReadHex(Path.Combine(sibling.FullName, "idVendor")) == CorsairVendor
                && LinuxSysfs.ReadHex(Path.Combine(sibling.FullName, "idProduct")) == CorsairEdgeEndpoint)
            {
                return true;
            }
        }
        return false;
    }
}
