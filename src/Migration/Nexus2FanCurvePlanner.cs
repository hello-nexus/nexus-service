using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nexus.Service.Cooling;
using Nexus.Service.Migration.FanControl;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;

namespace Nexus.Service.Migration;

/// <summary>Why a Nexus 2 curve or one of its outputs did not come over. The strings are the wire contract.</summary>
internal static class Nexus2FanCurveReasons
{
    public const string MixedCurve = "mixedCurve";
    public const string NonTemperatureInput = "nonTemperatureInput";
    public const string SensorNotFound = "sensorNotFound";
    public const string SensorAmbiguous = "sensorAmbiguous";
    public const string SensorImplausible = "sensorImplausible";
    public const string BadPoints = "badPoints";
    public const string UnsafeCurve = "unsafeCurve";
    public const string NoTargets = "noTargets";

    public const string PumpKept = "pumpKept";
    public const string TargetNotFound = "targetNotFound";
    public const string TargetAmbiguous = "targetAmbiguous";
    public const string UncontrolledChannel = "uncontrolledChannel";
    public const string PinnedChannel = "pinnedChannel";
    public const string UnsupportedTarget = "unsupportedTarget";
    public const string GpuFlat = "gpuFlat";
    public const string FollowedChannel = "followedChannel";
    public const string GuardHealed = "guardHealed";

    public const string NoteRecommendedMode = "recommendedMode";
    public const string NoteNoUserCurves = "noUserCurves";
    public const string NoteCoolingDisabled = "coolingDisabled";
    public const string NoteCoolingCustomized = "nexus3CoolingCustomized";
}

/// <summary>Safety numbers an imported curve must clear. Points are baked with Nexus 2's modifiers, then raised to the stop duty; the hot and flat checks scale that result by at most one of Nexus 3's global modifier.</summary>
internal static class Nexus2FanCurveLimits
{
    /// <summary>A live sensor reading outside this band is a bad sensor, not a temperature.</summary>
    public const double MinSensorC = 15;
    public const double MaxSensorC = 105;
    /// <summary>The widest temperature a stored point may carry.</summary>
    public const double MaxPointTempC = 110;
    public const double MaxResponseTimeSec = 60;
    /// <summary>A graph or linear curve must command at least <see cref="HotMinDuty"/> here.</summary>
    public const double HotCheckTempC = 80;
    public const double HotMinDuty = 60;
    public const double FlatMinDuty = 30;
    /// <summary>The lint stop threshold: below it a fan stops or barely turns.</summary>
    public const double StopDuty = 20;
}

/// <summary>Everything the planner reads from the live machine, so a plan is a pure function of it and the Nexus 2 file.</summary>
internal sealed class Nexus2FanCurveEnvironment
{
    public IReadOnlyList<FanChannel> Channels { get; init; } = Array.Empty<FanChannel>();
    public IReadOnlyList<TemperatureSource> Sources { get; init; } = Array.Empty<TemperatureSource>();
    public IReadOnlySet<string> Uncontrolled { get; init; } = new HashSet<string>();
    public IReadOnlyDictionary<string, bool> PinOverrides { get; init; } = new Dictionary<string, bool>();
    public IReadOnlyDictionary<string, string> FanNames { get; init; } = new Dictionary<string, string>();
    public string CpuModel { get; init; } = "";
    public string BoardModel { get; init; } = "";
    public IReadOnlyList<string> GpuModels { get; init; } = Array.Empty<string>();
    public IReadOnlyList<CurveDocument> CurrentCurves { get; init; } = Array.Empty<CurveDocument>();
    /// <summary>Nexus 3's global modifier still multiplies every imported curve at run time.</summary>
    public double NexusGlobalModifier { get; init; } = 1.0;
    public IReadOnlyDictionary<string, string> FanRoles { get; init; } = new Dictionary<string, string>();
    /// <summary>Channels with a manual duty, which counts as software-driven.</summary>
    public IReadOnlySet<string> ManualDriven { get; init; } = new HashSet<string>();
    /// <summary>Channels the thermal guard's heal moved onto its shared curve.</summary>
    public IReadOnlySet<string> HealedChannelIds { get; init; } = new HashSet<string>();
    public bool CoolingEnabled { get; init; } = true;
    /// <summary>Whether Nexus 3's cooling is still a fresh install for the given target preset; null counts as customized.</summary>
    public Func<string, bool>? IsCoolingUntouched { get; init; }
    /// <summary>A constant duty is only safe with the thermal guard running behind it.</summary>
    public bool ThermalGuardEnabled { get; init; } = true;
    /// <summary>Q-series and NP50 targets stay unsupported until a curve write is verified on that hardware.</summary>
    public bool AllowCoolerHubTargets { get; init; }
    /// <summary>Lint input for a candidate curve list under a scope; null skips the lint pass.</summary>
    public Func<IReadOnlyList<CurveDocument>, LintScope, LintInput>? BuildLint { get; init; }
}

internal sealed class Nexus2PlannedTarget
{
    public string Label { get; set; } = "";
    public bool Import { get; set; }
    public string? Reason { get; set; }
    public string? ChannelId { get; set; }
    public bool IsGpu { get; set; }
}

internal sealed class Nexus2PlannedCurve
{
    public string Nexus2Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public bool Import { get; set; }
    public string? Reason { get; set; }
    public bool MinimumRaised { get; set; }
    public List<Nexus2PlannedTarget> Targets { get; } = new();
    /// <summary>The Nexus 3 curve with only the importing targets as outputs; set while <see cref="Import"/> holds.</summary>
    public CurveDocument? Document { get; set; }
}

