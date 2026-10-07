using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// The HydroShift II pump ring's built-in effects. The firmware has no effect modes of its
/// own: L-Connect renders each one into a frame loop that the pump head then plays unattended,
/// so these reproduce L-Connect's frames byte for byte (ported from lian-li-linux's
/// h2 renderers, MIT, which pins them with hashes recovered from L-Connect).
/// </summary>
public static class HydroShift2RingEffects
{
    public const string Off = "off";
    public const string Rainbow = "rainbow";
    public const string RainbowMorph = "rainbowMorph";
    public const string Static = "static";
    public const string Breathing = "breathing";
    public const string Runway = "runway";
    public const string Meteor = "meteor";
    public const string TaiChi = "taichi";
    public const string Twinkle = "twinkle";
    public const string Voice = "voice";
    public const string Pump = "pump";
    public const string Bounce = "bounce";

    public static readonly IReadOnlyList<string> Modes = new[]
    {
        Rainbow, RainbowMorph, Static, Breathing, Runway, Meteor, TaiChi, Twinkle, Voice, Pump, Bounce, Off,
    };

    /// <summary>Colours each mode reads from the palette; the rest of the palette is ignored.</summary>
    public static int ColorCount(string mode) => mode switch
    {
        Static or Breathing or Voice or Pump => 1,
        Runway or TaiChi => 2,
        Meteor or Bounce => 4,
        _ => 0,
    };

    /// <summary>Modes whose sweep direction is selectable.</summary>
    public static bool HasDirection(string mode) => mode is Rainbow or Meteor or TaiChi or Voice or Pump;

    public const int MaxLevel = 4;

    private const int Leds = HydroShift2Protocol.RingLedCount;

    private static readonly byte[] BrightnessLevels = { 0, 64, 128, 192, 255 };
    private static readonly int[] SpeedMultipliers = { 7, 6, 5, 4, 3 };

    /// <summary>L-Connect's palette when none is set, in mode order of use.</summary>
    private static readonly Rgb[] DefaultPalette = { new(255, 0, 0), new(0, 0, 255), new(0, 255, 0), new(255, 255, 0) };

    private readonly record struct Rgb(byte R, byte G, byte B);

    /// <summary>
    /// The frames for one effect, each <see cref="HydroShift2Protocol.RingLedCount"/> packed RGB
    /// triples, and the per-frame interval in firmware ticks (0.625 ms).
    /// </summary>
    public static (List<byte[]> Frames, byte IntervalTicks) Render(
        string mode, IReadOnlyList<(byte R, byte G, byte B)> colors, int brightness, int speed, bool reverse)
    {
        var level = BrightnessLevels[Math.Clamp(brightness, 0, MaxLevel)];
        var palette = Palette(colors);
        var (frames, baseHundredths) = mode switch
        {
            Rainbow => (RenderRainbow(level, reverse), 2000),
            RainbowMorph => (RenderMorph(level), 1100),
            Static => (Repeat(Scale(palette[0], level), 30), 2000),
            Breathing => (RenderBreathing(palette[0], level), 1100),
            Runway => (RenderRunway(palette, level), 2000),
            Meteor => (RenderMeteor(palette, level, reverse), 2000),
            TaiChi => (RenderTaiChi(palette, level, reverse), 2000),
            Twinkle => (RenderTwinkle(level), 2000),
            Voice => (RenderVoice(palette[0], level, reverse), 2000),
            Pump => (RenderPump(palette[0], level, reverse), 2000),
            Bounce => (RenderBounce(palette, level), 2000),
            _ => (new List<Rgb[]> { new Rgb[Leds] }, 2000),
        };
        var ticks = baseHundredths * SpeedMultipliers[Math.Clamp(speed, 0, MaxLevel)] / 100;
        var packed = new List<byte[]>(frames.Count);
        foreach (var frame in frames)
        {
            var bytes = new byte[Leds * 3];
            for (int i = 0; i < Leds; i++)
            {
                bytes[i * 3] = frame[i].R;
                bytes[(i * 3) + 1] = frame[i].G;
                bytes[(i * 3) + 2] = frame[i].B;
            }
            packed.Add(bytes);
        }
        return (packed, (byte)ticks);
    }

    /// <summary>The user's colours over L-Connect's defaults, each held under the ring's per-LED current limit; an all-black palette counts as unset.</summary>
    private static Rgb[] Palette(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        var palette = (Rgb[])DefaultPalette.Clone();
        bool anySet = false;
        foreach (var c in colors)
        {
            anySet |= c.R != 0 || c.G != 0 || c.B != 0;
        }
        if (anySet)
        {
            for (int i = 0; i < palette.Length && i < colors.Count; i++)
            {
                palette[i] = LimitCurrent(new Rgb(colors[i].R, colors[i].G, colors[i].B));
            }
        }
        return palette;
    }

