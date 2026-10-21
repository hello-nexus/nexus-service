using System;
using System.Collections.Generic;
using System.Globalization;

namespace Nexus.Service.Peripherals.LianLiFlex;

/// <summary>One fan unit as found on the bus: its receiver, and its screen when one sits behind the same hub.</summary>
public sealed class FlexUnitPort
{
    public required string ReceiverPath { get; init; }
    public required int ReceiverPid { get; init; }
    public string ScreenPath { get; init; } = "";
    public int ScreenPid { get; init; }
    public string ScreenSerial { get; init; } = "";
    /// <summary>Instance id of the hub both devices hang off; empty when it could not be resolved.</summary>
    public string HubId { get; init; } = "";

    public FlexFamily Family => ReceiverPid == 0 ? FlexProtocol.FamilyOfScreen(ScreenPid) : FlexProtocol.FamilyOfReceiver(ReceiverPid);
}

public interface IFlexDiscovery
{
    IReadOnlyList<FlexUnitPort> Discover();

    /// <summary>Units and receiverless screens from one enumeration, so a screen never appears in both.</summary>
    FlexDiscoverySnapshot Snapshot() => new(Discover(), Array.Empty<FlexUnitPort>());
}

/// <summary>Paired units, and the screens with no receiver on the bus (fans run over the radio, only the screen is on USB).</summary>
public sealed record FlexDiscoverySnapshot(IReadOnlyList<FlexUnitPort> Units, IReadOnlyList<FlexUnitPort> ScreenOnly);

public sealed class StubFlexDiscovery : IFlexDiscovery
{
    public IReadOnlyList<FlexUnitPort> Discover() => Array.Empty<FlexUnitPort>();
}

/// <summary>A device interface path with its parsed ids.</summary>
public readonly record struct FlexPath(string Path, int Vid, int Pid, string Serial, string InstanceId);

/// <summary>Pure classification of device interface paths; the platform discovery only enumerates.</summary>
public static class FlexDiscoveryParser
{
    /// <summary>Paths look like \\?\USB#VID_43A8&amp;PID_0102#&lt;instance or serial&gt;#{guid}.</summary>
    public static bool TryParse(string path, out FlexPath parsed)
    {
        parsed = default;
        var vidIdx = path.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        var pidIdx = path.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
        if (vidIdx < 0 || pidIdx < 0 || vidIdx + 8 > path.Length || pidIdx + 8 > path.Length)
        {
            return false;
        }
        if (!int.TryParse(path.AsSpan(vidIdx + 4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var vid)
            || !int.TryParse(path.AsSpan(pidIdx + 4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var pid))
        {
            return false;
        }
        var parts = path.Split('#');
        var serial = parts.Length > 2 ? parts[2] : "";
        var instance = $"USB\\VID_{vid:X4}&PID_{pid:X4}\\{serial}";
        parsed = new FlexPath(path, vid, pid, serial, instance);
        return true;
    }

    /// <summary>Pairs once and lists every screen no receiver claimed; each screen is in exactly one of the two lists.</summary>
    public static FlexDiscoverySnapshot Snapshot(IEnumerable<string> paths, Func<string, string> hubOf)
    {
        var list = paths as IReadOnlyCollection<string> ?? new List<string>(paths);
        var units = Pair(list, hubOf);
        var paired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in units)
        {
            if (u.ScreenPath.Length > 0)
            {
                paired.Add(u.ScreenPath);
            }
        }
        var screenOnly = new List<FlexUnitPort>();
        var seenSerials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in list)
        {
            if (TryParse(raw, out var p) && FlexProtocol.IsScreenProduct(p.Vid, p.Pid) && !paired.Contains(p.Path) && seenSerials.Add(p.Serial))
            {
                screenOnly.Add(new FlexUnitPort
                {
                    ReceiverPath = "",
                    ReceiverPid = 0,
                    ScreenPath = p.Path,
                    ScreenPid = p.Pid,
                    ScreenSerial = p.Serial,
                    HubId = hubOf(p.InstanceId),
                });
            }
        }
        return new FlexDiscoverySnapshot(units, screenOnly);
    }

    /// <summary>
    /// Keeps only the Flex receivers and screens and pairs them: by the resolved parent hub when
    /// <paramref name="hubOf"/> knows it, else by family when exactly one of each is present.
    /// </summary>
    public static IReadOnlyList<FlexUnitPort> Pair(IEnumerable<string> paths, Func<string, string> hubOf)
    {
        var receivers = new List<FlexPath>();
        var screens = new List<FlexPath>();
        foreach (var raw in paths)
        {
            if (!TryParse(raw, out var p))
            {
                continue;
            }
            if (FlexProtocol.IsReceiverProduct(p.Vid, p.Pid))
            {
                receivers.Add(p);
            }
            else if (FlexProtocol.IsScreenProduct(p.Vid, p.Pid))
            {
                screens.Add(p);
            }
        }
        var units = new List<FlexUnitPort>(receivers.Count);
        var usedScreens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in receivers)
        {
            var family = FlexProtocol.FamilyOfReceiver(r.Pid);
            var hub = hubOf(r.InstanceId);
            FlexPath? match = null;
            foreach (var s in screens)
            {
                if (FlexProtocol.FamilyOfScreen(s.Pid) != family || usedScreens.Contains(s.Path))
                {
                    continue;
                }
                if (hub.Length > 0 && string.Equals(hubOf(s.InstanceId), hub, StringComparison.OrdinalIgnoreCase))
                {
                    match = s;
                    break;
                }
            }
            if (match is null && hub.Length == 0)
            {
                var sameFamilyReceivers = receivers.FindAll(x => FlexProtocol.FamilyOfReceiver(x.Pid) == family).Count;
                var sameFamilyScreens = screens.FindAll(x => FlexProtocol.FamilyOfScreen(x.Pid) == family);
                if (sameFamilyReceivers == 1 && sameFamilyScreens.Count == 1)
                {
                    match = sameFamilyScreens[0];
                }
            }
            if (match is { } m)
            {
                usedScreens.Add(m.Path);
            }
            units.Add(new FlexUnitPort
            {
                ReceiverPath = r.Path,
                ReceiverPid = r.Pid,
                ScreenPath = match?.Path ?? "",
                ScreenPid = match?.Pid ?? 0,
                ScreenSerial = match?.Serial ?? "",
                HubId = hub,
            });
        }
        return units;
    }
}
