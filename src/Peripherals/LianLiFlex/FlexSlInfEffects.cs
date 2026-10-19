using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.LianLiFlex;

/// <summary>
/// Effect generators for the 44-LED SL-INF fan. Center is LED 0..7, inner is 8..17 and 26..35,
/// outer is 18..25 and 36..43 of each fan.
/// </summary>
public static class FlexSlInfEffects
{
    public const int LedsPerFan = 44;

    private delegate int ModeFn(FlexSlInfModes modes, int zone, int fans, int bright, int direction, int attach);

    private sealed record Entry(ModeFn Mode, Func<int, double> IntervalBase);

    public static IReadOnlyList<string> Keys { get; } = new[]
    {
        "rainbow", "rainbowMorph", "static", "breathing", "runway", "meteor", "twinkle", "taichi", "mopUp",
        "meteorRainbow", "warning", "voice", "mixing", "tide", "scan", "door", "heartBeat", "heartBeatRunway",
        "disco", "electricCurrent", "reflect", "gradientRibbon", "wing", "drumming", "boomerang", "candyBox",
    };

    private static Func<int, double> Fixed(double value) => _ => value;

    private static Func<int, double> ByFans(double oneFan, double twoFans, double threeFans, double fourFans) =>
        fans => fans switch { 1 => oneFan, 2 => twoFans, 3 => threeFans, _ => fourFans };

    private static readonly Dictionary<string, Entry> Table = new(StringComparer.Ordinal)
    {
        ["rainbow"] = new((modes, zone, fans, brightness, direction, attach) => modes.InfRainbow(zone, fans, brightness, direction, attach), Fixed(20)),
        ["rainbowMorph"] = new((modes, zone, fans, brightness, direction, attach) => modes.InfRainbowMorph(zone, fans, brightness, direction, attach), Fixed(11)),
        ["static"] = new((modes, zone, fans, brightness, direction, attach) => modes.InfStaticColor(zone, fans, brightness, direction, attach), Fixed(11)),
        ["breathing"] = new((modes, zone, fans, brightness, direction, attach) => modes.InfBreathing(zone, fans, brightness, direction, attach), Fixed(11)),
        ["runway"] = new((modes, zone, fans, brightness, direction, attach) => modes.InfRunway(zone, fans, brightness, direction, attach), Fixed(20)),
        ["meteor"] = new((modes, zone, fans, brightness, direction, attach) => modes.InfMeteor(zone, fans, brightness, direction, attach), Fixed(20)),
        ["twinkle"] = new((modes, zone, fans, brightness, direction, attach) => modes.InfTwinkle(zone, fans, brightness, direction, attach), Fixed(20)),
        ["taichi"] = new((modes, zone, fans, brightness, direction, attach) => modes.InfTaichi(zone, fans, brightness, direction, attach), Fixed(20)),
        ["mopUp"] = new((modes, zone, fans, brightness, direction, attach) => modes.InfMopUp(zone, fans, brightness, direction, attach), Fixed(20)),
        ["meteorRainbow"] = new((modes, zone, fans, brightness, direction, attach) => modes.InfMeteorRainbow(zone, fans, brightness, direction, attach), Fixed(20)),
        ["warning"] = new((modes, zone, fans, brightness, direction, attach) => modes.InfWarning(zone, fans, brightness, direction, attach), Fixed(20)),
        ["voice"] = new((modes, zone, fans, brightness, direction, attach) => modes.InfVoice(zone, fans, brightness, direction, attach), Fixed(20)),
        ["mixing"] = new((modes, zone, fans, brightness, direction, attach) => modes.InfMixing(zone, fans, brightness, direction, attach), Fixed(20)),
        ["tide"] = new((modes, zone, fans, brightness, direction, attach) => modes.InfTide(zone, fans, brightness, direction, attach), Fixed(20)),
        ["scan"] = new((modes, zone, fans, brightness, direction, attach) => modes.InfScan(zone, fans, brightness, direction, attach), Fixed(20)),
        ["door"] = new((modes, zone, fans, brightness, direction, attach) => modes.InfDoor(zone, fans, brightness, direction, attach), Fixed(30)),
        ["heartBeat"] = new((modes, zone, fans, brightness, direction, attach) => modes.InfHeartBeat(zone, fans, brightness, direction, attach), Fixed(20)),
        ["heartBeatRunway"] = new((modes, zone, fans, brightness, direction, attach) => modes.InfHeartBeatRunway(zone, fans, brightness, direction, attach), Fixed(10)),
        ["disco"] = new((modes, zone, fans, brightness, direction, attach) => modes.InfDisco(zone, fans, brightness, direction, attach), Fixed(20)),
        ["electricCurrent"] = new((modes, zone, fans, brightness, direction, attach) => modes.InfElectricCurrent(zone, fans, brightness, direction, attach), ByFans(40, 20, 15, 10)),
        ["reflect"] = new((modes, zone, fans, brightness, direction, attach) => modes.InfReflect(zone, fans, brightness, direction, attach), Fixed(20)),
        ["gradientRibbon"] = new((modes, zone, fans, brightness, direction, attach) => modes.InfGradientRibbon(zone, fans, brightness, direction, attach), Fixed(20)),
        ["wing"] = new((modes, zone, fans, brightness, direction, attach) => modes.InfWing(zone, fans, brightness, direction, attach), ByFans(50, 30, 20, 18)),
        ["drumming"] = new((modes, zone, fans, brightness, direction, attach) => modes.InfDrumming(zone, fans, brightness, direction, attach), Fixed(20)),
        ["boomerang"] = new((modes, zone, fans, brightness, direction, attach) => modes.InfBoomerang(zone, fans, brightness, direction, attach), ByFans(50, 35, 20, 20)),
        ["candyBox"] = new((modes, zone, fans, brightness, direction, attach) => modes.InfCandyBox(zone, fans, brightness, direction, attach), Fixed(20)),
    };