    /// <summary>Dims a colour until it fits L-Connect's per-LED current limit.</summary>
    private static Rgb LimitCurrent(Rgb c)
    {
        while (c.R + c.G + c.B > 570)
        {
            c = new Rgb((byte)(c.R * 0.95), (byte)(c.G * 0.95), (byte)(c.B * 0.95));
        }
        return c;
    }

    private static Rgb Scale(Rgb c, byte factor) =>
        new((byte)((c.R * factor) >> 8), (byte)((c.G * factor) >> 8), (byte)((c.B * factor) >> 8));

    private static List<Rgb[]> Repeat(Rgb c, int count)
    {
        var frames = new List<Rgb[]>(count);
        for (int f = 0; f < count; f++)
        {
            var frame = new Rgb[Leds];
            Array.Fill(frame, c);
            frames.Add(frame);
        }
        return frames;
    }

    private static List<Rgb[]> RenderRainbow(byte level, bool reverse)
    {
        Rgb[] colors =
        {
            new(255, 0, 0), new(224, 32, 0), new(192, 64, 0), new(160, 96, 0), new(128, 128, 0), new(96, 160, 0),
            new(64, 192, 0), new(32, 224, 0), new(0, 255, 0), new(0, 224, 32), new(0, 192, 64), new(0, 160, 96),
            new(0, 128, 128), new(0, 96, 160), new(0, 64, 192), new(0, 32, 224), new(0, 0, 255), new(32, 0, 224),
            new(64, 0, 192), new(96, 0, 160), new(128, 0, 128), new(160, 0, 96), new(192, 0, 64), new(224, 0, 32),
        };
        var frames = new List<Rgb[]>(Leds);
        for (int shift = 0; shift < Leds; shift++)
        {
            var frame = new Rgb[Leds];
            for (int position = 0; position < Leds; position++)
            {
                var destination = reverse ? position : Leds - 1 - position;
                frame[destination] = Scale(colors[(shift + position) % Leds], level);
            }
            frames.Add(frame);
        }
        return frames;
    }

    private static List<Rgb[]> RenderMorph(byte level)
    {
        int red = 255, green = 0, blue = 0;
        var source = new List<Rgb>(255);
        for (int index = 0; index < 255; index++)
        {
            source.Add(Scale(new Rgb((byte)red, (byte)green, (byte)blue), level));
            if (index < 85) { red -= 3; green += 3; blue = 0; }
            else if (index < 170) { red = 0; green -= 3; blue += 3; }
            else { red += 3; green = 0; blue -= 3; }
        }
        var frames = new List<Rgb[]>(127);
        for (int index = 0; index < 127; index++)
        {
            var frame = new Rgb[Leds];
            Array.Fill(frame, source[index * 2]);
            frames.Add(frame);
        }
        return frames;
    }

    private static List<Rgb[]> RenderBreathing(Rgb color, byte level)
    {
        var frames = new List<Rgb[]>(170);
        int phase = 0;
        for (int index = 0; index < 170; index++)
        {
            var intensity = (byte)((phase * 3) & 0xFF);
            phase += index >= 85 ? -1 : 1;
            var frame = new Rgb[Leds];
            Array.Fill(frame, Scale(Scale(color, intensity), level));
            frames.Add(frame);
        }
        return frames;
    }

    private static List<Rgb[]> RenderRunway(Rgb[] colors, byte level)
    {
        var frames = new List<Rgb[]>(54);
        foreach (var reverse in new[] { false, true })
        {
            for (int step = 0; step < 27; step++)
            {
                var frame = new Rgb[Leds];
                for (int position = 0; position < Leds; position++)
                {
                    var color = position <= step && position + 3 > step ? colors[0] : colors[1];
                    frame[reverse ? Leds - 1 - position : position] = Scale(color, level);
                }
                frames.Add(frame);
            }
        }
        return frames;
    }

    private static List<Rgb[]> RenderMeteor(Rgb[] colors, byte level, bool reverse)
    {
        byte[] tail = { 6, 8, 16, 24, 32, 48, 64, 96, 120, 150, 200, 255 };
        var source = new List<Rgb[]>(144);
        foreach (var color in colors)
        {
            for (int step = 0; step < 36; step++)
            {
                var frame = new Rgb[Leds];
                int tailIndex = 0;
                for (int position = 0; position < Leds; position++)
                {
                    byte intensity = position <= step && position + 12 > step ? tail[tailIndex++] : (byte)0;
                    frame[reverse ? Leds - 1 - position : position] = Scale(Scale(color, intensity), level);
                }
                source.Add(frame);
            }
        }
        var frames = new List<Rgb[]>(60);
        for (int index = 0; index < 60; index++)
        {
            frames.Add(source[(int)(index * 2.4)]);
        }
        return frames;
    }

