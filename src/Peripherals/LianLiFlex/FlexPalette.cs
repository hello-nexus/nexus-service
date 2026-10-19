using System.Collections.Generic;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Peripherals.LianLiFlex;

/// <summary>Builds the 4x4x3 color table the generators index, capping each color to the power limit.</summary>
internal static class FlexPalette
{
    public const int PowerCap = 600;

    public static byte[,,] Build(IReadOnlyList<RgbColor> colors)
    {
        var table = new byte[4, 4, 3]
        {
            { { 255, 0, 0 }, { 0, 0, 255 }, { 0, 255, 0 }, { 255, 255, 0 } },
            { { 255, 0, 0 }, { 0, 255, 0 }, { 0, 0, 255 }, { 255, 255, 0 } },
            { { 255, 0, 0 }, { 255, 255, 0 }, { 255, 0, 255 }, { 255, 255, 0 } },
            { { 255, 0, 255 }, { 0, 0, 255 }, { 0, 255, 0 }, { 255, 255, 0 } },
        };
        for (var j = 0; j < colors.Count && j < 4; j++)
        {
            var (r, g, b) = Cap(colors[j].R, colors[j].G, colors[j].B);
            for (var set = 0; set < 4; set++)
            {
                table[set, j, 0] = r;
                table[set, j, 1] = g;
                table[set, j, 2] = b;
            }
        }
        return table;
    }

    public static (byte R, byte G, byte B) Cap(byte r, byte g, byte b)
    {
        while (r + g + b > PowerCap)
        {
            r = (byte)(r * 0.95);
            g = (byte)(g * 0.95);
            b = (byte)(b * 0.95);
        }
        return (r, g, b);
    }

    public static byte[] Flatten(byte[,,] frames, int frameCount, int leds)
    {
        var buf = new byte[frameCount * leds * 3];
        var o = 0;
        for (var f = 0; f < frameCount; f++)
        {
            for (var l = 0; l < leds; l++)
            {
                buf[o++] = frames[f, 0, l];
                buf[o++] = frames[f, 1, l];
                buf[o++] = frames[f, 2, l];
            }
        }
        return buf;
    }
}
