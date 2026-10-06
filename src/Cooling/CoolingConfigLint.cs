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

public sealed record HealResult(List<CurveDocument> Curves, List<LintHazard> Healed);

/// <summary>
/// Pure hazard detection and heal transform for fan curve configs. A hazard is a
/// CPU-cooling channel whose duty can sit low while the CPU is hot.
/// </summary>
public static class CoolingConfigLint
{
    public const string SafeCurveId = "guard-safe-cpu";
    public const string MixIdPrefix = "guard-mix-";
    public const string ManualIdPrefix = "guard-manual-";

    private const double MinCeilingPercent = 60;
    private const double MinManualPercent = 30;

    /// <summary>
    /// CPU-cooling: an explicit CPU role, a pump or AIO channel, or a motherboard header
    /// (no owning device) that is not a GPU fan. Hub fans without a role are case fans
    /// and are left out.
    /// </summary>
    public static bool IsCpuCooling(FanChannel ch, IReadOnlyDictionary<string, string> roles)
    {
        var role = roles.TryGetValue(ch.Id, out var r) ? r : (ch.IsGpu ? FanRoleKind.Gpu : FanRoleKind.None);
        if (role == FanRoleKind.Cpu)
        {
            return true;
        }
        if (role == FanRoleKind.Gpu || ch.IsGpu)
        {
            return false;
        }
        return ch.Kind == FanKinds.Pump || ch.IsAio || ch.DeviceId is null;
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

    /// <summary>
    /// Replace each hazardous channel's assignment with a Mixed max of (its original
    /// curve, a safe CPU Graph curve). The input curves are never mutated; null when
    /// there is no CPU temperature source to build the safe curve on.
    /// </summary>
    public static HealResult? Heal(LintInput input, IReadOnlyList<LintHazard> hazards)
    {
        var cpuSources = input.Sources.Where(s => s.Category == "CPU").ToList();
        var cpuInput = FanProfiles.PreferredInput(cpuSources);
        if (cpuInput is null || hazards.Count == 0)
        {
            return null;
        }

        var curves = input.Curves.Select(CloneCurve).ToList();
        var safe = curves.FirstOrDefault(c => c.Id == SafeCurveId);
        if (safe is null)
        {
            safe = FanProfiles.BuildPresetCurve("balanced", cpuInput);
            safe.Id = SafeCurveId;
            safe.Name = "Thermal guard (CPU)";
            safe.Preset = null;
            curves.Add(safe);
        }

        var healed = new List<LintHazard>();
        foreach (var hazard in hazards)
        {
            if (healed.Any(h => h.ChannelId == hazard.ChannelId))
            {
                continue;
            }
            string originalId;
            var outputType = "Fan";
            var original = curves.FirstOrDefault(c => c.Outputs.Any(o => o.Id == hazard.ChannelId));
            if (original is not null)
            {
                outputType = original.Outputs.First(o => o.Id == hazard.ChannelId).Type;
                original.Outputs.RemoveAll(o => o.Id == hazard.ChannelId);
                originalId = original.Id;
            }
            else
            {
                if (!input.ManualSpeeds.TryGetValue(hazard.ChannelId, out var manual))
                {
                    continue;
                }
                originalId = ManualIdPrefix + hazard.ChannelId;
                if (curves.All(c => c.Id != originalId))
                {
                    curves.Add(new CurveDocument
                    {
                        Id = originalId,
                        Name = hazard.ChannelName + " (manual)",
                        Type = "Flat",
                        Flat = new FlatCurveData { Speed = manual },
                    });
                }
            }

            var mixId = MixIdPrefix + hazard.ChannelId;
            curves.RemoveAll(c => c.Id == mixId);
            curves.Add(new CurveDocument
            {
                Id = mixId,
                Name = hazard.ChannelName + " (thermal guard)",
                Type = "Mixed",
                Mixed = new MixedCurveData { Fn = "max", CurveIds = new List<string> { originalId, SafeCurveId } },
                Outputs = new List<CurveOutputDocument> { new() { Id = hazard.ChannelId, Type = outputType } },
            });
            healed.Add(hazard);
        }
        return healed.Count == 0 ? null : new HealResult(curves, healed);
    }

    public static CurveDocument CloneCurve(CurveDocument d) => CurveWireMapper.ToDocument(CurveWireMapper.ToWire(d));
}
