using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Nexus.Service.Platform.Linux;

/// <summary>
/// Binds the Y70 touch device to the Y70 output under Hyprland with the monitor's transform, so a touch
/// lands on the panel and not the desktop. Runtime keyword is rejected under a Lua config, so this
/// evals hl.device. The binding lives until Hyprland reloads its config; it is re-applied on each kiosk spawn.
/// </summary>
internal static class LinuxHyprlandTouchMap
{
    private const long MaxConfigFileBytes = 1_000_000;

    internal enum Outcome { Mapped, NoMonitor, NoTouchDevice, Ambiguous, UserConfigured, Rejected }

    /// <summary>The one Hyprland touch device that is a Y70 digitizer, or null when none or several match.</summary>
    internal static (string? Name, Outcome Outcome) SelectDevice(IReadOnlyList<string> hyprlandTouch, IReadOnlyList<string> y70KernelNames)
    {
        var wanted = y70KernelNames.Select(HyprlandIpc.NormalizeDeviceName).ToHashSet(StringComparer.Ordinal);
        var matches = hyprlandTouch.Where(wanted.Contains).Distinct(StringComparer.Ordinal).ToList();
        return matches.Count switch
        {
            0 => (null, Outcome.NoTouchDevice),
            1 => (matches[0], Outcome.Mapped),
            _ => (null, Outcome.Ambiguous),
        };
    }

    /// <summary>True when an uncommented line of the user's config names the device or sets the global touch binding.</summary>
    internal static bool UserConfigClaims(string configText, string deviceName)
    {
        foreach (var raw in configText.Split('\n'))
        {
            var line = raw.TrimStart();
            if (line.StartsWith("--", StringComparison.Ordinal) || line.StartsWith('#'))
                continue;
            if (line.Contains(deviceName, StringComparison.OrdinalIgnoreCase)
                || line.Contains("touchdevice", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    internal static string? EvalCommand(string deviceName, string connector, int transform)
    {
        if (HyprlandIpc.LuaString(deviceName) is not { } name || HyprlandIpc.LuaString(connector) is not { } output)
            return null;
        return $"/eval hl.device({{ name = {name}, output = {output}, transform = {transform} }})";
    }

    /// <summary>The session user's hypr config files, concatenated; empty when unreadable.</summary>
    internal static string ReadUserConfigText()
    {
        try
        {
            var home = LinuxSession.SessionHome ?? Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrEmpty(home))
                return "";
            var dir = Path.Combine(home, ".config", "hypr");
            if (!Directory.Exists(dir))
                return "";
            // Symlinks are skipped, and a zero Length also drops FIFOs and devices, which would block a read.
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 3,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
            };
            var text = new StringBuilder();
            foreach (var file in Directory.EnumerateFiles(dir, "*", options))
            {
                if (file.EndsWith(".lua", StringComparison.Ordinal) || file.EndsWith(".conf", StringComparison.Ordinal))
                    text.Append(ReadCapped(file)).Append('\n');
            }
            return text.ToString();
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static string ReadCapped(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null || info.Length == 0)
            return "";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[(int)Math.Min(MaxConfigFileBytes, info.Length)];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n <= 0)
                break;
            read += n;
        }
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    internal static Outcome Apply(Func<string, string?> send, string connector, IReadOnlyList<string> y70KernelNames, string userConfigText)
    {
        var outcome = ApplyCore(send, connector, y70KernelNames, userConfigText);
        Console.Error.WriteLine($"[panel-kiosk] Hyprland touch map: {outcome}");
        return outcome;
    }

    private static Outcome ApplyCore(Func<string, string?> send, string connector, IReadOnlyList<string> y70KernelNames, string userConfigText)
    {
        var monitor = HyprlandIpc.ParseMonitors(send("j/monitors"))
            .Where(m => string.Equals(m.Name, connector, StringComparison.OrdinalIgnoreCase))
            .Cast<HyprlandIpc.Monitor?>().FirstOrDefault();
        if (monitor is null)
            return Outcome.NoMonitor;
        var (name, outcome) = SelectDevice(HyprlandIpc.ParseTouchDevices(send("j/devices")), y70KernelNames);
        if (name is null)
            return outcome;
        if (HyprlandIpc.ParseSetStringOption(send("j/getoption input:touchdevice:output")) is not null
            || UserConfigClaims(userConfigText, name))
        {
            return Outcome.UserConfigured;
        }
        if (EvalCommand(name, monitor.Value.Name, monitor.Value.Transform) is not { } command)
            return Outcome.Rejected;
        return HyprlandIpc.IsOk(send(command)) ? Outcome.Mapped : Outcome.Rejected;
    }
}
