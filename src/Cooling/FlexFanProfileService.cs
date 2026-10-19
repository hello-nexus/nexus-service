using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.LianLiFlex;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;

namespace Nexus.Service.Cooling;

/// <summary>
/// Keeps each Flex fan's vendor-style profile as one managed cooling curve ("flex-{MAC}") that
/// drives the fan's channel, so the Cooling page and the Cooling tab show and edit the same thing.
/// The curve is evaluated live by the curve engine.
/// </summary>
public sealed class FlexFanProfileService
{
    public const string CurveIdPrefix = "flex-";

    private readonly IConfigStore _store;
    private readonly IFanControlProvider _fans;
    private readonly ICurveProvider _curves;
    private readonly FlexHub _hub;
    private readonly MultiplexHub? _mux;

    public FlexFanProfileService(IConfigStore store, IFanControlProvider fans, ICurveProvider curves, FlexHub hub, MultiplexHub? mux = null)
    {
        _store = store;
        _fans = fans;
        _curves = curves;
        _hub = hub;
        _mux = mux;
    }

    public static string CurveIdOf(string mac) => $"{CurveIdPrefix}{mac}";

    public static bool IsKnownProfile(string key) => key == "" || key == "mbsync" || FlexFanProfiles.IsKnown(key);

    /// <summary>Which temperature sources exist: CPU, GPU, and a coolant sensor for "water".</summary>
    public (bool Cpu, bool Gpu, bool Water) SourcesAvailable()
    {
        var sources = _fans.GetTemperatureSources();
        return (sources.Any(s => s.Category == "CPU"), sources.Any(s => s.Category == "GPU"), sources.Any(s => s.Category == "Cooler"));
    }

