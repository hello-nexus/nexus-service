using System.Collections.Generic;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Peripherals.LianLiFlex;

/// <summary>The colors an effect plays when the user has picked none, for the effects that read a palette.</summary>
public static class FlexDefaultPalettes
{
    private static readonly Dictionary<(FlexFamily, string), RgbColor[]> Sets = new()
    {
        [(FlexFamily.SlInfFlex, "boomerang")] = new RgbColor[] { new(255, 0, 150), new(255, 215, 0) },
        [(FlexFamily.SlInfFlex, "breathing")] = new RgbColor[] { new(170, 0, 255), new(0, 215, 255), new(0, 255, 0), new(255, 216, 0) },
        [(FlexFamily.SlInfFlex, "disco")] = new RgbColor[] { new(255, 0, 0), new(0, 255, 0), new(0, 0, 255) },
        [(FlexFamily.SlInfFlex, "door")] = new RgbColor[] { new(255, 0, 0), new(0, 0, 255), new(0, 255, 0), new(255, 105, 0) },
        [(FlexFamily.SlInfFlex, "drumming")] = new RgbColor[] { new(255, 0, 0) },
        [(FlexFamily.SlInfFlex, "electricCurrent")] = new RgbColor[] { new(255, 0, 0), new(0, 0, 255), new(0, 255, 0) },
        [(FlexFamily.SlInfFlex, "meteor")] = new RgbColor[] { new(255, 0, 150), new(0, 215, 255), new(0, 255, 0), new(255, 215, 0) },
        [(FlexFamily.SlInfFlex, "mixing")] = new RgbColor[] { new(255, 0, 0), new(0, 0, 255) },
        [(FlexFamily.SlInfFlex, "mopUp")] = new RgbColor[] { new(255, 0, 0), new(0, 0, 255), new(0, 255, 0), new(255, 105, 0) },
        [(FlexFamily.SlInfFlex, "reflect")] = new RgbColor[] { new(255, 0, 150), new(0, 215, 255), new(0, 255, 0) },
        [(FlexFamily.SlInfFlex, "runway")] = new RgbColor[] { new(255, 255, 255), new(255, 0, 150) },
        [(FlexFamily.SlInfFlex, "static")] = new RgbColor[] { new(170, 0, 255), new(0, 215, 255), new(0, 255, 0), new(255, 216, 0) },
        [(FlexFamily.SlInfFlex, "taichi")] = new RgbColor[] { new(255, 0, 0), new(0, 128, 0) },
        [(FlexFamily.SlInfFlex, "tide")] = new RgbColor[] { new(255, 0, 0), new(0, 0, 255), new(0, 255, 0), new(255, 105, 0) },
        [(FlexFamily.SlInfFlex, "voice")] = new RgbColor[] { new(255, 0, 0), new(0, 255, 0), new(0, 0, 255) },
        [(FlexFamily.SlInfFlex, "warning")] = new RgbColor[] { new(255, 0, 0), new(0, 0, 255), new(0, 255, 0), new(255, 105, 0) },
        [(FlexFamily.SlInfFlex, "wing")] = new RgbColor[] { new(170, 0, 255), new(255, 0, 150), new(0, 215, 255), new(0, 255, 0) },
        [(FlexFamily.TlFlex, "breathing")] = new RgbColor[] { new(170, 0, 255), new(0, 215, 255), new(0, 255, 0), new(255, 216, 0) },
        [(FlexFamily.TlFlex, "colorCycle")] = new RgbColor[] { new(255, 215, 0), new(0, 255, 0), new(0, 215, 255) },
        [(FlexFamily.TlFlex, "meteor")] = new RgbColor[] { new(255, 0, 150), new(0, 215, 255), new(0, 255, 0), new(255, 215, 0) },
        [(FlexFamily.TlFlex, "runway")] = new RgbColor[] { new(255, 255, 255), new(255, 0, 150) },
        [(FlexFamily.TlFlex, "static")] = new RgbColor[] { new(170, 0, 255), new(0, 215, 255), new(0, 255, 0), new(255, 216, 0) },
        [(FlexFamily.TlFlex, "stack")] = new RgbColor[] { new(255, 0, 0), new(0, 0, 255), new(0, 255, 0), new(255, 255, 0) },
        [(FlexFamily.TlFlex, "twinkle")] = new RgbColor[] { new(255, 0, 0), new(0, 0, 255), new(0, 255, 0), new(255, 255, 0) },
        [(FlexFamily.SlInfFlex, "twinkle")] = new RgbColor[] { new(255, 0, 0), new(255, 255, 0), new(255, 0, 255), new(255, 255, 0) },
        [(FlexFamily.SlInfFlex, "scan")] = new RgbColor[] { new(255, 0, 0), new(0, 0, 255), new(0, 255, 0), new(255, 255, 0) },
        [(FlexFamily.SlInfFlex, "heartBeat")] = new RgbColor[] { new(255, 0, 0), new(0, 0, 255), new(0, 255, 0), new(255, 255, 0) },
        [(FlexFamily.SlInfFlex, "heartBeatRunway")] = new RgbColor[] { new(255, 0, 0), new(0, 0, 255), new(0, 255, 0), new(255, 255, 0) },
    };

    /// <summary>
    /// Effects whose built-in colors differ per half of a fan, so one list cannot stand for them: with no
    /// user colors they render straight from the generator's table, and the list above is only what a
    /// color picker starts from.
    /// </summary>
    public static bool RendersFromTable(FlexFamily family, string key) => family == FlexFamily.TlFlex && key == "twinkle";

    /// <summary>Colors for merged effects that read a palette when the leader has none and the effect has no default of its own.</summary>
    public static readonly IReadOnlyList<RgbColor> MergeFallback = new RgbColor[]
    {
        new(170, 0, 255), new(0, 215, 255), new(0, 255, 0), new(255, 216, 0),
    };

    /// <summary>The effect's default colors; empty when the generator's own table applies.</summary>
    public static IReadOnlyList<RgbColor> For(FlexFamily family, string key)
    {
        if (Sets.TryGetValue((family, key), out var set))
        {
            return set;
        }
        return family == FlexFamily.TlFlex ? FlexTlExtendedEffects.DefaultColors(key) : System.Array.Empty<RgbColor>();
    }
}
