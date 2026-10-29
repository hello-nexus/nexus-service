using System;
using System.Collections.Generic;
using System.Globalization;
using Nexus.Service.Persistence;

namespace Nexus.Service.Peripherals.AsiahorseUmbra;

public sealed class UmbraEffectInfo
{
    public UmbraEffectInfo(string key, int syncId, int mergeId, int colors, bool speed, bool direction)
    {
        Key = key;
        SyncId = syncId;
        MergeId = mergeId;
        Colors = colors;
        HasSpeed = speed;
        HasDirection = direction;
    }

    public string Key { get; }
    public int SyncId { get; }
    // -1 when the effect has no merge-group form.
    public int MergeId { get; }
    public int Colors { get; }
    public bool HasSpeed { get; }
    public bool HasDirection { get; }
    public bool InMerge => MergeId >= 0;
}

public static class UmbraEffects
{
    public const string GroupSync = "sync";
    public const string GroupAsync = "async";
    public const string GroupMerge = "merge";
    public const string DirectionForward = "forward";
    public const string DirectionReverse = "reverse";
    public const int SpeedMax = 5;
    public const int BrightnessMin = 1;
    public const int BrightnessMax = 5;

    public static readonly UmbraEffectInfo[] Catalog =
    {
        new("static", 0, -1, 1, false, false),
        new("rainbow", 1, 0, 0, true, true),
        new("gradientRainbow", 2, -1, 0, true, false),
        new("breath", 3, -1, 4, true, false),
        new("breathRainbow", 4, -1, 0, true, false),
        new("runway", 5, 1, 1, true, true),
        new("tile", 6, 2, 4, true, true),
        new("fadeOut", 7, 3, 1, true, true),
        new("trail", 8, 4, 4, true, true),
        new("scan", 9, 5, 2, true, false),
        new("brick", 10, 6, 1, true, true),
        new("disco", 11, -1, 4, true, true),
        new("pulse", 12, 7, 4, true, false),
        new("flash", 13, 8, 2, true, false),
        new("address", 14, 9, 1, true, true),
    };

    private static readonly string[] DefaultColors = { "#ff0000", "#00ff00", "#0000ff", "#ffff00" };

    public static UmbraEffectInfo? Find(string? key)
    {
        if (key is null)
        {
            return null;
        }
        foreach (var e in Catalog)
        {
            if (e.Key == key)
            {
                return e;
            }
        }
        return null;
    }

    public static bool IsGroup(string? group) =>
        group is GroupSync or GroupAsync or GroupMerge;

    public static bool AllowedIn(string group, UmbraEffectInfo effect) =>
        group != GroupMerge || effect.InMerge;

    public static UmbraEffectValues Defaults(string group, UmbraEffectInfo effect)
    {
        var colors = new List<string>(DefaultColors);
        if (effect.Key == "static" && group == GroupSync)
        {
            colors = new List<string> { DefaultColors[0], DefaultColors[0], DefaultColors[0], DefaultColors[0] };
        }
        return new UmbraEffectValues
        {
            Speed = group == GroupMerge ? 4 : 3,
            // Only the vendor's sync table brightens Gradient Rainbow and reverses Fade Out.
            Brightness = effect.Key == "gradientRainbow" && group == GroupSync ? 5 : 3,
            Direction = effect.Key == "fadeOut" && group == GroupSync ? DirectionReverse : DirectionForward,
            Colors = colors,
        };
    }

    public static bool TryParseColor(string? hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (hex is null || hex.Length != 7 || hex[0] != '#')
        {
            return false;
        }
        return byte.TryParse(hex.AsSpan(1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out r)
            && byte.TryParse(hex.AsSpan(3, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out g)
            && byte.TryParse(hex.AsSpan(5, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out b);
    }

    private const int LevelStep = 51;
    private const int BrightnessFloor = 50;

    public static byte BrightnessByte(int level) => (byte)(Math.Clamp(level, BrightnessMin, BrightnessMax) * LevelStep);

    public static byte SpeedByte(int speed) => (byte)(6 - Math.Clamp(speed, 0, SpeedMax));

    // The current values of a group's selected effect, or the vendor defaults when none are stored.
    public static UmbraEffectValues Resolve(string group, UmbraEffectGroupSettings state, UmbraEffectInfo effect)
    {
        if (state.Values is not null && state.Values.TryGetValue(effect.Key, out var stored) && stored is not null)
        {
            return Normalized(stored, group, effect);
        }
        return Defaults(group, effect);
    }

    // Clamps stored values so a hand-edited settings file cannot put an out-of-range byte on the wire.
    public static UmbraEffectValues Normalized(UmbraEffectValues v, string group, UmbraEffectInfo effect)
    {
        var fallback = Defaults(group, effect);
        var colors = new List<string>(ColorSlots);
        for (var i = 0; i < ColorSlots; i++)
        {
            var c = v.Colors is not null && i < v.Colors.Count ? v.Colors[i] : fallback.Colors[i];
            colors.Add(TryParseColor(c, out _, out _, out _) ? c.ToLowerInvariant() : fallback.Colors[i]);
        }
        return new UmbraEffectValues
        {
            Speed = Math.Clamp(v.Speed, 0, SpeedMax),
            Brightness = Math.Clamp(v.Brightness, BrightnessMin, BrightnessMax),
            Direction = v.Direction == DirectionReverse ? DirectionReverse : DirectionForward,
            Colors = colors,
        };
    }

    private const int ColorSlots = UmbraProtocol.ColorSlots;

    public static byte ScaleBrightness(int level, float master)
    {
        var cap = (int)Math.Round(Math.Clamp(master, 0f, 1f) * 255f);
        var nearest = (int)Math.Round(cap / (double)LevelStep) * LevelStep;
        var quantized = nearest >= LevelStep ? nearest : BrightnessFloor;
        return (byte)Math.Min((int)BrightnessByte(level), quantized);
    }

    internal static UmbraEffectBlock ToBlock(string group, UmbraEffectInfo effect, UmbraEffectValues values, float master, string? order = null)
    {
        var colors = new byte[ColorSlots * 3];
        for (var i = 0; i < ColorSlots; i++)
        {
            // Sync Static plays color 1 in every slot; the vendor app sends async Static colors as stored.
            var src = effect.Key == "static" && group == GroupSync ? values.Colors[0] : values.Colors[i];
            TryParseColor(src, out colors[i * 3], out colors[i * 3 + 1], out colors[i * 3 + 2]);
            Nexus.Service.Lighting.DeviceColorAdjust.ReorderChannels(order, ref colors[i * 3], ref colors[i * 3 + 1], ref colors[i * 3 + 2]);
        }
        var id = group == GroupMerge ? effect.MergeId : effect.SyncId;
        return new UmbraEffectBlock(
            (byte)id,
            SpeedByte(values.Speed),
            values.Direction == DirectionReverse ? (byte)0 : (byte)1,
            ScaleBrightness(values.Brightness, master),
            colors);
    }
}
