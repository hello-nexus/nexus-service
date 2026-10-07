using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;

namespace Nexus.Service.Cooling;

public static class CoolingHazardKinds
{
    public const string FollowsStoppableSource = "follows-stoppable-source";
    public const string NonCpuSensor = "non-cpu-sensor";
    public const string LowCeiling = "low-ceiling";
    public const string ManualLow = "manual-low";
}

/// <summary>Severe (save warnings): fans that can stop while the CPU is hot. AfterTrip (trip-end heal) adds configs a trip proved too weak.</summary>
public enum LintScope
{
    Severe,
    AfterTrip,
}

public sealed record LintHazard(string ChannelId, string ChannelName, string Kind, string RootId, string RootName);

public sealed class LintInput
{
    public IReadOnlyList<CurveDocument> Curves { get; init; } = Array.Empty<CurveDocument>();
    public IReadOnlyList<FanChannel> Channels { get; init; } = Array.Empty<FanChannel>();
    public IReadOnlyList<TemperatureSource> Sources { get; init; } = Array.Empty<TemperatureSource>();
    public IReadOnlyDictionary<string, string> FanRoles { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, int> ManualSpeeds { get; init; } = new Dictionary<string, int>();
    public IReadOnlyCollection<string> Uncontrolled { get; init; } = Array.Empty<string>();
    public double LimitC { get; init; } = ThermalLimits.GenericDefaultC;
    public LintScope Scope { get; init; } = LintScope.Severe;
}

public sealed record HealResult(List<CurveDocument> Curves, List<LintHazard> Healed, List<string> ManualDrops);

/// <summary>
/// Pure hazard detection and heal transform for fan curve configs. A hazard is a
/// CPU-cooling channel whose duty can sit low while the CPU is hot.
/// </summary>
public static class CoolingConfigLint
{
    /// <summary>The one curve every healed channel follows.</summary>
    public const string GuardCurveId = "guard-cpu";

    // Leftovers of an earlier heal design: removed on the next heal, their outputs joining the shared curve.
    private const string LegacySafeCurveId = "guard-safe-cpu";
    private const string LegacyMixIdPrefix = "guard-mix-";
    private const string LegacyManualIdPrefix = "guard-manual-";

    // Fan speed points of the shared curve as offsets below the limit: (degrees under, duty percent).
    private static readonly (double Below, double Duty)[] GuardCurveShape =
    {
        (55, 30), (40, 40), (25, 60), (10, 85), (0, 100),
    };

    // Below this a fan stops or barely turns.
    private const double StopPercent = 20;
    private const double AfterTripMinCeilingPercent = 60;
    private const double AfterTripMinManualPercent = 30;

    /// <summary>
    /// CPU-cooling: everything that is not a GPU fan. An unknown channel counts as
    /// CPU-cooling (conservative), the same as a motherboard header.
    /// </summary>
    public static bool IsCpuCooling(FanChannel ch, IReadOnlyDictionary<string, string> roles)
    {
        var role = roles.TryGetValue(ch.Id, out var r) ? r : (ch.IsGpu ? FanRoleKind.Gpu : FanRoleKind.None);
        if (role == FanRoleKind.Cpu)
        {
            return true;
        }
        return role != FanRoleKind.Gpu && !ch.IsGpu;
    }

    private static bool IsGpuFan(FanChannel ch, IReadOnlyDictionary<string, string> roles) =>
        ch.IsGpu || (roles.TryGetValue(ch.Id, out var r) && r == FanRoleKind.Gpu);

    public static List<LintHazard> Analyze(LintInput input)
    {
        var result = new List<LintHazard>();
        var byId = input.Channels.ToDictionary(c => c.Id, StringComparer.Ordinal);
        foreach (var ch in input.Channels)
        {
            if (!IsCpuCooling(ch, input.FanRoles) || input.Uncontrolled.Contains(ch.Id))
            {
                continue;
            }
            var curve = input.Curves.FirstOrDefault(c => c.Outputs.Any(o => o.Id == ch.Id));
            if (curve is null)
            {
                var minManual = input.Scope == LintScope.AfterTrip ? AfterTripMinManualPercent : StopPercent;
                if (input.ManualSpeeds.TryGetValue(ch.Id, out var manual) && manual < minManual)
                {
                    result.Add(new LintHazard(ch.Id, ch.Name, CoolingHazardKinds.ManualLow, ch.Id, ch.Name));
                }
                continue;
            }
            var found = Evaluate(curve, input, byId, new HashSet<string>(StringComparer.Ordinal));
            if (found is { } h)
            {
                result.Add(new LintHazard(ch.Id, ch.Name, h.Kind, h.RootId, h.RootName));
            }
        }
        return result;
    }

