using System;

namespace Nexus.Service.Peripherals.SteelSeries;

/// <summary>
/// Turns a rendered BGRA panel frame into the lit/unlit pixels of the 1-bit OLED. A fixed luma
/// threshold, not a dither: at 128x40 the panel's text and numbers are the content, and a
/// dither speckles anti-aliased glyph edges where a threshold keeps them crisp.
/// </summary>
public static class ApexOledMono
{
    public const int FrameLength = ApexOledProtocol.Width * ApexOledProtocol.Height * 4;

    /// <summary>Below the mid-point so dim secondary text on a dark panel still lights.</summary>
    public const int LumaThreshold = 96;

    /// <summary>Fills <paramref name="lit"/> from a BGRA frame; false when the frame is not exactly the OLED's size.</summary>
    public static bool Convert(ReadOnlySpan<byte> bgra, Span<bool> lit)
    {
        if (bgra.Length != FrameLength || lit.Length != ApexOledProtocol.Width * ApexOledProtocol.Height)
        {
            return false;
        }
        for (var i = 0; i < lit.Length; i++)
        {
            var p = i * 4;
            // Integer Rec.601 luma, weights summing to 256.
            var luma = (bgra[p] * 29 + bgra[p + 1] * 150 + bgra[p + 2] * 77) >> 8;
            lit[i] = luma >= LumaThreshold;
        }
        return true;
    }
}
