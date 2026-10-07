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
}

public sealed record HealResult(List<CurveDocument> Curves, List<LintHazard> Healed, List<string> ManualDrops, bool GuardCurveRegenerated);

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

    private const double MinCeilingPercent = 60;
    private const double MinManualPercent = 30;

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
                if (input.ManualSpeeds.TryGetValue(ch.Id, out var manual) && manual < MinManualPercent)
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
            if (src is not null && src.Category != "CPU")
            {
                return new Found(CoolingHazardKinds.NonCpuSensor, src.Id, src.Name);
            }
        }
        var ceiling = CeilingAt(curve, input.LimitC);
        if (ceiling is { } c && c < MinCeilingPercent)
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
        var cpuSources = input.Sources.Where(s => s.Category == "CPU").ToList();
        var cpuInput = FanProfiles.PreferredInput(cpuSources);
        var legacy = input.Curves.Where(IsLegacyGuardCurve).ToList();
        if (cpuInput is null || (hazards.Count == 0 && legacy.Count == 0))
        {
            return null;
        }

        var curves = input.Curves.Select(CloneCurve).ToList();
        var guard = curves.FirstOrDefault(c => c.Id == GuardCurveId);
        // The shared curve is linted like any other: if hand edits broke a rule, a channel on it is
        // flagged, and healing rebuilds it. An intact one keeps its points, hand edits included.
        var broken = guard is not null && hazards.Any(h => guard.Outputs.Any(o => o.Id == h.ChannelId));
        var regenerate = guard is null || broken;
        if (guard is null)
        {
            guard = new CurveDocument { Id = GuardCurveId, Name = "Thermal guard" };
            curves.Add(guard);
        }
        if (regenerate)
        {
            guard.Type = "Graph";
            guard.Input = new CurveInputDocument { Id = cpuInput.Id, Type = "Temperature", Device = cpuInput.Category };
            guard.Flat = null;
            guard.Linear = null;
            guard.Mixed = null;
            guard.Trigger = null;
            guard.Sync = null;
            guard.Auto = null;
            guard.Graph = new GraphCurveData
            {
                ResponseTime = FanProfiles.PresetDefaults.For("balanced").ResponseTime,
                SpeedModifier = 1.0,
                Points = GuardCurvePoints(input.LimitC),
            };
        }

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
            .ToList(); // channels migrated from the earlier design: a stale manual speed would only fight the curve
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
        return new HealResult(curves, healed, manualDrops, regenerate);
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
