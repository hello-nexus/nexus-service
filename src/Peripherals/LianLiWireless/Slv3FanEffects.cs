using System;
using System.Collections.Generic;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// Which part of each TL fan an effect lights. All (the default) shows the same content on
/// the two halves; Top and Bottom keep it to the first or the second 13 LEDs and leave the
/// other half dark, the way L-Connect lets a TL effect run on one half only ("up" and "down").
/// </summary>
public enum Slv3FanScope
{
    All,
    Top,
    Bottom,
}

/// <summary>
/// Renders looping animations for a wireless fan chain (up to <see cref="MaxFans"/> fans
/// behind one RF record), the same way <see cref="Slv3StrimerEffects"/> does for a
/// Strimer cable: the firmware only plays back an uploaded frame set, so every
/// mode's motion, colour cycling and timing is baked into the buffer up front.
/// A fan's two wire halves show the same content, unless a <see cref="Slv3FanScope"/>
/// keeps the effect to one of them.
/// </summary>
public static partial class Slv3FanEffects
{
    public const int MaxFans = 4;

    private static readonly IReadOnlyDictionary<Slv3FanFamily, Slv3StrimerEffectInfo[]> Catalogs = BuildCatalogs();
    private static readonly IReadOnlyDictionary<Slv3FanFamily, Dictionary<string, Slv3StrimerEffectInfo>> CatalogIndexes = BuildCatalogIndexes();
    private static readonly IReadOnlyDictionary<Slv3FanFamily, IReadOnlyDictionary<string, Func<FanEffectContext, RawAnimation>>> Renderers = BuildRenderers();

    // Keyed like the catalogs; every entry marked Mergeable must have one.
    private static readonly IReadOnlyDictionary<string, Func<FanEffectContext, RawAnimation>> MergedRenderers =
        new Dictionary<string, Func<FanEffectContext, RawAnimation>>(StringComparer.Ordinal)
        {
            ["runway"] = SlMergedRunway,
            ["tide"] = SlMergedTide,
        };

    public static IReadOnlyList<Slv3StrimerEffectInfo> CatalogFor(Slv3FanFamily family) =>
        Catalogs.TryGetValue(family, out var list) ? list : Array.Empty<Slv3StrimerEffectInfo>();

    public static Slv3StrimerEffectInfo? Find(Slv3FanFamily family, string key) =>
        CatalogIndexes.TryGetValue(family, out var index) && index.TryGetValue(key, out var info) ? info : null;

    /// <summary>
    /// Renders <paramref name="key"/>; with <paramref name="merge"/> a mergeable effect spans the whole chain,
    /// and <paramref name="scope"/> keeps it to one half of each fan.
    /// </summary>
    public static Slv3StrimerAnimation Render(
        Slv3FanFamily family, string key, int fanCount, int speed, int direction, IReadOnlyList<RgbColor> colors,
        bool merge = false, Slv3FanScope scope = Slv3FanScope.All)
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
        var renderer = merge && info.Mergeable ? MergedRenderers[key] : Renderers[family][key];

        var ledsPerFan = Slv3Protocol.LedsPerFanFor(family);
        var ringLen = ledsPerFan / 2;
        var ctx = new FanEffectContext(
            fanCount, ringLen, NormalizeDirection(direction), ClampSpeed(speed), ResolveColors(colors), scope, Math.Min(colors.Count, 6));
        var raw = renderer(ctx);
        if (scope != Slv3FanScope.All)
        {
            DarkenOtherHalf(raw, fanCount, ledsPerFan, scope);
        }
        return Slv3WirelessEffectMath.Finalize(raw, fanCount, ledsPerFan);
    }

    /// <summary>"top" and "bottom" as stored in the settings; anything else is the whole fan.</summary>
    public static Slv3FanScope ParseScope(string? scope) => scope switch
    {
        "top" => Slv3FanScope.Top,
        "bottom" => Slv3FanScope.Bottom,
        _ => Slv3FanScope.All,
    };

    private static void DarkenOtherHalf(RawAnimation raw, int fanCount, int ledsPerFan, Slv3FanScope scope)
    {
        var half = ledsPerFan / 2;
        var first = scope == Slv3FanScope.Top ? half : 0;
        var count = scope == Slv3FanScope.Top ? ledsPerFan - half : half;
        for (var f = 0; f < raw.FrameCount; f++)
        {
            for (var fan = 0; fan < fanCount; fan++)
            {
                var offset = ((f * fanCount + fan) * ledsPerFan + first) * 3;
                Array.Clear(raw.Frames, offset, count * 3);
            }
        }
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
        map[Slv3FanFamily.TlFlex] = tl;
        var slInf = BuildSlInfCatalog();
        map[Slv3FanFamily.SlInf] = slInf;
        map[Slv3FanFamily.SlInfFlex] = slInf;
        map[Slv3FanFamily.Cl] = slInf;
        map[Slv3FanFamily.ClV2] = slInf;
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
        map[Slv3FanFamily.TlFlex] = tl;
        var slInf = BuildSlInfRenderers();
        map[Slv3FanFamily.SlInf] = slInf;
        map[Slv3FanFamily.SlInfFlex] = slInf;
        map[Slv3FanFamily.Cl] = slInf;
        map[Slv3FanFamily.ClV2] = slInf;
        return map;
    }

    /// <summary>Shared per-effect render state: fan-chain geometry, user intent and resolved colours.</summary>
    private readonly struct FanEffectContext
    {
        public FanEffectContext(int fanCount, int ringLen, int direction, int speed, RgbColor[] colors, Slv3FanScope scope, int colorCount)
        {
            FanCount = fanCount;
            RingLen = ringLen;
            Direction = direction;
            Speed = speed;
            Colors = colors;
            Scope = scope;
            ColorCount = colorCount;
        }

        public int FanCount { get; }
        public int RingLen { get; }
        public int Direction { get; }
        public int Speed { get; }
        public RgbColor[] Colors { get; }
        public Slv3FanScope Scope { get; }
        /// <summary>How many colours the user chose (0 to 6); <see cref="Colors"/> is always padded with the defaults.</summary>
        public int ColorCount { get; }

        public int LedsPerFan => RingLen * 2;
    }
}
