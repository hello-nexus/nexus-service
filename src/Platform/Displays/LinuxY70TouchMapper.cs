using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Models.Displays;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Maps the Y70 touch digitizer to the Y70 output on KDE Plasma Wayland by
/// setting <c>outputName</c> on its KWin InputDevice object. Without it KWin
/// spreads touch over the whole desktop, so touching the Y70 moves the cursor
/// on the main monitor. Device paths are enumerated each time because the
/// event node number changes across USB re-enumeration. Only the touch-capable
/// node of the digitizer matches; the same VID:PID also exposes a pointer node.
/// </summary>
internal static class LinuxY70TouchMapper
{
    private const string Service = "org.kde.KWin";
    private const string Interface = "org.kde.KWin.InputDevice";
    private const string DevicePrefix = "/org/kde/KWin/InputDevice/event";

    internal readonly record struct InputDeviceInfo(string Name, int Vendor, int Product, bool Touch, string OutputName);

    internal enum TouchMapStatus
    {
        Mapped,
        /// <summary>Session or bus not ready yet, or a write failed: worth another attempt.</summary>
        Retry,
        /// <summary>Not KDE, no digitizer, or ambiguous: retrying cannot change it.</summary>
        Final,
    }

    internal readonly record struct TouchMapResult(TouchMapStatus Status, string Detail)
    {
        public bool Ok => Status == TouchMapStatus.Mapped;
    }

    /// <summary>Hub-sibling hardware ids (VID_xxxx&amp;PID_xxxx) for an input node, as the Windows snapshot reports them.</summary>
    internal delegate IReadOnlyList<string> CompanionLookup(string sysName);

    /// <summary>Runs <c>busctl --user</c> with the arguments as the session user.</summary>
    internal delegate (int Exit, string Output) BusRunner(string[] args);