    private TemperatureSource? ResolveSource(string source)
    {
        var category = source switch { "gpu" => "GPU", "water" => "Cooler", _ => "CPU" };
        var sources = _fans.GetTemperatureSources();
        return sources.FirstOrDefault(t => t.Category == category && t.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
            ?? sources.FirstOrDefault(t => t.Category == category);
    }

    /// <summary>The points a profile plays, in RPM terms: shipped, the user's custom slot, or empty for fixed and mbsync.</summary>
    public static IReadOnlyList<(int Temp, int Rpm)> EffectivePoints(LianLiFlexFanProfileSettings p)
    {
        if (FlexFanProfiles.Shipped(p.Profile, p.FanSize) is { } shipped)
        {
            return shipped.Points;
        }
        if (FlexFanProfiles.CustomKeys.Contains(p.Profile))
        {
            if (p.Custom.TryGetValue(p.Profile, out var saved) && saved.Count >= 2)
            {
                return saved.Select(x => (x.Temp, x.Rpm)).ToList();
            }
            return FlexFanProfiles.Shipped("stdsp", p.FanSize)!.Points;
        }
        return Array.Empty<(int, int)>();
    }

    /// <summary>
    /// Stores <paramref name="profile"/> for the unit and writes (or removes) its managed curve.
    /// Returns an error message, or null on success.
    /// </summary>
    public string? Apply(string mac, LianLiFlexFanProfileSettings profile)
    {
        var unit = _hub.Find(mac);
        if (unit is null)
        {
            return "fan not found";
        }
        if (!unit.Controllable)
        {
            return "fan is bound to a wireless controller";
        }
        if (!IsKnownProfile(profile.Profile))
        {
            return "unknown profile";
        }
        if (profile.Source is not ("cpu" or "gpu" or "water"))
        {
            return "unknown source";
        }
        if (profile.FanSize is not (120 or 140))
        {
            return "fan size must be 120 or 140";
        }
        var channel = FlexCoolingProvider.ChannelId(unit.Mac);
        var curveId = CurveIdOf(unit.Mac);
        profile.CurveId = curveId;
        profile.FixedPercent = Math.Clamp(profile.FixedPercent, 0, 100);

        if (profile.Profile is "" or "mbsync")
        {
            RemoveCurve(curveId);
            Persist(unit.Mac, profile);
            if (profile.Profile == "mbsync")
            {
                FanProfiles.DetachFanFromCurves(channel, _store);
                _fans.ReleaseFan(channel);
                _store.Update(s => s.Cooling.ManualSpeeds.Remove(channel));
            }
            Broadcast();
            return null;
        }

        var needsSource = profile.Profile != "fixed";
        TemperatureSource? input = null;
        if (needsSource)
        {
            input = ResolveSource(profile.Source);
            if (input is null)
            {
                return $"no {profile.Source} temperature source is available";
            }
        }
        var cooling = _store.Load().Cooling;
        var wire = cooling.Curves.Select(CurveWireMapper.ToWire).ToList();
        foreach (var c in wire)
        {
            if (c.Id != curveId)
            {
                c.Outputs.RemoveAll(o => o.Id == channel);
            }
        }
        var target = wire.FirstOrDefault(c => c.Id == curveId);
        if (target is null)
        {
            target = new Curve { Id = curveId };
            wire.Add(target);
        }
        target.Name = $"{FlexLightingNames.Model(unit.Family)} {unit.Mac[^4..]}";
        target.Preset = null;
        target.Outputs = new List<CurveOutput> { new() { Id = channel, Type = "Fan" } };
        if (profile.Profile == "fixed")
        {
            target.Type = "Flat";
            target.Flat = new FlatCurve { Speed = profile.FixedPercent };
            target.Input = new CurveInput();
        }
        else
        {
            var points = FlexFanProfiles.DutyPoints(unit.Family, EffectivePoints(profile), profile.StartStop);
            target.Type = "Graph";
            target.Input = new CurveInput { Id = input!.Id, Type = "Temperature", Device = input.Category };
            target.Graph = new GraphCurve
            {
                Points = points.Select(p => new Nexus.Service.Models.Cooling.GraphPoint { Temp = p.Temp, Speed = p.Duty }).ToList(),
            };
        }
        var body = new SetCurvesBody { GlobalSpeedModifier = cooling.GlobalSpeedModifier, Curves = wire };
        _curves.SetCurves(CoolingSafety.Sanitize(body));
        _store.Update(s => s.Cooling.ManualSpeeds.Remove(channel));
        Persist(unit.Mac, profile);
        var derived = FanProfiles.DerivePresetFromCurves(_store, _fans);
        _store.Update(s => s.Cooling.ActivePreset = derived);
        Broadcast();
        return null;
    }

    private void RemoveCurve(string curveId)
    {
        var cooling = _store.Load().Cooling;
        if (!cooling.Curves.Any(c => c.Id == curveId))
        {
            return;
        }
        var wire = cooling.Curves.Where(c => c.Id != curveId).Select(CurveWireMapper.ToWire).ToList();
        _curves.SetCurves(CoolingSafety.Sanitize(new SetCurvesBody { GlobalSpeedModifier = cooling.GlobalSpeedModifier, Curves = wire }));
    }

    private void Persist(string mac, LianLiFlexFanProfileSettings profile)
    {
        _store.Update(s =>
        {
            s.Devices.FlexUnits.TryGetValue(mac, out var old);
            var next = (old ?? new LianLiFlexUnitSettings()).Clone();
            next.FanProfile = profile;
            s.Devices.FlexUnits = new Dictionary<string, LianLiFlexUnitSettings>(s.Devices.FlexUnits, StringComparer.Ordinal)
            {
                [mac] = next,
            };
        });
    }

    private void Broadcast()
    {
        if (_mux is not null)
        {
            PanelTopics.BroadcastCooling(_mux);
        }
    }

    /// <summary>
    /// Default: the selected profile goes back to its shipped points and its duty is sent again; the
    /// selection itself stays. With no profile chosen yet it selects StdSP on the CPU, 120 mm.
    /// </summary>
    public string? Default(string mac)
    {
        var current = Current(mac);
        if (current.Profile.Length == 0 || current.Profile is "mbsync" or "fixed")
        {
            return Apply(mac, new LianLiFlexFanProfileSettings { Profile = "stdsp", Source = "cpu", FanSize = 120 });
        }
        var next = current.Clone();
        next.Custom = new Dictionary<string, List<LianLiFlexCurvePoint>>(current.Custom);
        next.Custom.Remove(next.Profile);
        return Apply(mac, next);
    }

    public LianLiFlexFanProfileSettings Current(string mac) =>
        _store.Load().Devices.FlexUnits.TryGetValue(mac, out var u) ? u.FanProfile : new LianLiFlexFanProfileSettings();

    /// <summary>Global MB RPM Sync: every unit follows the board header, or gets its stored profile back.</summary>
    public void SetMbSync(bool enabled)
    {
        foreach (var unit in _hub.Units)
        {
            if (!unit.Controllable)
            {
                continue;
            }
            var next = Current(unit.Mac).Clone();
            if (enabled && next.Profile != "mbsync")
            {
                next.BeforeSync = next.Profile;
                next.Profile = "mbsync";
                Apply(unit.Mac, next);
            }
            else if (!enabled && next.Profile == "mbsync")
            {
                next.Profile = next.BeforeSync.Length > 0 ? next.BeforeSync : "stdsp";
                next.BeforeSync = "";
                Apply(unit.Mac, next);
            }
        }
    }
}

internal static class FlexLightingNames
{
    public static string Model(FlexFamily family) => Nexus.Service.Lighting.FlexLightingDeviceProvider.ModelName(family);
}
