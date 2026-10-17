using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Nexus.Service.Devices.Handlers;

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

    internal static bool IsY70Touch(InputDeviceInfo d) =>
        d.Touch && Y70Handler.TouchDigitizers.Any(t => t.VendorId == d.Vendor && t.ProductId == d.Product);

    /// <summary>Maps every Y70 touch node to <paramref name="connector"/>; idempotent.</summary>
    internal static (bool Ok, string Detail) Apply(string connector, BusRunner run)
    {
        var (treeExit, tree) = run(["tree", Service, "--list"]);
        if (treeExit != 0)
        {
            return (false, ToolFailure(treeExit, tree));
        }
        var paths = ParseDevicePaths(tree);
        if (paths.Count == 0)
        {
            return (false, "KWin reports no input devices (KDE Plasma Wayland required)");
        }

        var matched = new List<string>();
        foreach (var path in paths)
        {
            var (exit, text) = run(["get-property", Service, path, Interface, "name", "vendor", "product", "touch", "outputName"]);
            if (exit != 0 || ParseDevice(text) is not { } device || !IsY70Touch(device))
            {
                continue;
            }
            var node = path[DevicePrefix.Length..];
            if (device.OutputName != connector)
            {
                var (setExit, setOut) = run(["set-property", Service, path, Interface, "outputName", "s", connector]);
                if (setExit != 0)
                {
                    return (false, $"set outputName on event{node} failed: {setOut.Trim()}");
                }
                var (checkExit, after) = run(["get-property", Service, path, Interface, "outputName"]);
                if (checkExit != 0 || Unquote(after.Trim()) != connector)
                {
                    return (false, $"event{node} did not keep outputName '{connector}'");
                }
            }
            matched.Add($"event{node}");
        }
        return matched.Count == 0
            ? (false, "no Y70 touch digitizer among KWin input devices")
            : (true, $"touch {string.Join(',', matched)} mapped to {connector}");
    }

    private static string ToolFailure(int exit, string output) => exit switch
    {
        -1 => "busctl not found",
        -2 => "busctl timed out",
        -3 or -4 => output,
        _ => "KWin session bus not reachable (KDE Plasma Wayland required)",
    };
}
