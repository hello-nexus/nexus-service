using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.Strimer;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>An effect with every option resolved: what one cable or port plays.</summary>
public sealed record StrimerLook(StrimerModeInfo Mode, int Speed, int Direction, int Brightness, string[] Colors);

/// <summary>One zone's firmware effect command and color report.</summary>
public readonly record struct StrimerZonePlan(int Zone, StrimerModeInfo Mode, byte Speed, byte Direction, byte Brightness, byte[] Colors);

public static class StrimerEffectLooks
{
    public const string AllTarget = "all";

    /// <summary>"ch0" and "ch1" for the cables, "p0".."p11" for single ports; null for anything else.</summary>
    public static string? NormalizeTarget(string? target)
    {
        if (target is "ch0" or "ch1") return target;
        if (target is { Length: >= 2 } && target[0] == 'p' && int.TryParse(target.AsSpan(1), out var p) && p is >= 0 and < StrimerProtocol.ZoneCount
            && target == "p" + p)
        {
            return target;
        }
        return null;
    }

    private static string ShownKey(StrimerLightingSettings ls) =>
        ls.Mode != "custom" ? ls.Mode : ls.EffectMode ?? "rainbow";

    private static StrimerModeInfo FallbackMode => StrimerLightingModes.Find("rainbow")!;

    /// <summary>The shared effect, which is the one the Lighting page hands back to.</summary>
    public static StrimerLook Shared(StrimerLightingSettings ls)
    {
        var mode = StrimerLightingModes.Find(ShownKey(ls)) ?? FallbackMode;
        return new StrimerLook(
            mode,
            StrimerLightingModes.ResolveSpeed(ls.ModeSpeeds, ls.Speed, mode),
            ls.Direction,
            ls.Brightness,
            StrimerLightingModes.ResolveColors(ls, mode));
    }

    public static StrimerLook Own(StrimerTargetLook t)
    {
        var mode = StrimerLightingModes.Find(t.Mode) ?? FallbackMode;
        return new StrimerLook(
            mode,
            StrimerLightingModes.ResolveSpeed(t.ModeSpeeds, 0, mode),
            t.Direction,
            t.Brightness,
            StrimerLightingModes.ResolveColors(t.ModeColors, mode));
    }

    /// <summary>What the target shows: its own effect, else the cable's, else the shared one.</summary>
    public static StrimerLook Resolve(StrimerLightingSettings ls, string target)
    {
        if (ls.Targets.TryGetValue(target, out var own)) return Own(own);
        if (target[0] == 'p' && ls.Targets.TryGetValue(CableOf(target), out var cable)) return Own(cable);
        return Shared(ls);
    }

    public static string CableOf(string port) => int.Parse(port.AsSpan(1)) < StrimerProtocol.AtxZoneCount ? "ch0" : "ch1";

    /// <summary>A target's first edit starts from what it showed, so only the edited field changes.</summary>
    public static StrimerTargetLook Seed(StrimerLightingSettings ls, string target)
    {
        var look = Resolve(ls, target);
        var seeded = new StrimerTargetLook { Mode = look.Mode.Key, Direction = look.Direction, Brightness = look.Brightness };
        seeded.ModeSpeeds[look.Mode.Key] = look.Speed;
        if (look.Mode.DefaultColors.Length > 0) seeded.ModeColors[look.Mode.Key] = new List<string>(look.Colors);
        return seeded;
    }

    /// <summary>
    /// The zones to write. A channel-wide effect on a cable is one command on its first port;
    /// any other effect goes to each port the cable has, a port keeping its own effect if it has one.
    /// </summary>
    public static List<StrimerZonePlan> BuildPlan(StrimerLightingSettings ls, MasterLevel globalBrightness, int gpuZones = StrimerProtocol.GpuZoneCount)
    {
        var plan = new List<StrimerZonePlan>(StrimerProtocol.ZoneCount);
        for (var cable = 0; cable < 2; cable++)
        {
            var first = cable * StrimerProtocol.AtxZoneCount;
            var cableLook = ls.Targets.TryGetValue("ch" + cable, out var own) ? Own(own) : Shared(ls);
            if (cableLook.Mode.Complete)
            {
                plan.Add(Plan(first, cableLook, globalBrightness));
                continue;
            }
            var count = cable == 0 ? StrimerProtocol.AtxZoneCount : StrimerProtocol.NormalizeGpuZoneCount(gpuZones);
            for (var zone = first; zone < first + count; zone++)
            {
                var look = cableLook;
                if (ls.Targets.TryGetValue("p" + zone, out var port))
                {
                    var portLook = Own(port);
                    if (!portLook.Mode.Complete) look = portLook;
                }
                plan.Add(Plan(zone, look, globalBrightness));
            }
        }
        return plan;
    }

    private static StrimerZonePlan Plan(int zone, StrimerLook look, MasterLevel globalBrightness) => new(
        zone,
        look.Mode,
        look.Mode.HasSpeed ? StrimerLightingModes.SpeedCodes[Math.Clamp(look.Speed, 0, 4)] : (byte)0x00,
        StrimerLightingModes.DirectionByte(look.Mode, look.Direction),
        StrimerLightingFrameWriter.ComputeBrightnessByte(look.Brightness, globalBrightness),
        StrimerLightingModes.FirmwareColors(look.Mode, look.Colors, look.Brightness));
}