    /// <summary>Device object paths from <c>busctl --user tree org.kde.KWin --list</c>.</summary>
    internal static List<string> ParseDevicePaths(string tree)
    {
        var paths = new List<string>();
        foreach (var raw in tree.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(DevicePrefix, StringComparison.Ordinal) && line.IndexOf(' ') < 0)
            {
                paths.Add(line);
            }
        }
        return paths;
    }

    /// <summary>
    /// Parses <c>busctl --user get-property ... name vendor product touch outputName</c>
    /// output: one typed value per line, in the order requested.
    /// </summary>
    internal static InputDeviceInfo? ParseDevice(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 5)
        {
            return null;
        }
        var name = Unquote(lines[0]);
        var vendor = ParseUInt(lines[1]);
        var product = ParseUInt(lines[2]);
        var touch = lines[3].Trim() == "b true";
        var output = Unquote(lines[4]);
        if (name is null || vendor is null || product is null || output is null)
        {
            return null;
        }
        return new InputDeviceInfo(name, vendor.Value, product.Value, touch, output);
    }

    private static string? Unquote(string line)
    {
        var t = line.Trim();
        if (!t.StartsWith("s \"", StringComparison.Ordinal) || !t.EndsWith('"') || t.Length < 4)
        {
            return null;
        }
        return t[3..^1];
    }

    private static int? ParseUInt(string line)
    {
        var t = line.Trim();
        return t.StartsWith("u ", StringComparison.Ordinal)
            && int.TryParse(t[2..], NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>
    /// A Y70 candidate: touch-capable, a known digitizer id, and not claimed by
    /// a companion-scoped catalog entry (the Xeneon Edge shares 27C0:0859 and
    /// is told apart only by the Corsair endpoint on its hub, exactly as
    /// <see cref="TouchPanelCatalog"/> does on Windows).
    /// </summary>
    internal static bool IsY70Touch(InputDeviceInfo d, IReadOnlyList<string> companions)
    {
        if (!d.Touch || !Y70Handler.TouchDigitizers.Any(t => t.VendorId == d.Vendor && t.ProductId == d.Product))
        {
            return false;
        }
        var info = new TouchMapDigitizerInfo
        {
            InterfacePath = $"VID_{d.Vendor:X4}&PID_{d.Product:X4}",
            CompanionHardwareIds = companions.ToList(),
        };
        return !TouchPanelCatalog.IsClaimedByCompanionScopedEntry(info);
    }

    /// <summary>Maps the single Y70 touch node to <paramref name="connector"/>; idempotent.</summary>
    internal static TouchMapResult Apply(string connector, BusRunner run, CompanionLookup companions)
    {
        var (treeExit, tree) = run(["tree", Service, "--list"]);
        if (treeExit != 0)
        {
            return TreeFailure(treeExit, tree);
        }
        var paths = ParseDevicePaths(tree);
        if (paths.Count == 0)
        {
            return new(TouchMapStatus.Retry, "KWin reports no input devices yet");
        }

        var candidates = new List<(string Path, InputDeviceInfo Device)>();
        var unreadable = 0;
        foreach (var path in paths)
        {
            var (exit, text) = run(["get-property", Service, path, Interface, "name", "vendor", "product", "touch", "outputName"]);
            if (exit != 0 || ParseDevice(text) is not { } device)
            {
                unreadable++;
                continue;
            }
            if (IsY70Touch(device, companions("event" + path[DevicePrefix.Length..])))
            {
                candidates.Add((path, device));
            }
        }
        if (candidates.Count == 0)
        {
            return unreadable > 0
                ? new(TouchMapStatus.Retry, $"no Y70 touch digitizer found, {unreadable} KWin input devices unreadable")
                : new(TouchMapStatus.Final, "no Y70 touch digitizer among KWin input devices");
        }
        if (candidates.Count > 1)
        {
            return new(TouchMapStatus.Final,
                $"ambiguous: {candidates.Count} touch devices match ({string.Join(',', candidates.Select(c => "event" + c.Path[DevicePrefix.Length..]))}), not mapping");
        }

        var (candidatePath, candidate) = candidates[0];
        var node = "event" + candidatePath[DevicePrefix.Length..];
        if (candidate.OutputName == connector)
        {
            return new(TouchMapStatus.Mapped, $"touch {node} mapped to {connector}");
        }
        var (setExit, setOut) = run(["set-property", Service, candidatePath, Interface, "outputName", "s", connector]);
        if (setExit != 0)
        {
            return new(TouchMapStatus.Retry, $"set outputName on {node} failed: {setOut.Trim()}");
        }
        var (checkExit, after) = run(["get-property", Service, candidatePath, Interface, "outputName"]);
        return checkExit == 0 && Unquote(after.Trim()) == connector
            ? new(TouchMapStatus.Mapped, $"touch {node} mapped to {connector}")
            : new(TouchMapStatus.Retry, $"{node} did not keep outputName '{connector}'");
    }

    private static TouchMapResult TreeFailure(int exit, string output)
    {
        if (exit == -1)
        {
            return new(TouchMapStatus.Final, "busctl not found");
        }
        if (exit is -2 or -3 or -4)
        {
            return new(TouchMapStatus.Retry, exit == -2 ? "busctl timed out" : output);
        }
        // KWin is not on the session bus at all: not a KDE session.
        return output.Contains("not activatable", StringComparison.Ordinal) || output.Contains("not provided", StringComparison.Ordinal)
            ? new(TouchMapStatus.Final, "org.kde.KWin is not on the session bus (KDE Plasma required)")
            : new(TouchMapStatus.Retry, "session bus not reachable yet");
    }

    /// <summary>
    /// USB hub siblings of an input node's digitizer, read from sysfs: walk up
    /// to the USB device, then list the other USB devices under the same hub.
    /// Empty when the node has no USB ancestor (virtual or I2C devices).
    /// </summary>
    internal static IReadOnlyList<string> ReadCompanions(string sysName, string sysRoot = "/sys")
    {
        var result = new List<string>();
        try
        {
            var link = new DirectoryInfo(Path.Combine(sysRoot, "class", "input", sysName));
            var dir = (link.ResolveLinkTarget(true) as DirectoryInfo) ?? link;
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "idVendor")))
            {
                dir = dir.Parent;
            }
            if (dir?.Parent is null)
            {
                return result;
            }
            foreach (var sibling in dir.Parent.EnumerateDirectories())
            {
                if (sibling.FullName == dir.FullName)
                {
                    continue;
                }
                var vid = ReadFile(Path.Combine(sibling.FullName, "idVendor"));
                var pid = ReadFile(Path.Combine(sibling.FullName, "idProduct"));
                if (vid is not null && pid is not null)
                {
                    result.Add($"VID_{vid.ToUpperInvariant()}&PID_{pid.ToUpperInvariant()}");
                }
            }
        }
        catch (Exception)
        {
            // A failed walk yields no companions, the same fail-open the Windows snapshot has for the Y70 entry.
        }
        return result;
    }

    private static string? ReadFile(string path) => File.Exists(path) ? File.ReadAllText(path).Trim() : null;
}