internal sealed class Nexus2FanCurvePlan
{
    public string? Note { get; set; }
    public int OtherProfiles { get; set; }
    /// <summary>The Nexus 2 easy-mode value ("silent" | "balanced" | "performance") when it maps to a Nexus 3 preset.</summary>
    public string? RecommendedFrom { get; set; }
    /// <summary>The Nexus 3 preset ("silent" | "balanced" | "turbo") for <see cref="RecommendedFrom"/>.</summary>
    public string? RecommendedPreset { get; set; }
    /// <summary>True when the preset would be activated: mappable and Nexus 3 cooling untouched.</summary>
    public bool RecommendedApplies { get; set; }
    public List<Nexus2PlannedCurve> Curves { get; } = new();
    public int ImportableCount => Curves.Count(c => c.Import);
}

/// <summary>Turns the active Nexus 2 profile's curves into Nexus 3 curves, or says why each stays behind; any doubt skips instead of guessing.</summary>
internal static class Nexus2FanCurvePlanner
{
    public const string ImportedIdPrefix = "n2-";

    private const int MaxNameLength = 64;
    private const int MaxIdLength = 48;

    private static readonly Regex HubFanId = new(@"^(?<hub>Q60|Q80|NP50) Channel(?<ch>\d{1,3})Fan(?<fan>\d{1,3})(?:/(?<order>\d{1,2}))?$", RegexOptions.CultureInvariant);
    private static readonly Regex PwmPortId = new(@"^PWM (?<n>\d)$", RegexOptions.CultureInvariant);

    public static Nexus2FanCurvePlan Build(JsonElement root, Nexus2FanCurveEnvironment env)
    {
        var plan = new Nexus2FanCurvePlan();
        if (!env.CoolingEnabled)
        {
            plan.Note = Nexus2FanCurveReasons.NoteCoolingDisabled;
            return plan;
        }
        var profiles = Nexus2Json.GetArray(root, "profiles");
        if (profiles is not { } arr)
        {
            return plan;
        }

        JsonElement? active = null;
        var activeCount = 0;
        foreach (var p in arr.EnumerateArray())
        {
            if (Nexus2Json.GetBool(p, "active", false))
            {
                active = p;
                activeCount++;
            }
        }
        if (activeCount != 1 || active is not { } profile)
        {
            return plan;
        }

        foreach (var p in arr.EnumerateArray())
        {
            if (!Nexus2Json.GetBool(p, "active", false) && CoolingBlock(p) is { } other && CurvesWithOutputs(other) > 0)
            {
                plan.OtherProfiles++;
            }
        }

        if (CoolingBlock(profile) is not { } cooling)
        {
            plan.Note = Nexus2FanCurveReasons.NoteNoUserCurves;
            return plan;
        }

        // Nexus 2 builds its easy-mode curves from the first CPU and first GPU temperature and runs the stored curves when one is missing.
        // A missing CPU source usually means sensors are not ready yet, so only a present CPU with no GPU counts. An iGPU (GpuIntel) counts as a GPU here; parity with Nexus 2 on that is unverified.
        var easyRuns = !(env.Sources.Any(t => t.Category == "CPU") && !env.Sources.Any(t => t.Category == "GPU"));
        if (easyRuns && Nexus2Json.GetObject(cooling, "easyMode") is { } easy && Nexus2Json.GetBool(easy, "enabled", false))
        {
            // Nexus 2's easy-mode global modifier has no Nexus 3 preset equivalent and is not carried.
            plan.Note = Nexus2FanCurveReasons.NoteRecommendedMode;
            var selected = Nexus2Json.GetString(easy, "selected");
            if (Nexus2RecommendedPreset.Map(selected) is { } preset)
            {
                plan.RecommendedFrom = selected;
                plan.RecommendedPreset = preset;
                if (env.IsCoolingUntouched?.Invoke(preset) == true)
                {
                    plan.RecommendedApplies = true;
                }
                else
                {
                    plan.Note = Nexus2FanCurveReasons.NoteCoolingCustomized;
                }
            }
            return plan;
        }

        var curves = Nexus2Json.GetArray(cooling, "curves");
        if (curves is not { } curveArr)
        {
            plan.Note = Nexus2FanCurveReasons.NoteNoUserCurves;
            return plan;
        }

        var global = ReadGlobalModifier(cooling);
        var usedIds = env.CurrentCurves
            .Where(c => c is not null && c.Id.StartsWith(ImportedIdPrefix, StringComparison.Ordinal) && c.Id.EndsWith("-kept", StringComparison.Ordinal))
            .Select(c => c.Id)
            .ToHashSet(StringComparer.Ordinal);
        var ctx = BuildContext(root, env);
        var index = 0;
        foreach (var raw in curveArr.EnumerateArray())
        {
            index++;
            if (raw.ValueKind != JsonValueKind.Object || Nexus2Json.GetArray(raw, "outputs") is not { } outputs || outputs.GetArrayLength() == 0)
            {
                continue;
            }
            var kind = (Nexus2Json.GetString(raw, "type") ?? "").ToLowerInvariant();
            if (kind is not ("graph" or "linear" or "flat" or "mixed"))
            {
                continue;
            }
            plan.Curves.Add(PlanCurve(raw, kind, outputs, index, global, ctx, usedIds));
        }

        ResolveSharedChannels(plan);
        EnforceSharedModeSiblings(plan, env);
        RunLint(plan, env);
        EnforceSharedModeSiblings(plan, env);

        if (plan.Curves.Count == 0)
        {
            plan.Note = Nexus2FanCurveReasons.NoteNoUserCurves;
        }
        return plan;
    }

    private sealed class Context
    {
        public required Nexus2FanCurveEnvironment Env { get; init; }
        public int? MiniFanPort1 { get; init; }
        public int? MiniFanPort2 { get; init; }
        public required HashSet<string> Followed { get; init; }
        public required HashSet<string> GuardOwned { get; init; }
    }

