using System;
using System.Collections.Generic;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// Renders looping cable animations for a wireless fan chain (1..4 fans behind
/// one RF record), the same way <see cref="Slv3StrimerEffects"/> does for a
/// Strimer cable: the firmware only plays back an uploaded frame set, so every
/// mode's motion, colour cycling and timing is baked into the buffer up front.
/// Only the whole-fan render scope is implemented; a fan's ring positions all
/// show the same content rather than splitting into inner/outer sub-groups.
/// </summary>
public static partial class Slv3FanEffects
{
    public const int MaxFans = 4;

    private static readonly IReadOnlyDictionary<Slv3FanFamily, Slv3StrimerEffectInfo[]> Catalogs = BuildCatalogs();
    private static readonly IReadOnlyDictionary<Slv3FanFamily, Dictionary<string, Slv3StrimerEffectInfo>> CatalogIndexes = BuildCatalogIndexes();
    private static readonly IReadOnlyDictionary<Slv3FanFamily, IReadOnlyDictionary<string, Func<FanEffectContext, RawAnimation>>> Renderers = BuildRenderers();

    public static IReadOnlyList<Slv3StrimerEffectInfo> CatalogFor(Slv3FanFamily family) =>
        Catalogs.TryGetValue(family, out var list) ? list : Array.Empty<Slv3StrimerEffectInfo>();

    public static Slv3StrimerEffectInfo? Find(Slv3FanFamily family, string key) =>
        CatalogIndexes.TryGetValue(family, out var index) && index.TryGetValue(key, out var info) ? info : null;

    public static Slv3StrimerAnimation Render(
        Slv3FanFamily family, string key, int fanCount, int speed, int direction, IReadOnlyList<RgbColor> colors)
    {
        if (family == Slv3FanFamily.Unknown)
        {
            throw new ArgumentException("Unknown fan family", nameof(family));
        }
        if (fanCount < 1 || fanCount > MaxFans)
        {
            throw new ArgumentException($"Fan count must be 1..{MaxFans}", nameof(fanCount));
        }
        var info = Find(family, key) ?? throw new ArgumentException($"Unknown fan effect key '{key}' for {family}", nameof(key));
        var renderer = Renderers[family][key];

        var ledsPerFan = Slv3Protocol.LedsPerFanFor(family);
        var ringLen = ledsPerFan / 2;
        var ctx = new FanEffectContext(fanCount, ringLen, NormalizeDirection(direction), ClampSpeed(speed), ResolveColors(colors));
        var raw = renderer(ctx);
        return Slv3WirelessEffectMath.Finalize(raw, fanCount, ledsPerFan);
    }

    private static int NormalizeDirection(int direction) => direction != 0 ? 1 : 0;

    private static int ClampSpeed(int speed) => Math.Clamp(speed, 0, Slv3StrimerEffects.SpeedLevels - 1);

    private static RgbColor[] ResolveColors(IReadOnlyList<RgbColor> colors)
    {
        var resolved = new RgbColor[6];
        for (var i = 0; i < 6; i++)
        {
            resolved[i] = colors.Count > i ? colors[i] : Slv3StrimerEffects.DefaultColors[i];
        }
        return resolved;
    }

    private static Dictionary<Slv3FanFamily, Slv3StrimerEffectInfo[]> BuildCatalogs()
    {
        var map = new Dictionary<Slv3FanFamily, Slv3StrimerEffectInfo[]>();
        var sl = BuildSlCatalog();
        map[Slv3FanFamily.Slv3Led] = sl;
        map[Slv3FanFamily.Slv3Lcd] = sl;
        var tl = BuildTlCatalog();
        map[Slv3FanFamily.Tlv2Led] = tl;
        map[Slv3FanFamily.Tlv2Lcd] = tl;
        var slInf = BuildSlInfCatalog();
        map[Slv3FanFamily.SlInf] = slInf;
        map[Slv3FanFamily.Cl] = slInf;
        return map;
    }

    private static Dictionary<Slv3FanFamily, Dictionary<string, Slv3StrimerEffectInfo>> BuildCatalogIndexes()
    {
        var map = new Dictionary<Slv3FanFamily, Dictionary<string, Slv3StrimerEffectInfo>>();
        foreach (var (family, list) in Catalogs)
        {
            var index = new Dictionary<string, Slv3StrimerEffectInfo>(StringComparer.Ordinal);
            foreach (var info in list)
            {
                index[info.Key] = info;
            }
            map[family] = index;
        }
        return map;
    }

    private static Dictionary<Slv3FanFamily, IReadOnlyDictionary<string, Func<FanEffectContext, RawAnimation>>> BuildRenderers()
    {
        var map = new Dictionary<Slv3FanFamily, IReadOnlyDictionary<string, Func<FanEffectContext, RawAnimation>>>();
        var sl = BuildSlRenderers();
        map[Slv3FanFamily.Slv3Led] = sl;
        map[Slv3FanFamily.Slv3Lcd] = sl;
        var tl = BuildTlRenderers();
        map[Slv3FanFamily.Tlv2Led] = tl;
        map[Slv3FanFamily.Tlv2Lcd] = tl;
        var slInf = BuildSlInfRenderers();
        map[Slv3FanFamily.SlInf] = slInf;
        map[Slv3FanFamily.Cl] = slInf;
        return map;
    }

    /// <summary>Shared per-effect render state: fan-chain geometry, user intent and resolved colours.</summary>
    private readonly struct FanEffectContext
    {
        public FanEffectContext(int fanCount, int ringLen, int direction, int speed, RgbColor[] colors)
        {
            FanCount = fanCount;
            RingLen = ringLen;
            Direction = direction;
            Speed = speed;
            Colors = colors;
        }

        public int FanCount { get; }
        public int RingLen { get; }
        public int Direction { get; }
        public int Speed { get; }
        public RgbColor[] Colors { get; }

        public int LedsPerFan => RingLen * 2;
    }
}
