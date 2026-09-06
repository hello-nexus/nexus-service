using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nexus.Service.Models.Displays;
using Nexus.Service.Platform.Linux;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Linux monitor topology from DRM sysfs (/sys/class/drm/card*-*). Works
/// headless as root - no X/Wayland required - but layout positions are a
/// compositor concept sysfs doesn't expose, so X/Y stay null and
/// <see cref="PositionsAvailable"/> is false (the UI rows monitors instead
/// of mapping them). Identity comes from the connector's EDID blob; the
/// resolution is the preferred (first) mode.
/// </summary>
public sealed class LinuxDisplayTopologyProvider : IDisplayTopologyProvider
{
    private const string DrmRoot = "/sys/class/drm";

    public bool PositionsAvailable => false;

    public IReadOnlyList<RawDisplayInfo>? Enumerate()
    {
        var results = new List<RawDisplayInfo>();
        try
        {
            if (!Directory.Exists(DrmRoot)) return results;
            var usb = ReadUsbTree();
            var connectors = Directory.GetDirectories(DrmRoot, "card*-*")
                .OrderBy(p => p, StringComparer.Ordinal);
            foreach (var dir in connectors)
            {
                try
                {
                    var entry = ReadConnector(dir, results.Count + 1, usb);
                    if (entry is not null) results.Add(entry);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[displays-linux] connector {Path.GetFileName(dir)} failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[displays-linux] topology enumerate failed: {ex.Message}");
        }
        return results;
    }

    private static RawDisplayInfo? ReadConnector(string dir, int number, IReadOnlyList<UsbNode> usb)
    {
        var status = ReadFirstLine(Path.Combine(dir, "status"));
        if (!string.Equals(status, "connected", StringComparison.Ordinal)) return null;

        // "card1-DP-1" -> "DP-1"
        var dirName = Path.GetFileName(dir);
        var dash = dirName.IndexOf('-');
        var connector = dash >= 0 ? dirName[(dash + 1)..] : dirName;

        var (width, height) = ParseMode(ReadFirstLine(Path.Combine(dir, "modes")));

        byte[] edid = Array.Empty<byte>();
        try { edid = File.ReadAllBytes(Path.Combine(dir, "edid")); } catch { }
        var identity = ParseEdidIdentity(edid);

        var id = identity.Mfg.Length > 0
            ? $"{identity.Mfg}{identity.Product:X4}-{connector}"
            : connector;
        var name = identity.ModelName.Length > 0
            ? identity.ModelName
            : (identity.Mfg.Length > 0 ? $"{identity.Mfg} {identity.Product:X4}" : connector);

        double? dpi = identity.WidthCm > 0 && width > 0
            ? Math.Round(width / (identity.WidthCm / 2.54), 1)
            : null;

        return new RawDisplayInfo
        {
            Id = id,
            Number = number,
            Name = name,
            Manufacturer = identity.Mfg,
            Model = identity.Product > 0 ? identity.Product.ToString("X4") : "",
            X = null,
            Y = null,
            Width = width,
            Height = height,
            ResolutionWidth = width,
            ResolutionHeight = height,
            Scale = null,
            Dpi = dpi,
            IsPrimary = false,
            IsInternal = connector.StartsWith("eDP", StringComparison.OrdinalIgnoreCase)
                      || connector.StartsWith("LVDS", StringComparison.OrdinalIgnoreCase),
            IsTouch = HasDigitizer(id, usb),
            RawHardwareId = id,
        };
    }

    /// <summary>One USB device node: its ids and the sysfs parent, which is the hub
    /// a companion-scoped digitizer must share with its companion.</summary>
    private readonly record struct UsbNode(int VendorId, int ProductId, string Parent);

    /// <summary>
    /// True when this display matches a <see cref="TouchPanelCatalog"/> entry and that
    /// entry's digitizer is on the bus. Windows derives touch from the digitizer-to-
    /// monitor mapping; there is no such link in DRM, so presence of the paired
    /// digitizer is the signal.
    /// </summary>
    private static bool HasDigitizer(string hardwareId, IReadOnlyList<UsbNode> usb)
    {
        var entry = TouchPanelCatalog.MatchDisplay(hardwareId);
        if (entry is null) return false;
        foreach (var node in usb)
        {
            var isDigitizer = entry.DigitizerIds.Any(
                d => d.VendorId == node.VendorId && d.ProductId == node.ProductId);
            if (!isDigitizer) continue;
            if (!entry.IsCompanionScoped) return true;
            // Descriptor-identical digitizers are told apart by a sibling on the
            // same hub, mirroring TouchMapDigitizerInfo.CompanionHardwareIds.
            var hasCompanion = usb.Any(sibling =>
                string.Equals(sibling.Parent, node.Parent, StringComparison.Ordinal)
                && entry.CompanionUsbIds!.Any(
                    c => c.VendorId == sibling.VendorId && c.ProductId == sibling.ProductId));
            if (hasCompanion) return true;
        }
        return false;
    }

    private static IReadOnlyList<UsbNode> ReadUsbTree()
    {
        var nodes = new List<UsbNode>();
        try
        {
            foreach (var dir in Directory.GetDirectories("/sys/bus/usb/devices"))
            {
                var vid = LinuxSysfs.ReadHex(Path.Combine(dir, "idVendor"));
                var pid = LinuxSysfs.ReadHex(Path.Combine(dir, "idProduct"));
                if (vid is null || pid is null) continue;
                var real = Directory.ResolveLinkTarget(dir, returnFinalTarget: true)?.FullName ?? dir;
                nodes.Add(new UsbNode(vid.Value, pid.Value, Directory.GetParent(real)?.FullName ?? ""));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[displays-linux] usb scan failed: {ex.Message}");
        }
        return nodes;
    }

    /// <summary>"2560x1440" (optionally with a suffix) -> (2560, 1440).</summary>
    internal static (int Width, int Height) ParseMode(string mode)
    {
        if (string.IsNullOrEmpty(mode)) return (0, 0);
        var x = mode.IndexOf('x');
        if (x <= 0) return (0, 0);
        if (!int.TryParse(mode.AsSpan(0, x), out var w)) return (0, 0);
        var rest = mode.AsSpan(x + 1);
        var end = 0;
        while (end < rest.Length && char.IsAsciiDigit(rest[end])) end++;
        return int.TryParse(rest[..end], out var h) ? (w, h) : (0, 0);
    }

    internal readonly record struct EdidIdentity(string Mfg, string ModelName, ushort Product, uint Serial, int WidthCm, int HeightCm);

    /// <summary>
    /// Identity fields from a 128-byte EDID base block: PNP manufacturer +
    /// model-name descriptor via the shared decoder, plus product code
    /// (bytes 10-11 LE), serial (12-15 LE) and physical size (21/22, cm).
    /// </summary>
    internal static EdidIdentity ParseEdidIdentity(ReadOnlySpan<byte> edid)
    {
        if (edid.Length < 128 || edid[0] != 0x00 || edid[1] != 0xFF)
            return new EdidIdentity("", "", 0, 0, 0, 0);

        var (mfg, modelName) = LinuxDisplayBrightnessProvider.DecodeEdid(edid);
        var product = (ushort)(edid[10] | (edid[11] << 8));
        var serial = (uint)(edid[12] | (edid[13] << 8) | (edid[14] << 16) | (edid[15] << 24));
        return new EdidIdentity(mfg, modelName, product, serial, edid[21], edid[22]);
    }

    private static string ReadFirstLine(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            return reader.ReadLine()?.Trim() ?? "";
        }
        catch { return ""; }
    }
}