    private static Context BuildContext(JsonElement root, Nexus2FanCurveEnvironment env)
    {
        int? p1 = null, p2 = null;
        var settings = Nexus2Json.GetObject(root, "settings");
        var mini = settings is { } st ? Nexus2Json.GetObject(st, "minihub") : null;
        var layout = mini is { } m ? Nexus2Json.GetObject(m, "layout") : null;
        if (layout is { } l)
        {
            p1 = Nexus2Json.GetDouble(l, "fanPort1") is { } a && a == Math.Floor(a) ? (int)a : null;
            p2 = Nexus2Json.GetDouble(l, "fanPort2") is { } b && b == Math.Floor(b) ? (int)b : null;
        }
        var guardOwned = new HashSet<string>(env.HealedChannelIds, StringComparer.Ordinal);
        var followed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var curve in env.CurrentCurves)
        {
            if (curve is null)
            {
                continue;
            }
            if (curve.Id == CoolingConfigLint.GuardCurveId)
            {
                foreach (var o in curve.Outputs)
                {
                    guardOwned.Add(o.Id);
                }
            }
            if (curve.Sync is { } sync && !string.IsNullOrEmpty(sync.SourceChannelId))
            {
                followed.Add(sync.SourceChannelId);
            }
        }
        return new Context { Env = env, MiniFanPort1 = p1, MiniFanPort2 = p2, Followed = followed, GuardOwned = guardOwned };
    }

    private static JsonElement? CoolingBlock(JsonElement profile)
    {
        var widgets = Nexus2Json.GetObject(profile, "widgets");
        return widgets is { } w ? Nexus2Json.GetObject(w, "cooling") : null;
    }

    private static int CurvesWithOutputs(JsonElement cooling)
    {
        var count = 0;
        if (Nexus2Json.GetArray(cooling, "curves") is { } arr)
        {
            foreach (var c in arr.EnumerateArray())
            {
                if (c.ValueKind == JsonValueKind.Object && Nexus2Json.GetArray(c, "outputs") is { } outs && outs.GetArrayLength() > 0)
                {
                    count++;
                }
            }
        }
        return count;
    }

    /// <summary>The global modifier as a fraction, or null when Nexus 2 would have refused the whole curve set.</summary>
    private static double? ReadGlobalModifier(JsonElement cooling)
    {
        if (!cooling.TryGetProperty("globalSpeedModifier", out var el))
        {
            return 1.0;
        }
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetDouble(out var pct) || !double.IsFinite(pct) || pct < 0 || pct > 100)
        {
            return null;
        }
        return pct / 100.0;
    }

    private static Nexus2PlannedCurve PlanCurve(
        JsonElement raw, string kind, JsonElement outputs, int index, double? global, Context ctx, HashSet<string> usedIds)
    {
        var env = ctx.Env;
        var name = Nexus2Json.GetString(raw, "name");
        var nexus2Id = Nexus2Json.GetString(raw, "id") ?? "";
        var curve = new Nexus2PlannedCurve
        {
            Nexus2Id = nexus2Id,
            Name = !string.IsNullOrWhiteSpace(name) ? Truncate(name.Trim(), MaxNameLength)
                : !string.IsNullOrWhiteSpace(nexus2Id) ? Truncate(nexus2Id.Trim(), MaxNameLength)
                : "#" + index.ToString(CultureInfo.InvariantCulture),
            Kind = kind,
        };

        foreach (var output in outputs.EnumerateArray())
        {
            curve.Targets.Add(ResolveTarget(output, kind == "flat", ctx));
        }

        var skip = EvaluateCurve(raw, kind, global, env, out var points, out var flatSpeed, out var sensor, out var raised);
        curve.MinimumRaised = raised;
        if (skip is not null)
        {
            curve.Reason = skip;
            MarkTargetsSkipped(curve);
            return curve;
        }

        var importable = curve.Targets.Where(t => t.Import).ToList();
        if (importable.Count == 0)
        {
            curve.Reason = Nexus2FanCurveReasons.NoTargets;
            return curve;
        }

        var id = UniqueId(curve.Nexus2Id, index, usedIds);
        var doc = new CurveDocument
        {
            Id = id,
            Name = curve.Name,
            Outputs = importable.Select(t => new CurveOutputDocument { Id = t.ChannelId!, Type = "Fan" }).ToList(),
        };
        if (kind == "flat")
        {
            doc.Type = "Flat";
            doc.Flat = new FlatCurveData { Speed = (int)flatSpeed };
        }
        else
        {
            doc.Type = "Graph";
            doc.Input = new CurveInputDocument { Id = sensor!.Id, Type = "Temperature", Device = sensor.Category };
            doc.Graph = new GraphCurveData
            {
                SpeedModifier = 1.0,
                Points = points.Select(p => new Persistence.GraphPoint { Temp = p.Temp, Speed = p.Speed }).ToList(),
            };
        }

        // Same clamp as a curve posted through the cooling route.
        var sanitized = CoolingSafety.Sanitize(new SetCurvesBody { GlobalSpeedModifier = 1.0, Curves = { CurveWireMapper.ToWire(doc) } });
        curve.Document = CurveWireMapper.ToDocument(sanitized.Curves[0]);
        curve.Import = true;
        return curve;
    }

    /// <summary>The skip reason, or null when the curve itself is fine. Outputs are judged separately.</summary>
    private static string? EvaluateCurve(
        JsonElement raw, string kind, double? global, Nexus2FanCurveEnvironment env,
        out List<(double Temp, double Speed)> points, out double flatSpeed, out TemperatureSource? sensor, out bool raised)
    {
        points = new List<(double, double)>();
        flatSpeed = 0;
        sensor = null;
        raised = false;

        if (kind == "mixed")
        {
            return Nexus2FanCurveReasons.MixedCurve;
        }
        if (global is not { } g)
        {
            return Nexus2FanCurveReasons.BadPoints;
        }
        var nexusScale = Math.Min(1.0, Math.Max(0.0, env.NexusGlobalModifier));

        if (kind == "flat")
        {
            if (!TryReadFlat(raw, out var speed))
            {
                return Nexus2FanCurveReasons.BadPoints;
            }
            if (!env.ThermalGuardEnabled)
            {
                return Nexus2FanCurveReasons.UnsafeCurve;
            }
            // The stored duty is a whole percent, so the floor is judged on that.
            flatSpeed = Math.Clamp(Math.Round(speed * g, MidpointRounding.AwayFromZero), 0, 100);
            if (!(flatSpeed * nexusScale >= Nexus2FanCurveLimits.FlatMinDuty))
            {
                return Nexus2FanCurveReasons.UnsafeCurve;
            }
            return null;
        }

        double modifier;
        if (kind == "linear")
        {
            if (!TryReadLinear(raw, out var linearPoints))
            {
                return Nexus2FanCurveReasons.BadPoints;
            }
            points = linearPoints;
            modifier = 1.0;
        }
        else
        {
            if (!TryReadGraph(raw, out var graphPoints, out modifier))
            {
                return Nexus2FanCurveReasons.BadPoints;
            }
            points = graphPoints;
        }

        var sensorReason = ResolveSensor(raw, env, out sensor);
        if (sensorReason is not null)
        {
            return sensorReason;
        }

        for (var i = 0; i < points.Count; i++)
        {
            var speed = Math.Clamp(Round(points[i].Speed * modifier * g), 0, 100);
            if (!(speed >= Nexus2FanCurveLimits.StopDuty))
            {
                speed = Nexus2FanCurveLimits.StopDuty;
                raised = true;
            }
            points[i] = (points[i].Temp, speed);
        }

        if (!(EvaluateAt(points, Nexus2FanCurveLimits.HotCheckTempC) * nexusScale >= Nexus2FanCurveLimits.HotMinDuty))
        {
            return Nexus2FanCurveReasons.UnsafeCurve;
        }
        return null;
    }

    private static bool TryReadFlat(JsonElement raw, out double speed)
    {
        speed = 0;
        var flat = Nexus2Json.GetObject(raw, "flat");
        var value = flat is { } f ? Nexus2Json.GetDouble(f, "speed") : null;
        // Nexus 2 stores this as an integer, so anything else never ran.
        if (value is not { } v || !double.IsFinite(v) || v < 0 || v > 100 || v != Math.Floor(v))
        {
            return false;
        }
        speed = v;
        return true;
    }

    private static bool TryReadLinear(JsonElement raw, out List<(double Temp, double Speed)> points)
    {
        points = new List<(double, double)>();
        var linear = Nexus2Json.GetObject(raw, "linear");
        if (linear is not { } l)
        {
            return false;
        }
        if (!ResponseTimeAccepted(l))
        {
            return false;
        }
        var minTemp = Nexus2Json.GetDouble(l, "minTemp");
        var maxTemp = Nexus2Json.GetDouble(l, "maxTemp");
        var minSpeed = Nexus2Json.GetDouble(l, "minSpeed");
        var maxSpeed = Nexus2Json.GetDouble(l, "maxSpeed");
        if (minTemp is null || maxTemp is null || minSpeed is null || maxSpeed is null)
        {
            return false;
        }
        var raw4 = new[] { (minTemp.Value, minSpeed.Value), (maxTemp.Value, maxSpeed.Value) };
        if (raw4.Any(p => !ValidPoint(p.Item1, p.Item2)) || minTemp.Value >= maxTemp.Value)
        {
            return false;
        }
        points = raw4.Select(p => (p.Item1, p.Item2)).ToList();
        return true;
    }

    private static bool TryReadGraph(JsonElement raw, out List<(double Temp, double Speed)> points, out double modifier)
    {
        points = new List<(double, double)>();
        modifier = 1.0;
        var graph = Nexus2Json.GetObject(raw, "graph");
        if (graph is not { } g)
        {
            return false;
        }
        if (!ResponseTimeAccepted(g))
        {
            return false;
        }
        // Nexus 2 drops a graph whose modifier is missing or outside the percent range.
        var pct = Nexus2Json.GetDouble(g, "speedModifier");
        if (pct is not { } sm || !double.IsFinite(sm) || sm < 0 || sm > 100)
        {
            return false;
        }
        modifier = sm / 100.0;

        // The stray top-level points key is junk; only graph.points ran.
        if (Nexus2Json.GetArray(g, "points") is not { } arr || arr.GetArrayLength() < 2)
        {
            return false;
        }
        var parsed = new List<(double Temp, double Speed)>();
        foreach (var p in arr.EnumerateArray())
        {
            var temp = Nexus2Json.GetDouble(p, "temp");
            var speed = Nexus2Json.GetDouble(p, "speed");
            if (temp is null || speed is null || !ValidPoint(temp.Value, speed.Value))
            {
                return false;
            }
            parsed.Add((temp.Value, speed.Value));
        }

        // Equal temperatures keep the higher speed so a duplicate can only run faster.
        foreach (var group in parsed.GroupBy(p => p.Temp).OrderBy(gp => gp.Key))
        {
            points.Add((group.Key, group.Max(p => p.Speed)));
        }
        return points.Count >= 2;
    }

    /// <summary>Nexus 2 throws on a response time outside its range, so that curve never registered; an absent one reads as the default.</summary>
    private static bool ResponseTimeAccepted(JsonElement block)
    {
        if (!block.TryGetProperty("responseTime", out var el))
        {
            return true;
        }
        return el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var rt) && double.IsFinite(rt) && rt >= 0 && rt <= Nexus2FanCurveLimits.MaxResponseTimeSec;
    }

    private static bool ValidPoint(double temp, double speed) =>
        double.IsFinite(temp) && double.IsFinite(speed)
        && temp >= 0 && temp <= Nexus2FanCurveLimits.MaxPointTempC
        && speed >= 0 && speed <= 100;

    private static double EvaluateAt(List<(double Temp, double Speed)> points, double temp)
    {
        if (temp <= points[0].Temp)
        {
            return points[0].Speed;
        }
        if (temp >= points[^1].Temp)
        {
            return points[^1].Speed;
        }
        for (var i = 0; i < points.Count - 1; i++)
        {
            if (temp >= points[i].Temp && temp <= points[i + 1].Temp)
            {
                var ratio = (temp - points[i].Temp) / (points[i + 1].Temp - points[i].Temp);
                return points[i].Speed + ratio * (points[i + 1].Speed - points[i].Speed);
            }
        }
        return points[^1].Speed;
    }

    // Scale then round, as Math.Round(value, digits, mode) did before .NET 11. Its exact rounding
    // moves some imported curve points by one step, so this keeps the import output unchanged.
    private static double Round(double value) => Math.Round(value * 100, MidpointRounding.AwayFromZero) / 100;

    // ── Sensor ──

    private static string? ResolveSensor(JsonElement raw, Nexus2FanCurveEnvironment env, out TemperatureSource? sensor)
    {
        sensor = null;
        var input = Nexus2Json.GetObject(raw, "input");
        if (input is not { } inp)
        {
            return Nexus2FanCurveReasons.SensorNotFound;
        }
        var type = Nexus2Json.GetString(inp, "type");
        if (!string.Equals(type, "Temperature", StringComparison.OrdinalIgnoreCase))
        {
            return Nexus2FanCurveReasons.NonTemperatureInput;
        }
        var id = Nexus2Json.GetString(inp, "id");
        var device = Nexus2Json.GetString(inp, "device") ?? "";
        if (string.IsNullOrWhiteSpace(id))
        {
            return Nexus2FanCurveReasons.SensorNotFound;
        }

        TemperatureSource? found;
        if (id.Equals("pumpTempIn", StringComparison.OrdinalIgnoreCase) || id.Equals("pumpTempOut", StringComparison.OrdinalIgnoreCase))
        {
            var reason = ResolveCoolant(id, device, env, out found);
            if (reason is not null)
            {
                return reason;
            }
        }
        else if (id.Equals("portalTemp", StringComparison.OrdinalIgnoreCase))
        {
            // Nexus 3's cable probe reads the same bytes through a different thermistor table.
            return Nexus2FanCurveReasons.SensorNotFound;
        }
        else
        {
            var reason = ResolveLhm(id, device, env, out found);
            if (reason is not null)
            {
                return reason;
            }
        }

        if (found is null || !(found.Value >= Nexus2FanCurveLimits.MinSensorC && found.Value <= Nexus2FanCurveLimits.MaxSensorC))
        {
            return Nexus2FanCurveReasons.SensorImplausible;
        }
        sensor = found;
        return null;
    }

    private static string? ResolveCoolant(string id, string device, Nexus2FanCurveEnvironment env, out TemperatureSource? found)
    {
        found = null;
        var model = device.Contains("Q80", StringComparison.OrdinalIgnoreCase) ? "HYTE Q80"
            : device.Contains("Q60", StringComparison.OrdinalIgnoreCase) ? "HYTE Q60"
            : null;
        if (model is null)
        {
            return Nexus2FanCurveReasons.SensorNotFound;
        }
        var suffix = id.Equals("pumpTempIn", StringComparison.OrdinalIgnoreCase) ? ":coolant-in" : ":coolant-out";
        var matches = env.Sources
            .Where(s => s.Id.StartsWith("qseries:", StringComparison.Ordinal)
                && s.Id.EndsWith(suffix, StringComparison.Ordinal)
                && string.Equals(s.DeviceName, model, StringComparison.Ordinal))
            .ToList();
        if (matches.Count == 0)
        {
            return Nexus2FanCurveReasons.SensorNotFound;
        }
        if (matches.Count > 1)
        {
            return Nexus2FanCurveReasons.SensorAmbiguous;
        }
        found = matches[0];
        return null;
    }

    private static string? ResolveLhm(string id, string device, Nexus2FanCurveEnvironment env, out TemperatureSource? found)
    {
        found = null;
        var candidates = env.Sources.Select(s => new LhmIdentifierMatcher.Candidate(s.Id, s.Name)).ToList();
        var (matchId, tier) = LhmIdentifierMatcher.Match(id, candidates);
        if (matchId is null)
        {
            var normalized = LhmIdentifierMatcher.Normalize(id);
            var near = candidates.Count(c => string.Equals(LhmIdentifierMatcher.Normalize(c.Id), normalized, StringComparison.OrdinalIgnoreCase));
            return near > 1 ? Nexus2FanCurveReasons.SensorAmbiguous : Nexus2FanCurveReasons.SensorNotFound;
        }

        var source = env.Sources.First(s => string.Equals(s.Id, matchId, StringComparison.OrdinalIgnoreCase));
        var known = HardwareNameOf(source, env);
        var named = !string.IsNullOrWhiteSpace(device);
        var agrees = named && known is not null && string.Equals(known.Trim(), device.Trim(), StringComparison.OrdinalIgnoreCase);
        if (tier == IdentifierMatchTier.Exact)
        {
            // An identifier outlives a hardware swap; a stated, known and different name is that swap.
            if (named && known is not null && !agrees)
            {
                return Nexus2FanCurveReasons.SensorNotFound;
            }
        }
        else if (!agrees)
        {
            return Nexus2FanCurveReasons.SensorNotFound;
        }
        found = source;
        return null;
    }

    /// <summary>The hardware name Nexus 3 knows for a sensor's chip, or null when it cannot tell.</summary>
    private static string? HardwareNameOf(TemperatureSource source, Nexus2FanCurveEnvironment env)
    {
        switch (source.Category)
        {
            case "CPU":
                return string.IsNullOrWhiteSpace(env.CpuModel) ? null : env.CpuModel;
            case "Motherboard":
                return string.IsNullOrWhiteSpace(env.BoardModel) ? null : env.BoardModel;
            case "GPU":
                {
                    var segments = source.Id.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    var hardwareId = segments.Length >= 2 ? "/" + segments[0] + "/" + segments[1] : "";
                    var viaFans = env.Channels.FirstOrDefault(c => c.IsGpu && string.Equals(c.DeviceId, hardwareId, StringComparison.OrdinalIgnoreCase));
                    if (viaFans is not null)
                    {
                        return viaFans.OriginalDeviceName ?? viaFans.DeviceName;
                    }
                    // Without a fan to name the card, only a lone GPU is unambiguous.
                    var gpuHardware = env.Sources.Where(s => s.Category == "GPU").Select(s => string.Join('/', s.Id.Split('/', StringSplitOptions.RemoveEmptyEntries).Take(2))).Distinct().Count();
                    return env.GpuModels.Count == 1 && gpuHardware == 1 ? env.GpuModels[0] : null;
                }
            default:
                return null;
        }
    }

    // ── Targets ──

    private static Nexus2PlannedTarget ResolveTarget(JsonElement output, bool curveIsFlat, Context ctx)
    {
        var env = ctx.Env;
        var id = Nexus2Json.GetString(output, "id") ?? "";
        var deviceType = Nexus2Json.GetString(output, "deviceType") ?? "";
        var parent = Nexus2Json.GetString(output, "parentDeviceId") ?? "";
        var target = new Nexus2PlannedTarget { Label = id };

        if (deviceType.Contains("pump", StringComparison.OrdinalIgnoreCase) || id.Contains("pump", StringComparison.OrdinalIgnoreCase))
        {
            target.Reason = Nexus2FanCurveReasons.PumpKept;
            return target;
        }

        var lookup = FindChannel(id, deviceType, parent, ctx);
        if (lookup.Reason is not null)
        {
            target.Reason = lookup.Reason;
            return target;
        }

        var channel = lookup.Channel!;
        target.Label = env.FanNames.TryGetValue(channel.Id, out var custom) && !string.IsNullOrWhiteSpace(custom) ? custom : channel.Name;
        target.ChannelId = channel.Id;
        target.IsGpu = channel.IsGpu;
        if (channel.Kind == FanKinds.Pump)
        {
            target.Reason = Nexus2FanCurveReasons.PumpKept;
        }
        else if (channel.IsAio)
        {
            // The cooler ships its own curve and owns the loop; presets treat its channels as pinned for the same reason.
            target.Reason = Nexus2FanCurveReasons.UnsupportedTarget;
        }
        else if (env.Uncontrolled.Contains(channel.Id))
        {
            target.Reason = Nexus2FanCurveReasons.UncontrolledChannel;
        }
        else if (env.PinOverrides.TryGetValue(channel.Id, out var pinned) && pinned)
        {
            // Only the user's own pin counts: GPU fans are pinned by default yet are valid targets here.
            target.Reason = Nexus2FanCurveReasons.PinnedChannel;
        }
        else if (channel.ReadOnly)
        {
            target.Reason = Nexus2FanCurveReasons.UnsupportedTarget;
        }
        else if (ctx.Followed.Contains(channel.Id))
        {
            // A Sync curve mirrors this channel's duty, so rebinding it would change that curve's behavior.
            target.Reason = Nexus2FanCurveReasons.FollowedChannel;
        }
        else if (ctx.GuardOwned.Contains(channel.Id))
        {
            target.Reason = Nexus2FanCurveReasons.GuardHealed;
        }
        else if (curveIsFlat && !GuardCovers(channel, env))
        {
            // A constant duty needs the thermal guard behind it.
            target.Reason = Nexus2FanCurveReasons.GpuFlat;
        }
        else
        {
            target.Import = true;
        }
        return target;
    }

    /// <summary>The CPU guard can write a channel that is not a GPU fan, is controlled and counts as CPU cooling.</summary>
    private static bool GuardCovers(FanChannel channel, Nexus2FanCurveEnvironment env) =>
        !channel.IsGpu && !env.Uncontrolled.Contains(channel.Id)
        && channel.Kind != FanKinds.Pump && CoolingConfigLint.IsCpuCooling(channel, env.FanRoles);

    private readonly record struct ChannelLookup(FanChannel? Channel, string? Reason);

    private static ChannelLookup Found(FanChannel channel) => new(channel, null);

    private static ChannelLookup Fail(string reason) => new(null, reason);

    private static ChannelLookup Unique(IReadOnlyList<FanChannel> matches) => matches.Count switch
    {
        0 => Fail(Nexus2FanCurveReasons.TargetNotFound),
        1 => Found(matches[0]),
        _ => Fail(Nexus2FanCurveReasons.TargetAmbiguous),
    };

    private static ChannelLookup ByExactId(string expectedId, Nexus2FanCurveEnvironment env) =>
        Unique(env.Channels.Where(c => string.Equals(c.Id, expectedId, StringComparison.OrdinalIgnoreCase)).ToList());

    private static ChannelLookup FindChannel(string id, string deviceType, string parent, Context ctx)
    {
        var env = ctx.Env;
        if (string.IsNullOrWhiteSpace(id))
        {
            return Fail(Nexus2FanCurveReasons.UnsupportedTarget);
        }

        switch (deviceType.ToLowerInvariant())
        {
            case "fanport":
                {
                    // PWM n is the hub's zero-based channel n minus one.
                    var m = PwmPortId.Match(id);
                    if (!m.Success || string.IsNullOrEmpty(parent) || !int.TryParse(m.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1 || n > 4)
                    {
                        return Fail(Nexus2FanCurveReasons.UnsupportedTarget);
                    }
                    return ByExactId(SmartHubCoolingProvider.FanId(parent, n - 1), env);
                }
            case "ibpfan":
                {
                    // Nexus 2 names a Mini Hub fan <serial><usb port>: ports 0 and 1 are the fan ports, the rest LED strips.
                    if (string.IsNullOrEmpty(parent) || !id.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || id.Length != parent.Length + 1)
                    {
                        return Fail(Nexus2FanCurveReasons.UnsupportedTarget);
                    }
                    // Nexus 2 builds the rear fan as usb port 0 and the front fans as usb port 1 (IBPMiniHubController.InitDevice); the layout only gates whether each exists. Nexus 3 writes rear then front in the same byte order.
                    return id[^1] switch
                    {
                        '0' when ctx.MiniFanPort1 == 1 => ByExactId($"minihub:{parent}:port1", env),
                        '1' when ctx.MiniFanPort2 is >= 1 and <= 3 => ByExactId($"minihub:{parent}:port2", env),
                        _ => Fail(Nexus2FanCurveReasons.UnsupportedTarget),
                    };
                }
            case "gpufan":
                {
                    if (!parent.EndsWith("-ID", StringComparison.Ordinal))
                    {
                        return Fail(Nexus2FanCurveReasons.UnsupportedTarget);
                    }
                    var hardware = parent[..^3];
                    return Unique(env.Channels.Where(c => c.IsGpu
                        && string.Equals(c.OriginalDeviceName ?? c.DeviceName, hardware, StringComparison.OrdinalIgnoreCase)
                        && NameMatches(c.OriginalName ?? c.Name, id, "GPU ")).ToList());
                }
            case "chassis":
                {
                    if (string.IsNullOrWhiteSpace(env.BoardModel) || !string.Equals(env.BoardModel.Trim(), parent.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        return Fail(Nexus2FanCurveReasons.TargetNotFound);
                    }
                    return Unique(env.Channels.Where(c => !c.IsGpu
                        && c.Id.StartsWith('/')
                        && NameMatches(c.OriginalName ?? c.Name, id, null)).ToList());
                }
            case "legacy":
                return FindNp50Legacy(id, parent, env);
            case "fan":
            case "duo":
            case "trio":
                return FindHubFan(id, deviceType.ToLowerInvariant(), parent, env);
            default:
                return Fail(Nexus2FanCurveReasons.UnsupportedTarget);
        }
    }

    private static bool NameMatches(string channelName, string nexus2Name, string? prefix) =>
        string.Equals(channelName, nexus2Name, StringComparison.OrdinalIgnoreCase)
        || (prefix is not null && string.Equals(channelName, prefix + nexus2Name, StringComparison.OrdinalIgnoreCase));

    private static List<string> DeviceSerials(Nexus2FanCurveEnvironment env, string idPrefix, Func<FanChannel, bool>? filter = null) =>
        env.Channels
            .Where(c => c.Id.StartsWith(idPrefix, StringComparison.Ordinal) && (filter is null || filter(c)))
            .Select(c => c.Id.Split(':').ElementAtOrDefault(1) ?? "")
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static ChannelLookup FindNp50Legacy(string id, string parent, Nexus2FanCurveEnvironment env)
    {
        // One write latches the whole hub into software mode and re-sends stale duties for the other channels; unverified on hardware.
        if (!env.AllowCoolerHubTargets)
        {
            return Fail(Nexus2FanCurveReasons.UnsupportedTarget);
        }
        if (!string.Equals(id, "NP50Fan", StringComparison.Ordinal))
        {
            return Fail(Nexus2FanCurveReasons.UnsupportedTarget);
        }
        var serials = DeviceSerials(env, "np50:");
        if (serials.Count == 0)
        {
            return Fail(Nexus2FanCurveReasons.TargetNotFound);
        }
        if (serials.Count > 1)
        {
            return Fail(Nexus2FanCurveReasons.UnsupportedTarget);
        }
        if (!string.IsNullOrEmpty(parent) && !string.Equals(parent, serials[0], StringComparison.OrdinalIgnoreCase))
        {
            return Fail(Nexus2FanCurveReasons.TargetNotFound);
        }
        return ByExactId($"np50:{serials[0]}:legacy", env);
    }

    private static ChannelLookup FindHubFan(string id, string deviceType, string parent, Nexus2FanCurveEnvironment env)
    {
        if (!env.AllowCoolerHubTargets)
        {
            return Fail(Nexus2FanCurveReasons.UnsupportedTarget);
        }
        var m = HubFanId.Match(id);
        if (!m.Success)
        {
            return Fail(Nexus2FanCurveReasons.UnsupportedTarget);
        }
        var hub = m.Groups["hub"].Value;
        var channel = int.Parse(m.Groups["ch"].Value, CultureInfo.InvariantCulture);
        var fan = int.Parse(m.Groups["fan"].Value, CultureInfo.InvariantCulture);
        var hasOrder = m.Groups["order"].Success;
        if (hasOrder != (deviceType is "duo" or "trio"))
        {
            return Fail(Nexus2FanCurveReasons.UnsupportedTarget);
        }

        List<string> serials;
        string expected;
        if (hub == "NP50")
        {
            // Nexus 3 lists only single FP12 modules on the portal.
            if (hasOrder)
            {
                return Fail(Nexus2FanCurveReasons.UnsupportedTarget);
            }
            serials = DeviceSerials(env, "np50:");
            expected = serials.Count == 1 ? $"np50:{serials[0]}:port{channel}:dev{fan}" : "";
        }
        else
        {
            var product = hub == "Q80" ? "HYTE Q80" : "HYTE Q60";
            serials = DeviceSerials(env, "qseries:", c => string.Equals(c.DeviceName, product, StringComparison.Ordinal));
            if (channel is < 1 or > 2)
            {
                return Fail(Nexus2FanCurveReasons.UnsupportedTarget);
            }
            // Nexus 3 numbers Duo and Trio blades from one, Nexus 2 from zero.
            var order = hasOrder ? int.Parse(m.Groups["order"].Value, CultureInfo.InvariantCulture) + 1 : 0;
            expected = serials.Count == 1
                ? (hasOrder ? $"qseries:{serials[0]}:p{channel}:{fan}:{order}" : $"qseries:{serials[0]}:p{channel}:{fan}")
                : "";
        }

        if (serials.Count == 0)
        {
            return Fail(Nexus2FanCurveReasons.TargetNotFound);
        }
        // Cabling names cannot be tied to a physical module without exactly one such device.
        if (serials.Count > 1)
        {
            return Fail(Nexus2FanCurveReasons.UnsupportedTarget);
        }
        if (!string.IsNullOrEmpty(parent) && !string.Equals(parent, serials[0], StringComparison.OrdinalIgnoreCase))
        {
            return Fail(Nexus2FanCurveReasons.TargetNotFound);
        }
        return ByExactId(expected, env);
    }

    private static void MarkTargetsSkipped(Nexus2PlannedCurve curve)
    {
        foreach (var t in curve.Targets)
        {
            t.Import = false;
        }
    }

    // ── Cross-curve checks ──

    /// <summary>Nexus 2 gives an output one curve, so a channel two curves claim imports for neither.</summary>
    private static void ResolveSharedChannels(Nexus2FanCurvePlan plan)
    {
        var claims = plan.Curves
            .Where(c => c.Import)
            .SelectMany(c => c.Targets.Where(t => t.Import).Select(t => t.ChannelId!))
            .GroupBy(x => x, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (claims.Count == 0)
        {
            return;
        }
        foreach (var curve in plan.Curves.Where(c => c.Import))
        {
            foreach (var t in curve.Targets.Where(t => t.Import && claims.Contains(t.ChannelId!)))
            {
                t.Import = false;
                t.Reason = Nexus2FanCurveReasons.TargetAmbiguous;
            }
            FinishCurve(curve);
        }
    }

    /// <summary>Re-derives a curve's document and status from its targets after one of them was dropped.</summary>
    private static void FinishCurve(Nexus2PlannedCurve curve)
    {
        if (!curve.Import || curve.Document is null)
        {
            return;
        }
        var keep = curve.Targets.Where(t => t.Import).Select(t => t.ChannelId!).ToHashSet(StringComparer.Ordinal);
        curve.Document.Outputs.RemoveAll(o => !keep.Contains(o.Id));
        if (curve.Document.Outputs.Count == 0)
        {
            curve.Import = false;
            curve.Document = null;
            curve.Reason = Nexus2FanCurveReasons.NoTargets;
        }
    }

    /// <summary>A Mini Hub write sets the whole hub to software and re-sends the other port's duty, so a port imports only when its siblings are imported or already software-driven.</summary>
    private static void EnforceSharedModeSiblings(Nexus2FanCurvePlan plan, Nexus2FanCurveEnvironment env)
    {
        var imported = plan.Curves.Where(c => c.Import)
            .SelectMany(c => c.Targets.Where(t => t.Import).Select(t => t.ChannelId!))
            .ToHashSet(StringComparer.Ordinal);
        var driven = env.CurrentCurves.Where(c => c is not null).SelectMany(c => c.Outputs.Select(o => o.Id)).ToHashSet(StringComparer.Ordinal);
        driven.UnionWith(env.ManualDriven);

        var devices = env.Channels
            .Where(c => c.Id.StartsWith("minihub:", StringComparison.Ordinal) && !string.IsNullOrEmpty(c.DeviceId))
            .GroupBy(c => c.DeviceId!, StringComparer.Ordinal);
        foreach (var device in devices)
        {
            var members = device.ToList();
            if (!members.Any(m => imported.Contains(m.Id)))
            {
                continue;
            }
            var controllable = members.Where(m => !m.ReadOnly);
            if (controllable.All(m => imported.Contains(m.Id) || driven.Contains(m.Id)))
            {
                continue;
            }
            var ids = members.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var curve in plan.Curves.Where(c => c.Import))
            {
                foreach (var t in curve.Targets.Where(t => t.Import && ids.Contains(t.ChannelId!)))
                {
                    t.Import = false;
                    t.Reason = Nexus2FanCurveReasons.UnsupportedTarget;
                }
                FinishCurve(curve);
            }
        }
    }

    /// <summary>Skips a curve whose composed config has a lint hazard the current config lacks, under the save-time scope; the runtime guard and the guardHealed skip cover the rest.</summary>
    private static void RunLint(Nexus2FanCurvePlan plan, Nexus2FanCurveEnvironment env)
    {
        if (env.BuildLint is null)
        {
            return;
        }
        var scopes = new[] { LintScope.Severe };
        var baseline = scopes.ToDictionary(sc => sc, sc => Hazards(env.BuildLint(env.CurrentCurves, sc)));
        var accepted = new List<CurveDocument>();
        foreach (var curve in plan.Curves.Where(c => c.Import))
        {
            var composed = Nexus2FanCurveApplier.Compose(env.CurrentCurves, accepted.Append(curve.Document!).ToList());
            if (scopes.Any(sc => Hazards(env.BuildLint(composed, sc)).Any(h => !baseline[sc].Contains(h))))
            {
                curve.Import = false;
                curve.Document = null;
                curve.Reason = Nexus2FanCurveReasons.UnsafeCurve;
                MarkTargetsSkipped(curve);
                continue;
            }
            accepted.Add(curve.Document!);
        }
    }

    private static HashSet<(string Channel, string Kind)> Hazards(LintInput input) =>
        CoolingConfigLint.Analyze(input).Select(h => (h.ChannelId, h.Kind)).ToHashSet();

    // ── Ids ──

    private static string UniqueId(string nexus2Id, int index, HashSet<string> used)
    {
        var sb = new StringBuilder();
        foreach (var ch in nexus2Id.ToLowerInvariant())
        {
            if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                sb.Append(ch);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }
        var body = sb.ToString().Trim('-');
        if (body.Length > MaxIdLength)
        {
            body = body[..MaxIdLength].TrimEnd('-');
        }
        if (body.Length == 0)
        {
            body = "curve" + index.ToString(CultureInfo.InvariantCulture);
        }
        var candidate = ImportedIdPrefix + body;
        var suffix = 2;
        while (!used.Add(candidate))
        {
            candidate = ImportedIdPrefix + body + "-" + suffix.ToString(CultureInfo.InvariantCulture);
            suffix++;
        }
        return candidate;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