    public static bool IsImplemented(string key) => Table.ContainsKey(key);

    /// <summary>Per-frame base interval of a key for a fan count, before the speed multiplier.</summary>
    public static double IntervalBaseFor(string key, int fans) => Table[key].IntervalBase(ClampFans(fans));

    private static int ClampFans(int fans) => fans is < 1 or > 4 ? 4 : fans;

    /// <summary>
    /// Zone scopes (Outer, Inner or Up for inner, Center) fill only that zone's LEDs. All renders the
    /// same effect in all three zones and merges them.
    /// </summary>
    public static FlexRawAnimation Render(string key, FlexEffectRequest req)
    {
        if (!Table.TryGetValue(key, out var entry))
        {
            throw new NotSupportedException($"SL-INF effect '{key}' has no generator.");
        }
        var fans = ClampFans(req.Fans);
        var leds = fans * LedsPerFan;
        if (req.Scope == FlexScope.All)
        {
            var inner = RenderZone(entry, 1, fans, req);
            var outer = RenderZone(entry, 0, fans, req);
            var center = RenderZone(entry, 2, fans, req);
            var (frames, count, interval) = FlexSlInfCombiner.Combine(inner, outer, center, fans);
            return new FlexRawAnimation
            {
                Frames = FlexPalette.Flatten(frames, count, leds),
                FrameCount = count,
                Leds = leds,
                IntervalBaseMs = interval / req.SpeedMultiplier,
            };
        }
        var zone = req.Scope switch
        {
            FlexScope.Outer => 0,
            FlexScope.Inner or FlexScope.Up => 1,
            FlexScope.Center => 2,
            _ => throw new ArgumentException($"SL-INF scope {req.Scope} is not a zone.", nameof(req)),
        };
        var single = RenderZone(entry, zone, fans, req);
        return new FlexRawAnimation
        {
            Frames = FlexPalette.Flatten(single.Frames, single.FrameCount, leds),
            FrameCount = single.FrameCount,
            Leds = leds,
            IntervalBaseMs = single.IntervalBase,
        };
    }

    internal static FlexSlInfZone RenderZone(string key, int zone, FlexEffectRequest req)
    {
        var fans = ClampFans(req.Fans);
        return RenderZone(Table[key], zone, fans, req);
    }

    private static FlexSlInfZone RenderZone(Entry entry, int zone, int fans, FlexEffectRequest req)
    {
        var modes = new FlexSlInfModes(fans, FlexPalette.Build(req.Colors));
        var count = entry.Mode(modes, zone, fans, req.BrightnessLevel, req.Direction, req.RightAttach);
        var baseMs = entry.IntervalBase(fans);
        return new FlexSlInfZone
        {
            Frames = modes.Frames,
            FrameCount = count,
            IntervalBase = baseMs,
            Interval = req.SpeedMultiplier * baseMs,
            SpeedMultiplier = req.SpeedMultiplier,
        };
    }
}