    private readonly record struct Found(string Kind, string RootId, string RootName);

    private static Found? Evaluate(
        CurveDocument curve,
        LintInput input,
        Dictionary<string, FanChannel> byId,
        HashSet<string> visited)
    {
        if (!visited.Add(curve.Id))
        {
            return null;
        }
        switch (curve.Type)
        {
            case "Sync":
                return EvaluateSync(curve, input, byId, visited);
            case "Mixed":
                return EvaluateMixed(curve, input, byId, visited);
            default:
                return EvaluateTemperatureCurve(curve, input);
        }
    }

    private static Found? EvaluateSync(CurveDocument curve, LintInput input, Dictionary<string, FanChannel> byId, HashSet<string> visited)
    {
        var sourceId = curve.Sync?.SourceChannelId ?? "";
        if (sourceId.Length == 0 || !byId.TryGetValue(sourceId, out var source))
        {
            return null;
        }
        var stoppable = new Found(CoolingHazardKinds.FollowsStoppableSource, source.Id, source.Name);
        if (IsGpuFan(source, input.FanRoles) || input.Uncontrolled.Contains(source.Id))
        {
            return stoppable;
        }
        var driver = input.Curves.FirstOrDefault(c => c.Outputs.Any(o => o.Id == sourceId));
        if (driver is null)
        {
            return stoppable;
        }
        return Evaluate(driver, input, byId, visited);
    }

    private static Found? EvaluateMixed(CurveDocument curve, LintInput input, Dictionary<string, FanChannel> byId, HashSet<string> visited)
    {
        var members = (curve.Mixed?.CurveIds ?? new List<string>())
            .Select(id => input.Curves.FirstOrDefault(c => c.Id == id))
            .Where(c => c is not null)
            .Select(c => c!)
            .ToList();
        if (members.Count == 0)
        {
            return null;
        }
        Found? first = null;
        var isMax = (curve.Mixed?.Fn ?? "max") == "max";
        foreach (var m in members)
        {
            var f = Evaluate(m, input, byId, new HashSet<string>(visited, StringComparer.Ordinal));
            if (f is null)
            {
                if (isMax)
                {
                    return null;
                }
                continue;
            }
            first ??= f;
        }
        return first;
    }

    private static Found? EvaluateTemperatureCurve(CurveDocument curve, LintInput input)
    {
        if (curve.Type is "Linear" or "Graph" or "Trigger" or "Auto" && curve.Input.Id.Length > 0)
        {
            var src = input.Sources.FirstOrDefault(s => s.Id == curve.Input.Id);
            // A cooler's liquid temperature rises with CPU heat, so on save it is judged like a CPU sensor.
            var liquid = src?.Category == "Cooler" && input.Scope == LintScope.Severe;
            if (src is not null && src.Category != "CPU" && !liquid)
            {
                if (input.Scope == LintScope.Severe && FloorOf(curve) is { } floor && floor >= StopPercent)
                {
                    return null;
                }
                return new Found(CoolingHazardKinds.NonCpuSensor, src.Id, src.Name);
            }
        }
        var ceiling = CeilingAt(curve, input.LimitC);
        var minCeiling = input.Scope == LintScope.AfterTrip ? AfterTripMinCeilingPercent : StopPercent;
        if (ceiling is { } c && c < minCeiling)
        {
            return new Found(CoolingHazardKinds.LowCeiling, curve.Id, curve.Name);
        }
        return null;
    }

