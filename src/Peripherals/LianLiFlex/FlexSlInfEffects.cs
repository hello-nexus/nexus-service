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

    private static Func<int, double> Fixed(double v) => _ => v;

    private static Func<int, double> ByFans(double f1, double f2, double f3, double f4) =>
        fans => fans switch { 1 => f1, 2 => f2, 3 => f3, _ => f4 };

    private static readonly Dictionary<string, Entry> Table = new(StringComparer.Ordinal)
    {
        ["rainbow"] = new((m, z, f, b, d, a) => m.InfRainbow(z, f, b, d, a), Fixed(20)),
        ["rainbowMorph"] = new((m, z, f, b, d, a) => m.InfRainbowMorph(z, f, b, d, a), Fixed(11)),
        ["static"] = new((m, z, f, b, d, a) => m.InfStaticColor(z, f, b, d, a), Fixed(11)),
        ["breathing"] = new((m, z, f, b, d, a) => m.InfBreathing(z, f, b, d, a), Fixed(11)),
        ["runway"] = new((m, z, f, b, d, a) => m.InfRunway(z, f, b, d, a), Fixed(20)),
        ["meteor"] = new((m, z, f, b, d, a) => m.InfMeteor(z, f, b, d, a), Fixed(20)),
        ["twinkle"] = new((m, z, f, b, d, a) => m.InfTwinkle(z, f, b, d, a), Fixed(20)),
        ["taichi"] = new((m, z, f, b, d, a) => m.InfTaichi(z, f, b, d, a), Fixed(20)),
        ["mopUp"] = new((m, z, f, b, d, a) => m.InfMopUp(z, f, b, d, a), Fixed(20)),
        ["meteorRainbow"] = new((m, z, f, b, d, a) => m.InfMeteorRainbow(z, f, b, d, a), Fixed(20)),
        ["warning"] = new((m, z, f, b, d, a) => m.InfWarning(z, f, b, d, a), Fixed(20)),
        ["voice"] = new((m, z, f, b, d, a) => m.InfVoice(z, f, b, d, a), Fixed(20)),
        ["mixing"] = new((m, z, f, b, d, a) => m.InfMixing(z, f, b, d, a), Fixed(20)),
        ["tide"] = new((m, z, f, b, d, a) => m.InfTide(z, f, b, d, a), Fixed(20)),
        ["scan"] = new((m, z, f, b, d, a) => m.InfScan(z, f, b, d, a), Fixed(20)),
        ["door"] = new((m, z, f, b, d, a) => m.InfDoor(z, f, b, d, a), Fixed(30)),
        ["heartBeat"] = new((m, z, f, b, d, a) => m.InfHeartBeat(z, f, b, d, a), Fixed(20)),
        ["heartBeatRunway"] = new((m, z, f, b, d, a) => m.InfHeartBeatRunway(z, f, b, d, a), Fixed(10)),
        ["disco"] = new((m, z, f, b, d, a) => m.InfDisco(z, f, b, d, a), Fixed(20)),
        ["electricCurrent"] = new((m, z, f, b, d, a) => m.InfElectricCurrent(z, f, b, d, a), ByFans(40, 20, 15, 10)),
        ["reflect"] = new((m, z, f, b, d, a) => m.InfReflect(z, f, b, d, a), Fixed(20)),
        ["gradientRibbon"] = new((m, z, f, b, d, a) => m.InfGradientRibbon(z, f, b, d, a), Fixed(20)),
        ["wing"] = new((m, z, f, b, d, a) => m.InfWing(z, f, b, d, a), ByFans(50, 30, 20, 18)),
        ["drumming"] = new((m, z, f, b, d, a) => m.InfDrumming(z, f, b, d, a), Fixed(20)),
        ["boomerang"] = new((m, z, f, b, d, a) => m.InfBoomerang(z, f, b, d, a), ByFans(50, 35, 20, 20)),
        ["candyBox"] = new((m, z, f, b, d, a) => m.InfCandyBox(z, f, b, d, a), Fixed(20)),
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
            var (frames, count, interval) = FlexSlInfCombiner.Combine(inner, outer, center, fans, req.SpeedMultiplier);
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
        var z = RenderZone(entry, zone, fans, req);
        return new FlexRawAnimation
        {
            Frames = FlexPalette.Flatten(z.Frames, z.FrameCount, leds),
            FrameCount = z.FrameCount,
            Leds = leds,
            IntervalBaseMs = z.IntervalBase,
        };
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
        };
    }
}
