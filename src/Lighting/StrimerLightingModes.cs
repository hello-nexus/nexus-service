using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.Strimer;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

public sealed class StrimerModeInfo
{
    public string   Key          { get; init; } = "";
    public string   Label        { get; init; } = "";
    public byte     EffectByte   { get; init; }
    public bool     HasSpeed     { get; init; }
    public bool     HasDirection { get; init; }
    public bool     HasBrightness{ get; init; }
    /// <summary>Colors the mode plays, in slot order. Every slot is always sent, so min and max are both its length.</summary>
    public string[] DefaultColors { get; init; } = Array.Empty<string>();
    /// <summary>One animation runs across a whole channel, so only its first port is committed.</summary>
    public bool     Complete     { get; init; }
    /// <summary>Speed level (0..4) the mode starts at when the user has not set one; negative falls back to the shared speed.</summary>
    public int      DefaultSpeed { get; init; } = -1;
    public int      ColorsMin    => DefaultColors.Length;
    public int      ColorsMax    => DefaultColors.Length;
}

public static class StrimerLightingModes
{
    // Speed index 0..4 -> firmware byte. Slowest=0x02, fastest=0xFE.
    public static readonly byte[] SpeedCodes = { 0x02, 0x01, 0x00, 0xFF, 0xFE };

    // Brightness index 0..4 -> firmware byte. 0x08=off, 0x00=full (inverted scale).
    public static readonly byte[] BrightnessCodes = { 0x08, 0x03, 0x02, 0x01, 0x00 };

    // Direction wire byte is inverted vs SLI: 0=forward maps to 0x01, 1=reverse to 0x00.
    public static byte DirectionByte(int direction) => direction == 0 ? (byte)0x01 : (byte)0x00;

    /// <summary>An effect with no direction control is committed with the zero direction byte.</summary>
    public static byte DirectionByte(StrimerModeInfo mode, int direction) =>
        mode.HasDirection ? DirectionByte(direction) : (byte)0x00;

    private static readonly string[] None = Array.Empty<string>();
    private static readonly string[] Single = { "#ff0000" };
    private static readonly string[] Pair = { "#ff0000", "#0000ff" };
    private static readonly string[] Six = { "#ff0000", "#ff6900", "#ffd700", "#00ff00", "#0000ff", "#aa00ff" };

    private static StrimerModeInfo M(string key, string label, byte effect, bool speed, bool dir, string[] colors, bool complete = false, int defaultSpeed = 4) =>
        new() { Key = key, Label = label, EffectByte = effect, HasSpeed = speed, HasDirection = dir, HasBrightness = true, DefaultColors = colors, Complete = complete, DefaultSpeed = defaultSpeed };

    public static readonly StrimerModeInfo[] Catalog =
    {
        new() { Key = "custom", Label = "Custom (per-LED effects)", EffectByte = 0x01, HasBrightness = true },
        new() { Key = "off",    Label = "Off",                      EffectByte = 0x00 },
        M("static",        "Static",        0x01, false, false, Single),
        M("breathing",     "Breathing",     0x02, true, false, Single),
        M("flashing",      "Flashing",      0x03, true, false, Single, defaultSpeed: -1),
        M("rainbowMorph",  "Rainbow Morph", 0x04, true, false, None),
        M("rainbow",       "Rainbow",       0x05, true, true,  None),
        M("spectrumCycle", "Spectrum Cycle",0x06, true, false, None, defaultSpeed: -1),
        M("wave",          "Wave",          0x18, true, true,  Six),
        M("snooker",       "Snooker",       0x19, true, false, Six),
        M("mixing",        "Mixing",        0x1A, true, false, Pair, defaultSpeed: 2),
        M("pingPong",      "Ping Pong",     0x1B, true, false, Six),
        M("runway",        "Runway",        0x1C, true, false, Pair),
        M("painting",      "Painting",      0x1D, true, false, Six),
        M("tide",          "Tide",          0x1E, true, false, Six),
        M("blowUp",        "Blow Up",       0x1F, true, false, Six),
        M("meteor",        "Meteor",        0x20, true, true,  Six),
        M("shockWave",     "Shock Wave",    0x21, true, true,  Six, complete: true),
        M("ripple",        "Ripple",        0x22, true, false, Six, complete: true),
        M("voice",         "Voice",         0x23, true, false, Six, complete: true),
        M("bulletStack",   "Bullet Stack",  0x24, true, true,  None, complete: true),
        M("drizzling",     "Drizzling",     0x25, true, true,  Six, complete: true),
        M("fadeOut",       "Fade Out",      0x26, true, false, Six, complete: true),
        M("colorTransfer", "Color Transfer",0x27, true, true,  Six, complete: true),
        M("crossOver",     "Cross Over",    0x28, true, true,  Six, complete: true),
        M("twinkle",       "Twinkle",       0x29, true, false, None, complete: true),
        M("contest",       "Contest",       0x2A, true, true,  Six, complete: true),
        M("parallel",      "Parallel",      0x2B, true, true,  Six, complete: true),
    };