    private static double? CeilingAt(CurveDocument curve, double limitC) => curve.Type switch
    {
        "Flat" => curve.Flat?.Speed,
        "Linear" => CurveEngine.EvaluateLinear(curve.Linear, (float)limitC),
        "Graph" => curve.Graph is null ? null : CurveEngine.EvaluateGraph(
            new GraphCurveData
            {
                SpeedModifier = curve.Graph.SpeedModifier,
                Points = curve.Graph.Points.Select(p => new Persistence.GraphPoint { Temp = p.Temp, Speed = p.Speed }).ToList(),
            },
            (float)limitC),
        "Trigger" => curve.Trigger is null ? null : (limitC >= curve.Trigger.LoadTemp ? curve.Trigger.LoadSpeed : curve.Trigger.IdleSpeed),
        "Auto" => curve.Auto?.MaxSpeed,
        _ => null,
    };

    /// <summary>The lowest duty a curve can give, whatever its input reads.</summary>
    private static double? FloorOf(CurveDocument curve) => curve.Type switch
    {
        "Linear" => curve.Linear is null ? null : Math.Min(curve.Linear.MinSpeed, curve.Linear.MaxSpeed),
        "Graph" => curve.Graph is null || curve.Graph.Points.Count == 0 ? null : curve.Graph.Points.Min(p => p.Speed) * curve.Graph.SpeedModifier,
        "Trigger" => curve.Trigger is null ? null : Math.Min(curve.Trigger.IdleSpeed, curve.Trigger.LoadSpeed),
        "Auto" => curve.Auto?.MinSpeed,
        _ => null,
    };

    /// <summary>Sets the shared curve to its computed form: a Graph on the CPU input with the limit-relative points and the Balanced preset's response time.</summary>
    public static void ApplyGuardCurve(CurveDocument curve, TemperatureSource cpuInput, double limitC)
    {
        curve.Name = "Thermal guard";
        curve.Preset = null;
        curve.Outputs = (curve.Outputs ?? new List<CurveOutputDocument>()).Where(o => o is not null).ToList();
        curve.Type = "Graph";
        curve.Input = new CurveInputDocument { Id = cpuInput.Id, Type = "Temperature", Device = cpuInput.Category };
        curve.Flat = null;
        curve.Linear = null;
        curve.Mixed = null;
        curve.Trigger = null;
        curve.Sync = null;
        curve.Auto = null;
        curve.Graph = new GraphCurveData
        {
            ResponseTime = FanProfiles.PresetDefaults.For("balanced").ResponseTime,
            SpeedModifier = 1.0,
            Points = GuardCurvePoints(limitC),
        };
    }

    /// <summary>True when the stored curve already equals its computed form.</summary>
    public static bool GuardCurveMatches(CurveDocument curve, TemperatureSource cpuInput, double limitC)
    {
        // A curve with a missing part is simply not the computed form (so it gets repaired), never an error.
        if (curve.Input is null
            || curve.Outputs is null
            || curve.Outputs.Any(o => o is null)
            || curve.Graph is null
            || curve.Graph.Points is null
            || curve.Graph.Points.Any(p => p is null)
            || curve.Mixed is not null)
        {
            return false;
        }
        // Compared as wire shapes, so every field of the curve counts, not just the ones we know to check.
        var desired = new CurveDocument
        {
            Id = curve.Id,
            Outputs = curve.Outputs.Select(o => new CurveOutputDocument { Id = o.Id, Type = o.Type }).ToList(),
        };
        ApplyGuardCurve(desired, cpuInput, limitC);
        return WireJson(curve) == WireJson(desired);
    }

    private static string WireJson(CurveDocument curve) => System.Text.Json.JsonSerializer.Serialize(
        new SetCurvesBody { Curves = new List<Curve> { CurveWireMapper.ToWire(curve) } },
        Nexus.Service.Serialization.AppJsonContext.Default.SetCurvesBody);

    /// <summary>The input the guard curve reads: the one it already has while that is still a CPU source, otherwise the preferred one. A sensor that comes and goes elsewhere must not flip it.</summary>
    public static TemperatureSource? PickGuardInput(CurveDocument? existing, IReadOnlyList<TemperatureSource> sources)
    {
        var cpu = sources.Where(s => s.Category == "CPU").ToList();
        var kept = existing is null ? null : cpu.FirstOrDefault(s => s.Id == existing.Input?.Id);
        return kept ?? FanProfiles.PreferredInput(cpu);
    }

