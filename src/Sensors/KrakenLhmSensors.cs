using System;
using System.Collections.Concurrent;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Peripherals.Nzxt;

namespace Nexus.Service.Sensors;

/// <summary>
/// LibreHardwareMonitor stays the one sensor list for a Kraken, but while <see cref="KrakenHub"/>
/// drives the cooler LHM must not poll it: its entries are filled from the hub's readings instead.
/// </summary>
internal static class KrakenLhmSensors
{
    /// <summary>Name LHM's Kraken driver gives the hardware for a USB product id; null where that driver does not claim the id.</summary>
    public static string? LhmName(int productId) => productId switch
    {
        0x3008 => "NZXT Kraken Z3",
        0x300C => "NZXT Kraken Elite",
        0x3012 => "NZXT Kraken Elite v2",
        0x300E => "NZXT Kraken",
        0x2007 or 0x2014 => "NZXT Kraken X3",
        _ => null,
    };

    /// <summary>True for the LHM cooler the connected hub is driving, so LHM leaves it unpolled.</summary>
    public static bool IsHubDriven(KrakenHub? hub, string hardwareName) =>
        hub is not null && IsHubDriven(hub.IsConnected, hub.Model.ProductId, hardwareName);

    internal static bool IsHubDriven(bool connected, int productId, string hardwareName) =>
        connected && LhmName(productId) is { } name && string.Equals(name, hardwareName, StringComparison.Ordinal);

    /// <summary>The hub's liquid temperature, or null while it has no valid, fresh reading.</summary>
    public static float? Liquid(KrakenHub hub) => Liquid(hub.Snapshot.LiquidTempC, hub.StatusFresh);

    internal static float? Liquid(double liquidTempC, bool statusFresh) =>
        double.IsFinite(liquidTempC) && liquidTempC > 0 && statusFresh ? (float)liquidTempC : null;

    /// <summary>
    /// Replaces the values of LHM's own Kraken sensors in place; ids, names and parent are untouched.
    /// Without a valid liquid reading (none yet, or the status gone stale) the sensors are dropped,
    /// as the cooling provider offers no source then, rather than showing a frozen LHM number.
    /// <paramref name="range"/> carries session min/max per sensor id; null clears them.
    /// </summary>
    public static void Apply(HardwareComponent component, KrakenSnapshot snap, bool statusFresh, ConcurrentDictionary<string, (float Min, float Max)>? range)
    {
        var valid = double.IsFinite(snap.LiquidTempC) && snap.LiquidTempC > 0 && statusFresh;
        for (var i = component.Sensors.Count - 1; i >= 0; i--)
        {
            var s = component.Sensors[i];
            var value = ValueFor(s, snap);
            if (value is null)
            {
                continue;
            }
            if (!valid || !float.IsFinite(value.Value))
            {
                component.Sensors.RemoveAt(i);
                continue;
            }
            var v = value.Value;
            var (min, max) = range is null ? (0f, 0f) : range.AddOrUpdate(s.Id, (v, v), (_, r) => (Math.Min(r.Min, v), Math.Max(r.Max, v)));
            s.Value = v;
            s.Min = min;
            s.Max = max;
            s.Formatted = Format(v, s.Type);
            s.FormattedMin = Format(min, s.Type);
            s.FormattedMax = Format(max, s.Type);
        }
    }

    private static float? ValueFor(HardwareSensor s, KrakenSnapshot snap) => (s.Name, s.Type) switch
    {
        ("Liquid", "Temperature") => (float)snap.LiquidTempC,
        ("Pump", "Fan") => snap.PumpRpm,
        ("Pump Control", "Control") => snap.PumpDuty,
        ("Fans", "Fan") => snap.FanRpm,
        ("Fans Control", "Control") => snap.FanDuty,
        _ => null,
    };

    private static string Format(float v, string type) => type switch
    {
        "Temperature" => $"{v:F1} °C",
        "Fan" => $"{v:F0} RPM",
        _ => $"{v:F1}%",
    };
}
