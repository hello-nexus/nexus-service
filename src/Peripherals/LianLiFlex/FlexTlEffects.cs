using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.LianLiFlex;

/// <summary>Effect generators for the 26-LED TL fan. Keys not in the renderer table are catalog-only.</summary>
public static class FlexTlEffects
{
    public const int LedsPerFan = 26;
    public const int HalfLeds = 13;
    private const double IntervalBase = 11.0;

    private delegate int ModeFn(FlexTlModes modes, int tb, int fans, int bright, int direction);

    public static IReadOnlyList<string> Keys { get; } = new[]
    {
        "rainbow", "rainbowMorph", "static", "breathing", "runway", "meteor", "colorCycle", "staggered", "tide",
        "mixing", "voice", "door", "render", "ripple", "reflect", "tailChasing", "paint", "pingPong", "stack",
        "coverCycle", "wave", "racing", "lottery", "intertwine", "meteorShower", "collide", "electricCurrent",
        "kaleidoscope", "twinkle",
    };

    private static readonly Dictionary<string, ModeFn> Renderers = new(StringComparer.Ordinal)
    {
        ["rainbow"] = (m, tb, f, b, d) => m.TlRainbow(tb, f, b, d),
        ["rainbowMorph"] = (m, tb, f, b, d) => m.TlRainbowMorph(tb, f, b, d),
        ["static"] = (m, tb, f, b, d) => m.TlStaticColor(tb, f, b, d),
        ["breathing"] = (m, tb, f, b, d) => m.TlBreathing(tb, f, b, d),
        ["runway"] = (m, tb, f, b, d) => m.TlRunway(tb, f, b, d),
        ["meteor"] = (m, tb, f, b, d) => m.TlMeteor(tb, f, b, d),
        ["colorCycle"] = (m, tb, f, b, d) => m.TlColorCycle(tb, f, b, d),
        ["stack"] = (m, tb, f, b, d) => m.TlStack(tb, f, b, d),
        ["twinkle"] = (m, tb, f, b, d) => m.TlTwinkle(tb, f, b, d),
    };

    public static bool IsImplemented(string key) => Renderers.ContainsKey(key);

    public static double IntervalBaseFor(string key) => IntervalBase;

    /// <summary>
    /// Scope Up fills LED 0..12 of each fan and Down fills 13..25, the other half stays black.
    /// All fills both halves with the same effect.
    /// </summary>
    public static FlexRawAnimation Render(string key, FlexEffectRequest req)
    {
        if (!Renderers.TryGetValue(key, out var fn))
        {
            throw new NotSupportedException($"TL effect '{key}' has no generator yet.");
        }
        var fans = req.Fans is < 1 or > 4 ? 4 : req.Fans;
        var leds = fans * LedsPerFan;
        var palette = FlexPalette.Build(req.Colors);
        var direction = req.Direction;
        switch (req.Scope)
        {
            case FlexScope.Up:
            {
                var m = new FlexTlModes(fans, palette);
                var n = fn(m, 0, fans, req.BrightnessLevel, direction);
                return Pack(m.Frames, n, leds);
            }
            case FlexScope.Down:
            {
                var m = new FlexTlModes(fans, palette);
                var n = fn(m, 1, fans, req.BrightnessLevel, direction);
                return Pack(m.Frames, n, leds);
            }
            case FlexScope.All:
            {
                var up = new FlexTlModes(fans, palette);
                var n = fn(up, 0, fans, req.BrightnessLevel, direction);
                var down = new FlexTlModes(fans, palette);
                var nd = fn(down, 1, fans, req.BrightnessLevel, direction);
                var count = Math.Min(n, nd);
                var f = up.Frames;
                var g = down.Frames;
                for (var i = 0; i < count; i++)
                {
                    for (var c = 0; c < 3; c++)
                    {
                        for (var j = 0; j < leds; j += LedsPerFan)
                        {
                            for (var k = j + HalfLeds; k < j + LedsPerFan; k++)
                            {
                                f[i, c, k] = g[i, c, k];
                            }
                        }
                    }
                }
                return Pack(f, count, leds);
            }
            default:
                throw new ArgumentException($"TL scope {req.Scope} is not a TL zone.", nameof(req));
        }
    }

    private static FlexRawAnimation Pack(byte[,,] frames, int count, int leds) => new()
    {
        Frames = FlexPalette.Flatten(frames, count, leds),
        FrameCount = count,
        Leds = leds,
        IntervalBaseMs = IntervalBase,
    };
}
