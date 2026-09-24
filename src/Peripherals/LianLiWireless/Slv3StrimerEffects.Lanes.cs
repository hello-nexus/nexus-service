using System;
using System.Collections.Generic;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Peripherals.LianLiWireless;

public static partial class Slv3StrimerEffects
{
    public static IReadOnlyList<string> LaneEffectKeys { get; } =
        new[] { "rainbow", "wave", "static", "breathing", "rainbowMorph", "painting" };

    private delegate RgbColor LanePainter(int frame, int frameCount, int ledsPerLane, int direction, RgbColor color, int led);

    private static readonly IReadOnlyDictionary<string, LanePainter> LaneRenderers =
        new Dictionary<string, LanePainter>(StringComparer.Ordinal)
        {
            ["rainbow"] = (f, total, ledsPerLane, direction, _, led) =>
            {
                var pos = Pos(led, ledsPerLane, direction);
                return Hue((double)pos / ledsPerLane + (double)f / total);
            },
            ["wave"] = (f, total, ledsPerLane, direction, color, led) =>
            {
                var pos = Pos(led, ledsPerLane, direction);
                var t = (double)pos / ledsPerLane - (double)f / total;
                var brightness = (Math.Sin(t * 2 * Math.PI) + 1.0) / 2.0;
                return Scale(color, 0.1 + 0.9 * brightness);
            },
            ["static"] = (_, _, _, _, color, _) => color,
            ["breathing"] = (f, total, _, _, color, _) => Scale(color, 0.05 + 0.95 * Breath((double)f / total)),
            ["rainbowMorph"] = (f, total, _, direction, _, _) => Hue((direction != 0 ? -1.0 : 1.0) * f / total),
            ["painting"] = (f, total, ledsPerLane, direction, color, led) =>
            {
                var pos = Pos(led, ledsPerLane, direction);
                var revealed = (int)((double)f / total * ledsPerLane) + 1;
                return pos < revealed ? color : default;
            },
        };

    public static Slv3StrimerAnimation RenderPerLane(
        int lanes, int ledsPerLane, int speed, IReadOnlyList<(string Key, int Direction, RgbColor Color)> laneSettings)
    {
        ValidateGeometry(lanes, ledsPerLane);
        if (laneSettings.Count != lanes)
        {
            throw new ArgumentException($"Expected {lanes} lane settings, got {laneSettings.Count}", nameof(laneSettings));
        }

        const int frameCount = 48;
        var intervalMs = SpeedMultiplier[ClampSpeed(speed)] * 20.0;
        var buf = AllocateFrames(frameCount, lanes, ledsPerLane);
        for (var lane = 0; lane < lanes; lane++)
        {
            var (key, direction, color) = laneSettings[lane];
            var painter = LaneRenderers.TryGetValue(key, out var found) ? found : LaneRenderers["static"];
            var dir = NormalizeDirection(direction);
            for (var f = 0; f < frameCount; f++)
            {
                for (var led = 0; led < ledsPerLane; led++)
                {
                    SetLed(buf, f, lanes, ledsPerLane, lane, led, painter(f, frameCount, ledsPerLane, dir, color, led));
                }
            }
        }
        return Finalize(new RawAnimation(buf, frameCount, intervalMs), lanes, ledsPerLane);
    }
}
