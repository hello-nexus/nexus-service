using System;
using System.Globalization;
using Nexus.Service.Peripherals.JpegPanels;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

public sealed class Galahad2LcdRingEffect
{
    public string Id { get; init; } = "";
    public byte WireByte { get; init; }
    public int Colors { get; init; }
}

/// <summary>
/// Pump-ring firmware animations. Mode bytes follow sgtaziz/lian-li-linux
/// `to_hydroshift_lcd_mode_byte`; the colour slot counts are those L-Connect sent in a
/// USB capture, so a mode that capture never showed is not listed.
/// </summary>
public static class Galahad2LcdRingEffects
{
    /// <summary>Top step of the firmware's speed and brightness scales; the bottom step is zero.</summary>
    public const int MaxLevel = 4;

    public const int DefaultSpeed = 2;

    public static readonly Galahad2LcdRingEffect[] All =
    {
        new() { Id = "rainbowMorph", WireByte = 0x02, Colors = 0 },
        new() { Id = "breathing", WireByte = 0x04, Colors = 1 },
        new() { Id = "runway", WireByte = 0x05, Colors = 2 },
        new() { Id = "meteor", WireByte = 0x06, Colors = 4 },
    };

    public static Galahad2LcdRingEffect? Find(string? id)
    {
        foreach (var e in All)
        {
            if (e.Id == id)
            {
                return e;
            }
        }
        return null;
    }

    /// <summary>Packs "#rrggbb" entries into RGB triples; false when any entry is malformed.</summary>
    public static bool TryPackColors(string[] hex, out byte[] rgb)
    {
        rgb = new byte[hex.Length * 3];
        for (var i = 0; i < hex.Length; i++)
        {
            var text = hex[i]?.Trim() ?? "";
            if (text.StartsWith('#'))
            {
                text = text[1..];
            }
            if (text.Length != 6
                || !byte.TryParse(text.AsSpan(0, 2), NumberStyles.HexNumber, null, out rgb[i * 3])
                || !byte.TryParse(text.AsSpan(2, 2), NumberStyles.HexNumber, null, out rgb[i * 3 + 1])
                || !byte.TryParse(text.AsSpan(4, 2), NumberStyles.HexNumber, null, out rgb[i * 3 + 2]))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Writes a saved effect to the ring; false when none is saved, it no longer parses, or the write fails.</summary>
    public static bool Apply(Galahad2LcdAio aio, Galahad2LcdRingEffectSettings saved)
    {
        var effect = Find(saved.Effect);
        if (effect is null || saved.Colors.Count != effect.Colors || !TryPackColors(saved.Colors.ToArray(), out var rgb))
        {
            return false;
        }
        return aio.SetRingEffect(
            effect.WireByte,
            (byte)Math.Clamp(saved.Brightness, 0, MaxLevel),
            (byte)Math.Clamp(saved.Speed, 0, MaxLevel),
            rgb);
    }
}
