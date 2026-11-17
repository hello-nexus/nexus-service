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
    // The 2023 Kraken is the one model where LHM's own polling was shown to fight the hub's LCD
    // stream, so it is the only one the hub drives. Every other model keeps LHM polling.
    internal const int HubDrivenProductId = 0x300E;
    internal const string HubDrivenLhmName = "NZXT Kraken";

    /// <summary>
    /// True for the LHM cooler the connected hub is driving, so LHM leaves it unpolled.
    /// <paramref name="sameNameCount"/> is how many LHM coolers carry this name: with two, the hub cannot
    /// tell which one it is, so LHM keeps polling both.
    /// </summary>
    public static bool IsHubDriven(KrakenHub? hub, string hardwareName, int sameNameCount) =>
        hub is not null && IsHubDriven(hub.IsConnected, hub.Model.ProductId, hardwareName, sameNameCount);

    internal static bool IsHubDriven(bool connected, int productId, string hardwareName, int sameNameCount) =>
        connected && productId == HubDrivenProductId && sameNameCount == 1
            && string.Equals(HubDrivenLhmName, hardwareName, StringComparison.Ordinal);

    /// <summary>The hub's liquid temperature, or null while it has no valid, fresh reading.</summary>
    public static float? Liquid(KrakenHub hub) => Liquid(hub.Snapshot.LiquidTempC, hub.StatusFresh);

    internal static float? Liquid(double liquidTempC, bool statusFresh) =>
        double.IsFinite(liquidTempC) && liquidTempC > 0 && statusFresh ? (float)liquidTempC : null;

    /// <summary>
    /// Replaces the values of LHM's own Kraken sensors in place; ids, names and parent are untouched.
    /// Without a valid liquid reading (none yet, or the status gone stale) the sensors stay, so a bound
    /// widget keeps its id, and read as unavailable: value 0 and a "-" text with no number, which the
    /// web shows as "-". Neither a frozen LHM number nor a stale hub value is shown.
    /// <paramref name="range"/> carries session min/max per sensor id; null clears them.
    /// </summary>
    public static void Apply(HardwareComponent component, KrakenSnapshot snap, bool statusFresh, ConcurrentDictionary<string, (float Min, float Max)>? range)
    {
        var valid = double.IsFinite(snap.LiquidTempC) && snap.LiquidTempC > 0 && statusFresh;
        foreach (var s in component.Sensors)
        {
            var value = ValueFor(s, snap);
            if (value is null)
            {
                continue;
            }
            if (!valid || !float.IsFinite(value.Value))
            {
                s.Value = 0;
                s.Min = 0;
                s.Max = 0;
                s.Formatted = Unavailable;
                s.FormattedMin = Unavailable;
                s.FormattedMax = Unavailable;
                continue;
            }
            var v = value.Value;
            var (min, max) = range is null ? (0f, 0f) : range.AddOrUpdate(s.Id, (v, v), (_, r) => (Math.Min(r.Min, v), Math.Max(r.Max, v)));
            s.Value = v;
            s.Min = min;
            s.Max = max;
            s.Formatted = SensorValueText.Format(v, s.Type);
            s.FormattedMin = SensorValueText.Format(min, s.Type);
            s.FormattedMax = SensorValueText.Format(max, s.Type);
        }
    }

    private const string Unavailable = "-";

    private static float? ValueFor(HardwareSensor s, KrakenSnapshot snap) => (s.Name, s.Type) switch
    {
        ("Liquid", "Temperature") => (float)snap.LiquidTempC,
        ("Pump", "Fan") => snap.PumpRpm,
        ("Pump Control", "Control") => snap.PumpDuty,
        ("Fans", "Fan") => snap.FanRpm,
        ("Fans Control", "Control") => snap.FanDuty,
        _ => null,
    };
}
