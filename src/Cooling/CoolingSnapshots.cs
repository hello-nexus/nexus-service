using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Persistence;

namespace Nexus.Service.Cooling;

/// <summary>
/// Copies of the live cooling collections the store hands out. A route thread can edit them
/// while the engine walks them; a copy that races a structural change is retried.
/// </summary>
internal static class CoolingSnapshots
{
    private const int Attempts = 4;

    public static List<CurveDocument> Curves(CoolingSettings cooling) => Retry(() => cooling.Curves.ToList());

    public static List<string> OutputIds(CurveDocument curve) => Retry(() => curve.Outputs.Select(o => o.Id).ToList());

    public static Dictionary<string, int> ManualSpeeds(CoolingSettings cooling) => Retry(() => Copy(cooling.ManualSpeeds));

    public static Dictionary<string, int> GpuManualBackup(CoolingSettings cooling) => Retry(() => Copy(cooling.GpuManualBackup));

    private static Dictionary<string, int> Copy(Dictionary<string, int> source)
    {
        var copy = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var kv in source)
        {
            copy[kv.Key] = kv.Value;
        }
        return copy;
    }

    private static T Retry<T>(Func<T> read)
    {
        for (var i = 1; i < Attempts; i++)
        {
            try { return read(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { }
        }
        return read();
    }
}
