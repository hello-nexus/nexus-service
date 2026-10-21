using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.LianLiFlex;

/// <summary>How many color swatches each effect offers, per family and for the group (Sync) effects.</summary>
public static class FlexSwatchCounts
{
    private static Dictionary<string, int> Table(params (int Count, string[] Keys)[] groups)
    {
        var table = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (count, keys) in groups)
        {
            foreach (var key in keys)
            {
                table[key] = count;
            }
        }
        return table;
    }

    private static readonly Dictionary<string, int> Tl = Table(
        (0, ["rainbow", "rainbowMorph", "voice", "kaleidoscope", "twinkle"]),
        (2, ["runway", "staggered", "tide", "mixing", "tailChasing", "racing", "lottery", "intertwine"]),
        (3, ["colorCycle", "coverCycle"]),
        (4, ["static", "breathing", "meteor", "door", "render", "ripple", "reflect", "paint", "pingPong", "stack", "wave", "meteorShower", "collide", "electricCurrent"]));

    private static readonly Dictionary<string, int> SlInf = Table(
        (0, ["rainbow", "rainbowMorph", "twinkle", "meteorRainbow", "gradientRibbon", "candyBox"]),
        (1, ["scan", "heartBeat", "heartBeatRunway"]),
        (2, ["runway", "taichi", "mixing", "drumming", "boomerang"]),
        (4, ["static", "breathing", "meteor", "mopUp", "warning", "voice", "tide", "door", "disco", "electricCurrent", "reflect", "wing"]));

    private static readonly Dictionary<string, int> Sync = Table(
        (0, ["rainbow", "rainbowMorph", "twinkle"]),
        (1, ["static", "breathing", "meteor", "stack", "wave"]),
        (2, ["runway", "coverCycle"]),
        (3, ["colorCycle"]),
        (4, ["meteorShower"]));

    public static int Of(FlexFamily family, string key) =>
        (family == FlexFamily.SlInfFlex ? SlInf : Tl).GetValueOrDefault(key);

    public static int OfSync(string key) => Sync.GetValueOrDefault(key);
}