    public static StrimerModeInfo? Find(string key)
    {
        foreach (var m in Catalog)
        {
            if (m.Key == key) return m;
        }
        return null;
    }

    /// <summary>The palette a mode shows and plays: its saved colors, each slot falling back to the mode's default when absent or not a valid color.</summary>
    public static string[] ResolveColors(StrimerLightingSettings ls, StrimerModeInfo mode) =>
        ResolveColors(ls.ModeColors, mode);

    public static string[] ResolveColors(IReadOnlyDictionary<string, List<string>> perMode, StrimerModeInfo mode)
    {
        var defaults = mode.DefaultColors;
        var result = new string[defaults.Length];
        var saved = perMode.TryGetValue(mode.Key, out var own) ? own : null;
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = saved != null && i < saved.Count && IsHexColor(saved[i]) ? saved[i] : defaults[i];
        }
        return result;
    }

    /// <summary>Saved speed level of the mode, else its own default, else the shared speed.</summary>
    public static int ResolveSpeed(IReadOnlyDictionary<string, int> perMode, int shared, StrimerModeInfo mode)
    {
        if (perMode.TryGetValue(mode.Key, out var saved)) return Math.Clamp(saved, 0, 4);
        return mode.DefaultSpeed >= 0 ? mode.DefaultSpeed : Math.Clamp(shared, 0, 4);
    }

    /// <summary>
    /// Flat R,G,B bytes of the color report sent with the mode, empty when the mode takes none.
    /// A single color fills the whole strip, and a mode with no slots gets the default six.
    /// Static scales its color by the brightness level.
    /// </summary>
    public static byte[] FirmwareColors(StrimerModeInfo mode, IReadOnlyList<string> palette, int brightness)
    {
        if (mode.Key is "off" or "custom") return Array.Empty<byte>();
        if (mode.DefaultColors.Length == 0) palette = Six;
        var count = palette.Count == 1 ? StrimerProtocol.MaxLedsPerZone : palette.Count;
        var scale = mode.Key == "static" ? Math.Clamp(brightness, 0, 4) * 25 / 100.0 : 1.0;
        var bytes = new byte[count * 3];
        for (var i = 0; i < count; i++)
        {
            TryParseHex(palette[palette.Count == 1 ? 0 : i], out var rgb);
            bytes[i * 3]     = (byte)Math.Floor(((rgb >> 16) & 0xFF) * scale);
            bytes[i * 3 + 1] = (byte)Math.Floor(((rgb >> 8) & 0xFF) * scale);
            bytes[i * 3 + 2] = (byte)Math.Floor((rgb & 0xFF) * scale);
        }
        return bytes;
    }

    public static bool IsHexColor(string? text) => TryParseHex(text, out _);

    private static bool TryParseHex(string? text, out int rgb)
    {
        rgb = 0;
        if (text is not { Length: 7 } || text[0] != '#') return false;
        return int.TryParse(text.AsSpan(1), System.Globalization.NumberStyles.AllowHexSpecifier, null, out rgb);
    }
}
