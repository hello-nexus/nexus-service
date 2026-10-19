using System;
using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Peripherals.LianLiFlex;

/// <summary>One shipped fan profile: temperature (Celsius) to target RPM points.</summary>
public sealed record FlexProfileDef(string Key, int FanSize, IReadOnlyList<(int Temp, int Rpm)> Points);

/// <summary>The shipped profile set and the RPM-to-duty conversion of each family.</summary>
public static class FlexFanProfiles
{
    public const int TlMaxRpm = 1900;
    public const int MinTemp = 0;
    public const int MaxTemp = 110;

    public static readonly IReadOnlyList<string> PresetKeys = new[] { "quiet", "stdsp", "highsp", "fullsp" };
    public static readonly IReadOnlyList<string> CustomKeys = new[] { "custom1", "custom2", "custom3", "custom4" };

    private static readonly (int Temp, int Rpm)[] Quiet = { (25, 365), (45, 520), (65, 860), (80, 1380), (90, 1900) };
    private static readonly (int Temp, int Rpm)[] StdSp = { (25, 365), (40, 720), (55, 1125), (70, 1500), (85, 1900) };
    private static readonly (int Temp, int Rpm)[] HighSp = { (25, 365), (40, 1042), (55, 1385), (70, 1730), (80, 1900) };
    private static readonly (int Temp, int Rpm)[] FullSp = { (25, 1900), (50, 1900), (75, 1900), (100, 1900) };

    // Duty percent at the RPMs the SL-INF fan is specified against.
    private static readonly (int Rpm, int Duty)[] SlInfDuty = { (365, 10), (520, 20), (860, 40), (1380, 70), (2000, 100) };

    public static bool IsKnown(string key) =>
        PresetKeys.Contains(key) || CustomKeys.Contains(key) || key == "fixed";

    /// <summary>The shipped points of a preset; the same set serves both fan sizes.</summary>
    public static FlexProfileDef? Shipped(string key, int fanSize)
    {
        var points = key switch
        {
            "quiet" => Quiet,
            "stdsp" => StdSp,
            "highsp" => HighSp,
            "fullsp" => FullSp,
            _ => null,
        };
        return points is null ? null : new FlexProfileDef(key, fanSize == 140 ? 140 : 120, points);
    }

    /// <summary>All shipped presets for both fan sizes, then the four custom slots with their default (StdSP) points.</summary>
    public static IReadOnlyList<FlexProfileDef> Catalog()
    {
        var list = new List<FlexProfileDef>();
        foreach (var size in new[] { 120, 140 })
        {
            foreach (var key in PresetKeys)
            {
                list.Add(Shipped(key, size)!);
            }
            foreach (var key in CustomKeys)
            {
                list.Add(new FlexProfileDef(key, size, StdSp));
            }
        }
        return list;
    }

    /// <summary>Target RPM to duty percent: TL by its maximum speed, SL-INF by its duty table.</summary>
    public static int RpmToDuty(FlexFamily family, int rpm)
    {
        if (family == FlexFamily.TlFlex)
        {
            return Math.Clamp((int)Math.Round(rpm * 100.0 / TlMaxRpm), 0, 100);
        }
        if (rpm <= SlInfDuty[0].Rpm)
        {
            return SlInfDuty[0].Duty;
        }
        for (var i = 1; i < SlInfDuty.Length; i++)
        {
            if (rpm <= SlInfDuty[i].Rpm)
            {
                var (r0, d0) = SlInfDuty[i - 1];
                var (r1, d1) = SlInfDuty[i];
                return (int)Math.Round(d0 + ((rpm - r0) * (double)(d1 - d0) / (r1 - r0)));
            }
        }
        return 100;
    }

    /// <summary>The cooling-curve points (Celsius, duty percent) of a profile; Start/Stop adds a stop point just below the first.</summary>
    public static List<(int Temp, int Duty)> DutyPoints(FlexFamily family, IReadOnlyList<(int Temp, int Rpm)> points, bool startStop)
    {
        var result = new List<(int Temp, int Duty)>();
        var ordered = points.OrderBy(p => p.Temp).ToList();
        if (startStop && ordered.Count > 0)
        {
            result.Add((Math.Max(MinTemp, ordered[0].Temp - 1), 0));
        }
        foreach (var (temp, rpm) in ordered)
        {
            result.Add((Math.Clamp(temp, MinTemp, MaxTemp), RpmToDuty(family, rpm)));
        }
        return result;
    }

    /// <summary>Sorts, clamps and de-duplicates user points; null when fewer than two remain.</summary>
    public const int CurveMaxTemp = 100;
    public const int CurveMinRpm = 350;

    /// <summary>A user-edited curve: temperatures 0..100, speeds 350..max, one point per temperature, null with fewer than two.</summary>
    public static List<(int Temp, int Rpm)>? Sanitize(IEnumerable<(int Temp, int Rpm)> points, int maxRpm = TlMaxRpm)
    {
        var map = new SortedDictionary<int, int>();
        foreach (var (t, r) in points)
        {
            map[Math.Clamp(t, MinTemp, CurveMaxTemp)] = Math.Clamp(r, CurveMinRpm, maxRpm);
        }
        return map.Count < 2 ? null : map.Select(kv => (kv.Key, kv.Value)).ToList();
    }
}