    /// <summary>The shared curve's points for a limit: a balanced curve relative to it.</summary>
    public static List<Persistence.GraphPoint> GuardCurvePoints(double limitC) =>
        GuardCurveShape
            .Select(p => new Persistence.GraphPoint { Temp = Math.Round(limitC - p.Below), Speed = p.Duty })
            .ToList();

    /// <summary>
    /// Move each hazardous channel onto one shared curve (<see cref="GuardCurveId"/>), removing its
    /// output from whatever drove it and dropping its manual speed. A second heal adds to the same
    /// curve. The input curves are never mutated; null when there is nothing to change or no CPU
    /// temperature source to build the curve on.
    /// </summary>
    public static HealResult? Heal(LintInput input, IReadOnlyList<LintHazard> hazards)
    {
        var cpuInput = PickGuardInput(input.Curves.FirstOrDefault(c => c.Id == GuardCurveId), input.Sources);
        var legacy = input.Curves.Where(IsLegacyGuardCurve).ToList();
        if (cpuInput is null || (hazards.Count == 0 && legacy.Count == 0))
        {
            return null;
        }

        var curves = input.Curves.Select(CloneCurve).ToList();
        var guard = curves.FirstOrDefault(c => c.Id == GuardCurveId);
        if (guard is null)
        {
            guard = new CurveDocument { Id = GuardCurveId };
            curves.Add(guard);
        }
        ApplyGuardCurve(guard, cpuInput, input.LimitC);

        // Channels an earlier design healed join the shared curve too.
        foreach (var old in curves.Where(IsLegacyGuardCurve).ToList())
        {
            foreach (var o in old.Outputs)
            {
                AddOutput(guard, o.Id, o.Type);
            }
            curves.Remove(old);
        }

        var healed = new List<LintHazard>();
        var manualDrops = guard.Outputs
            .Select(o => o.Id)
            .Where(input.ManualSpeeds.ContainsKey)
            .ToList(); // outputs already on the shared curve, migrated leftovers included: a manual speed would only fight it
        foreach (var hazard in hazards)
        {
            if (healed.Any(h => h.ChannelId == hazard.ChannelId))
            {
                continue;
            }
            var outputType = "Fan";
            foreach (var driver in curves.Where(c => c.Id != GuardCurveId && c.Outputs.Any(o => o.Id == hazard.ChannelId)))
            {
                outputType = driver.Outputs.First(o => o.Id == hazard.ChannelId).Type;
                driver.Outputs.RemoveAll(o => o.Id == hazard.ChannelId);
            }
            AddOutput(guard, hazard.ChannelId, outputType);
            if (input.ManualSpeeds.ContainsKey(hazard.ChannelId) && !manualDrops.Contains(hazard.ChannelId))
            {
                manualDrops.Add(hazard.ChannelId);
            }
            healed.Add(hazard);
        }
        return new HealResult(curves, healed, manualDrops);
    }

    private static bool IsLegacyGuardCurve(CurveDocument c) =>
        c.Id == LegacySafeCurveId
        || c.Id.StartsWith(LegacyMixIdPrefix, StringComparison.Ordinal)
        || c.Id.StartsWith(LegacyManualIdPrefix, StringComparison.Ordinal);

    private static void AddOutput(CurveDocument curve, string channelId, string type)
    {
        if (curve.Outputs.All(o => o.Id != channelId))
        {
            curve.Outputs.Add(new CurveOutputDocument { Id = channelId, Type = type });
        }
    }

    /// <summary>Curves the global modifier must not scale: Sync outputs already carry it, and the guard curve is a safety curve.</summary>
    internal static bool IsGlobalModifierExempt(CurveDocument member) => member.Type == "Sync" || member.Id == GuardCurveId;

    public static CurveDocument CloneCurve(CurveDocument d) => CurveWireMapper.ToDocument(CurveWireMapper.ToWire(d));
}
