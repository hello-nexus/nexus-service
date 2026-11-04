using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Nexus.Service.Diagnostics;

/// <summary>One USB device, or a hub above it. Id is a short hash of the serial or instance id, never the raw value.</summary>
public sealed class UsbTopologyNode
{
    public string Id { get; set; } = "";
    public string VidPid { get; set; } = "";
    public string Name { get; set; } = "";
    public string Speed { get; set; } = "";
    /// <summary>Hub port path: sysfs device path on Linux, locationID on macOS, instance-id depth on Windows.</summary>
    public string Port { get; set; } = "";
}

public sealed class UsbTopologyDevice
{
    public UsbTopologyNode Device { get; set; } = new();
    /// <summary>Nearest hub first, root last.</summary>
    public List<UsbTopologyNode> Parents { get; set; } = new();
}

public sealed class UsbTopologyReport
{
    /// <summary>Set instead of Devices when this OS's reader failed; the bundle never fails over it.</summary>
    public string? Unavailable { get; set; }
    public List<UsbTopologyDevice> Devices { get; set; } = new();
}

public static class UsbTopology
{
    private const int MaxDepth = 16;

    public static string ShortHash(string value)
    {
        if (value.Length == 0)
        {
            return "";
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)), 0, 4).ToLowerInvariant();
    }

    /// <summary>Topology of the devices <paramref name="isKnown"/> accepts, with every hub above each. Never throws.</summary>
    public static UsbTopologyReport Collect(Func<int, int, bool> isKnown)
    {
        try
        {
#if WINDOWS
            return new UsbTopologyReport { Devices = Platform.Windows.WindowsDevnodes.ReadUsbTopology(isKnown) };
#elif MACOS
            return new UsbTopologyReport { Devices = Devices.Detection.MacUsbEnumerator.ReadTopology(isKnown) };
#else
            if (!OperatingSystem.IsLinux())
            {
                return new UsbTopologyReport { Unavailable = "unsupported OS" };
            }
            return ReadSysfs("/sys/bus/usb/devices", isKnown);
#endif
        }
        catch (Exception ex)
        {
            return new UsbTopologyReport { Unavailable = $"{ex.GetType().Name}: {ex.Message}" };
        }
    }

    internal static UsbTopologyReport ReadSysfs(string root, Func<int, int, bool> isKnown)
    {
        if (!Directory.Exists(root))
        {
            return new UsbTopologyReport { Unavailable = "sysfs usb tree not found" };
        }
        var report = new UsbTopologyReport();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith("usb", StringComparison.Ordinal) || name.Contains(':'))
            {
                continue;
            }
            var node = ReadNode(dir);
            if (node is null || !isKnown(node.Value.Vid, node.Value.Pid))
            {
                continue;
            }
            var device = new UsbTopologyDevice { Device = node.Value.Node };
            var current = name;
            for (var depth = 0; depth < MaxDepth; depth++)
            {
                var parent = ParentName(current);
                if (parent is null)
                {
                    break;
                }
                var parentNode = ReadNode(Path.Combine(root, parent));
                if (parentNode is not null)
                {
                    device.Parents.Add(parentNode.Value.Node);
                }
                current = parent;
            }
            report.Devices.Add(device);
        }
        report.Devices.Sort((a, b) => string.CompareOrdinal(a.Device.Port, b.Device.Port));
        return report;
    }

    /// <summary>"1-2.3" sits under "1-2"; "1-2" sits under the root hub "usb1"; the root hub has no parent here.</summary>
    internal static string? ParentName(string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot > 0)
        {
            return name[..dot];
        }
        var dash = name.IndexOf('-');
        return name.StartsWith("usb", StringComparison.Ordinal) || dash <= 0 ? null : "usb" + name[..dash];
    }

    private static (UsbTopologyNode Node, int Vid, int Pid)? ReadNode(string dir)
    {
        var vid = ParseHex(Read(dir, "idVendor"));
        var pid = ParseHex(Read(dir, "idProduct"));
        if (vid < 0 || pid < 0)
        {
            return null;
        }
        var speed = Read(dir, "speed");
        var node = new UsbTopologyNode
        {
            Id = ShortHash(Read(dir, "serial") is { Length: > 0 } serial ? $"{vid:x4}{pid:x4}{serial}" : Path.GetFileName(dir)),
            VidPid = $"{vid:X4}:{pid:X4}",
            Name = Read(dir, "product"),
            Speed = speed.Length == 0 ? "" : speed + " Mb/s",
            Port = Path.GetFileName(dir),
        };
        return (node, vid, pid);
    }

    private static string Read(string dir, string file)
    {
        try
        {
            var path = Path.Combine(dir, file);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
        catch (IOException)
        {
            return "";
        }
    }

    private static int ParseHex(string value) =>
        int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n) ? n : -1;
}
