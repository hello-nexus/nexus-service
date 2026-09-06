using System;
using System.Collections.Generic;
using System.IO;
using Nexus.Service.Platform.Linux;

namespace Nexus.Service.Peripherals.Hyte;

/// <summary>
/// Shared Linux serial-port discovery for the HYTE USB-CDC hubs (CNVS, NP50,
/// MiniHub, Q-series cooler, Y70 display). Mirrors what the per-device
/// <c>Windows*PortDiscovery</c> classes do via SetupAPI, but reads the USB
/// identity straight out of sysfs: every <c>/dev/ttyACM*</c> / <c>/dev/ttyUSB*</c>
/// node has a <c>/sys/class/tty/&lt;name&gt;/device</c> symlink into the USB device
/// tree, whose enclosing device directory exposes <c>idVendor</c>,
/// <c>idProduct</c> and <c>serial</c>. Pure BCL file IO - AOT-safe, no P/Invoke.
/// All five HYTE hubs share VID 0x3402 and differ only by PID, so one helper
/// backs all of them; each device's thin wrapper maps the matched PID to its
/// own port-info shape.
/// </summary>
internal static class LinuxSerialDiscovery
{
    /// <summary>A serial port that matched the requested USB VID/PID filter.</summary>
    internal readonly record struct Match(string PortName, string Serial, int ProductId);

    /// <summary>
    /// Enumerate attached serial ports whose USB parent reports
    /// <paramref name="vendorId"/> and one of <paramref name="productIds"/>.
    /// Returns an empty list off-Linux (the shared non-Windows compile unit
    /// also builds on macOS) and on any IO error.
    /// </summary>
    internal static IReadOnlyList<Match> Find(int vendorId, params int[] productIds)
        => OperatingSystem.IsLinux()
            ? FindIn("/dev", "/sys/class/tty", vendorId, productIds)
            : Array.Empty<Match>();

    /// <summary>
    /// Core discovery against explicit <paramref name="devRoot"/> and
    /// <paramref name="sysTtyRoot"/> trees, with no platform gate - so tests can
    /// drive it against a fake sysfs tree on any OS.
    /// </summary>
    internal static IReadOnlyList<Match> FindIn(string devRoot, string sysTtyRoot, int vendorId, params int[] productIds)
    {
        var matches = new List<Match>();
        foreach (var devPath in EnumerateSerialNodes(devRoot))
        {
            try
            {
                var name = Path.GetFileName(devPath); // e.g. "ttyACM0"
                var usbDir = ResolveUsbDeviceDir(Path.Combine(sysTtyRoot, name));
                if (usbDir is null)
                    continue;

                if (LinuxSysfs.ReadHex(Path.Combine(usbDir, "idVendor")) != vendorId)
                    continue;
                var pid = LinuxSysfs.ReadHex(Path.Combine(usbDir, "idProduct"));
                if (pid is null)
                    continue;
                if (productIds.Length > 0 && Array.IndexOf(productIds, pid.Value) < 0)
                    continue;

                var serial = LinuxSysfs.ReadText(Path.Combine(usbDir, "serial")) ?? "";
                matches.Add(new Match(devPath, serial, pid.Value));
            }
            catch
            {
                // A device can disappear mid-enumeration; skip it and keep going.
            }
        }
        return matches;
    }

    private static IEnumerable<string> EnumerateSerialNodes(string devRoot)
    {
        if (!Directory.Exists(devRoot))
            yield break;

        foreach (var pattern in new[] { "ttyACM*", "ttyUSB*" })
        {
            string[] entries;
            try { entries = Directory.GetFiles(devRoot, pattern); }
            catch { entries = Array.Empty<string>(); }
            foreach (var e in entries)
                yield return e;
        }
    }

    /// <summary>
    /// Resolve <c>/sys/class/tty/&lt;name&gt;/device</c> to the real path, then walk up
    /// the USB device tree until a directory exposing <c>idVendor</c> is found. The
    /// <c>device</c> link points at the USB *interface* (e.g. <c>2-1:1.0</c>); the
    /// VID/PID/serial live on its parent USB *device* node (e.g. <c>2-1</c>), so we
    /// climb a few levels.
    /// </summary>
    private static string? ResolveUsbDeviceDir(string classEntry)
    {
        var dir = ResolveDeviceLink(classEntry);

        for (var i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
        {
            if (File.Exists(Path.Combine(dir, "idVendor")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }

    /// <summary>
    /// <c>readlink -f</c> semantics for the sysfs <c>device</c> link. Both halves are
    /// symlinks in real sysfs: <c>/sys/class/tty/ttyACM0</c> points into
    /// <c>/sys/devices/…</c>, and its <c>device</c> entry is a RELATIVE link
    /// (<c>../../../1-13.3.3.2:1.0</c>). <see cref="Directory.ResolveLinkTarget"/>
    /// resolves a relative target against the literal path it was handed, yielding
    /// <c>/sys/1-13.3.3.2:1.0</c> - a path that does not exist, so every port was
    /// silently dropped. Resolve the class entry first, then combine the leaf target
    /// against that physical directory.
    /// </summary>
    private static string? ResolveDeviceLink(string classEntry)
    {
        try
        {
            var entry = Directory.ResolveLinkTarget(classEntry, returnFinalTarget: true)?.FullName
                        ?? classEntry;
            var device = new DirectoryInfo(Path.Combine(entry, "device"));
            var target = device.LinkTarget;
            if (target is null)
                return device.Exists ? device.FullName : null;
            return Path.GetFullPath(target, entry);
        }
        catch
        {
            return null;
        }
    }
}
