using System;
using System.Collections.Generic;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>Static metadata describing one Strimer animation preset for the UI.</summary>
public sealed class Slv3StrimerEffectInfo
{
    public required string Key { get; init; }
    public bool HasSpeed { get; init; }
    public bool HasDirection { get; init; }
    public int ColorsMin { get; init; }
    public int ColorsMax { get; init; }
}

/// <summary>A rendered, looping Strimer animation ready for the RF upload pipeline.</summary>
public sealed class Slv3StrimerAnimation
{
    public required byte[] Frames { get; init; }
    public required int FrameCount { get; init; }
    public required double IntervalMs { get; init; }
}

/// <summary>
/// Renders looping cable animations entirely in memory: the cable's firmware
/// only plays back an uploaded frame set, so every mode's motion, color cycling
/// and timing has to be baked into the frame buffer up front.
/// </summary>
public static partial class Slv3StrimerEffects
{
    public const int SpeedLevels = 5;

    private static readonly double[] SpeedMultiplier = { 7.0, 6.0, 5.0, 4.0, 3.0 };

    private static readonly (int Lanes, int LedsPerLane)[] ValidGeometries =
    {
        (4, 29), (4, 22), (6, 22), (6, 29),
    };

    public static IReadOnlyList<RgbColor> DefaultColors { get; } = new[]
    {
        new RgbColor(255, 0, 0),
        new RgbColor(0, 255, 0),
        new RgbColor(0, 0, 255),
        new RgbColor(255, 255, 0),
        new RgbColor(0, 255, 255),
        new RgbColor(255, 0, 255),
    };

    public static IReadOnlyList<Slv3StrimerEffectInfo> Catalog { get; } = BuildCatalog();

    private static readonly IReadOnlyDictionary<string, Slv3StrimerEffectInfo> CatalogByKey =
        BuildCatalogIndex();

    public static Slv3StrimerEffectInfo? Find(string key) =>
        CatalogByKey.TryGetValue(key, out var info) ? info : null;

    private static Slv3StrimerEffectInfo[] BuildCatalog() => new[]
    {
        new Slv3StrimerEffectInfo { Key = "rainbow", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 0 },
        new Slv3StrimerEffectInfo { Key = "rainbowWave", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 0 },
        new Slv3StrimerEffectInfo { Key = "rainbowMorph", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 0 },
        new Slv3StrimerEffectInfo { Key = "static", HasSpeed = false, HasDirection = false, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "breathing", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "wave", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 2 },
        new Slv3StrimerEffectInfo { Key = "painting", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 6 },
        new Slv3StrimerEffectInfo { Key = "colorTransfer", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 6 },
        new Slv3StrimerEffectInfo { Key = "fadeOut", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 6 },
        new Slv3StrimerEffectInfo { Key = "contest", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 6 },
        new Slv3StrimerEffectInfo { Key = "crossOver", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 6 },
        new Slv3StrimerEffectInfo { Key = "bulletStack", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 6 },
        new Slv3StrimerEffectInfo { Key = "twinkle", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 6 },
        new Slv3StrimerEffectInfo { Key = "parallel", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 6 },
        new Slv3StrimerEffectInfo { Key = "shockWave", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 6 },
        new Slv3StrimerEffectInfo { Key = "ripple", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 0 },
        new Slv3StrimerEffectInfo { Key = "voice", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 6 },
        new Slv3StrimerEffectInfo { Key = "drizzling", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "endless", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 6 },
        new Slv3StrimerEffectInfo { Key = "shuttleRun", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 6 },
        new Slv3StrimerEffectInfo { Key = "river", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 6 },
        new Slv3StrimerEffectInfo { Key = "hourglass", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 2 },
        new Slv3StrimerEffectInfo { Key = "pioneer", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 2 },
        new Slv3StrimerEffectInfo { Key = "electricCurrent", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "transformation", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 2 },
        new Slv3StrimerEffectInfo { Key = "gradientRibbon", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 3 },
        new Slv3StrimerEffectInfo { Key = "snooker", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 6 },
        new Slv3StrimerEffectInfo { Key = "mixing", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 2 },
        new Slv3StrimerEffectInfo { Key = "pingPong", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 2 },
        new Slv3StrimerEffectInfo { Key = "runway", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 2 },
        new Slv3StrimerEffectInfo { Key = "tide", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 6 },
        new Slv3StrimerEffectInfo { Key = "blowUp", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "meteor", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "stack", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 6 },
    };

    private static Dictionary<string, Slv3StrimerEffectInfo> BuildCatalogIndex()
    {
        var map = new Dictionary<string, Slv3StrimerEffectInfo>(StringComparer.Ordinal);
        foreach (var info in BuildCatalog())
        {
            map[info.Key] = info;
        }
        return map;
    }

    public static Slv3StrimerAnimation Render(
        string key, int lanes, int ledsPerLane, int speed, int direction, IReadOnlyList<RgbColor> colors)
    {
        var info = Find(key) ?? throw new ArgumentException($"Unknown Strimer effect key '{key}'", nameof(key));
        ValidateGeometry(lanes, ledsPerLane);
        var ctx = new EffectContext(lanes, ledsPerLane, NormalizeDirection(direction), ClampSpeed(speed), ResolveColors(colors));
        var raw = ModeRenderers[key](ctx);
        return Finalize(raw, lanes, ledsPerLane);
    }

    private static void ValidateGeometry(int lanes, int ledsPerLane)
    {
        foreach (var (l, p) in ValidGeometries)
        {
            if (l == lanes && p == ledsPerLane)
            {
                return;
            }
        }
        throw new ArgumentException($"Unsupported Strimer geometry: {lanes} lanes x {ledsPerLane} LEDs", nameof(ledsPerLane));
    }

    private static int NormalizeDirection(int direction) => direction != 0 ? 1 : 0;

    private static int ClampSpeed(int speed) => Math.Clamp(speed, 0, SpeedLevels - 1);

    private static RgbColor[] ResolveColors(IReadOnlyList<RgbColor> colors)
    {
        var resolved = new RgbColor[6];
        for (var i = 0; i < 6; i++)
        {
            resolved[i] = colors.Count > i ? colors[i] : DefaultColors[i];
        }
        return resolved;
    }

    internal static double ScaledInterval(int speed, double baseMs) => baseMs * SpeedMultiplier[speed];

    /// <summary>Shared per-effect render state: geometry, user intent and resolved colors.</summary>
    private readonly struct EffectContext
    {
        public EffectContext(int lanes, int ledsPerLane, int direction, int speed, RgbColor[] colors)
        {
            Lanes = lanes;
            LedsPerLane = ledsPerLane;
            Direction = direction;
            Speed = speed;
            Colors = colors;
        }

        public int Lanes { get; }
        public int LedsPerLane { get; }
        public int Direction { get; }
        public int Speed { get; }
        public RgbColor[] Colors { get; }
    }
}