    private static List<Rgb[]> RenderTaiChi(Rgb[] colors, byte level, bool reverse)
    {
        byte[] intensity = { 255, 230, 205, 180, 155, 130, 105, 80, 55, 30, 20, 10, 255, 230, 205, 180, 155, 130, 105, 80, 55, 30, 20, 10 };
        var frames = new List<Rgb[]>(Leds);
        for (int shift = 0; shift < Leds; shift++)
        {
            var frame = new Rgb[Leds];
            for (int position = 0; position < Leds; position++)
            {
                int source = (shift + position) % Leds;
                var destination = reverse ? position : Leds - 1 - position;
                frame[destination] = Scale(Scale(colors[source >= 12 ? 1 : 0], intensity[source]), level);
            }
            frames.Add(frame);
        }
        return frames;
    }

    private static List<Rgb[]> RenderTwinkle(byte level)
    {
        Rgb[] colors = { new(255, 0, 0), new(0, 255, 0), new(0, 0, 255), new(255, 255, 0), new(0, 255, 255), new(255, 0, 255), new(255, 255, 255) };
        int[] colorIndex = { 1, 0, 6, 3, 2, 4, 1, 0, 2, 5, 0, 3, 1, 6, 4, 2, 0, 5, 1, 2, 3, 6, 4, 5 };
        byte[] pulse = { 5, 30, 55, 80, 105, 130, 160, 190, 220, 255, 220, 190, 160, 130, 105, 80, 55, 30, 5 };
        (int Led, int Start)[] starts =
        {
            (0, 54), (0, 129), (1, 14), (1, 91), (1, 176), (2, 115), (3, 64), (3, 155), (4, -3), (4, 197),
            (6, 48), (7, 4), (7, 84), (8, 121), (10, 160), (11, 11), (11, 109), (12, 55), (13, -9), (13, 134),
            (13, 191), (14, 32), (17, 154), (19, 66), (20, 29), (20, 94), (21, 0), (22, 116), (23, 54), (23, 179),
        };
        var frames = new List<Rgb[]>(200);
        for (int f = 0; f < 200; f++)
        {
            frames.Add(new Rgb[Leds]);
        }
        foreach (var (led, start) in starts)
        {
            for (int offset = 0; offset < pulse.Length; offset++)
            {
                int index = start + offset;
                if (index >= 0 && index < 200)
                {
                    frames[index][led] = Scale(Scale(colors[colorIndex[led]], pulse[offset]), level);
                }
            }
        }
        frames[64][9] = Scale(Scale(colors[colorIndex[9]], 5), level);
        return frames;
    }

    private static List<Rgb[]> RenderVoice(Rgb color, byte level, bool reverse)
    {
        var lit = Scale(color, level);
        var frames = new List<Rgb[]>(48);
        foreach (var extent in new[] { 12, 8, 4 })
        {
            foreach (var shrink in new[] { false, true })
            {
                for (int step = 0; step < extent; step++)
                {
                    var frame = new Rgb[Leds];
                    for (int position = 0; position < 12; position++)
                    {
                        bool dark = shrink ? position > extent - step : position > step;
                        frame[reverse ? 11 - position : position] = dark ? default : lit;
                    }
                    MirrorHalf(frame);
                    frames.Add(frame);
                }
            }
        }
        return frames;
    }

    private static List<Rgb[]> RenderPump(Rgb color, byte level, bool reverse)
    {
        var frames = new List<Rgb[]>(20);
        for (int step = 0; step < 20; step++)
        {
            var frame = new Rgb[Leds];
            TailHalf(frame, color, level, step, reverse);
            MirrorHalf(frame);
            frames.Add(frame);
        }
        return frames;
    }

    private static List<Rgb[]> RenderBounce(Rgb[] colors, byte level)
    {
        var frames = new List<Rgb[]>(80);
        for (int colorIndex = 0; colorIndex < colors.Length; colorIndex++)
        {
            for (int step = 0; step < 20; step++)
            {
                var frame = new Rgb[Leds];
                TailHalf(frame, colors[colorIndex], level, step, colorIndex % 2 == 1);
                MirrorHalf(frame);
                frames.Add(frame);
            }
        }
        return frames;
    }

    /// <summary>A fading tail stepping along the first half of the ring.</summary>
    private static void TailHalf(Rgb[] frame, Rgb color, byte level, int step, bool reverse)
    {
        byte[] tail = { 16, 24, 32, 64, 96, 128, 196, 255 };
        int tailIndex = 0;
        for (int position = 0; position < 12; position++)
        {
            byte intensity = position <= step && position + 8 > step ? tail[tailIndex++] : (byte)0;
            frame[reverse ? 11 - position : position] = Scale(Scale(color, intensity), level);
        }
    }

    /// <summary>The second half of the ring mirrors the first.</summary>
    private static void MirrorHalf(Rgb[] frame)
    {
        for (int position = 0; position < 12; position++)
        {
            frame[position + 12] = frame[11 - position];
        }
    }
}
