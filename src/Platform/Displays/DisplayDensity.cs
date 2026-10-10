using System;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Native px/inch of a monitor's current mode from its reported physical
/// size. Null when the size is missing or implausible: EDIDs carry 0 for
/// projectors and an aspect-ratio code or a copied panel's size on cheap
/// screens, and the panel grid falls back to its default density then.
/// </summary>
public static class DisplayDensity
{
    private const double MinDpi = 40;
    private const double MaxDpi = 700;
    private const double MaxAspectMismatch = 0.25;

    /// <summary>
    /// Physical size from the first detailed timing descriptor (mm, bytes
    /// 66-68), else the base block's image size (cm, bytes 21/22).
    /// </summary>
    public static double? FromEdid(ReadOnlySpan<byte> edid, int pixelWidth, int pixelHeight)
    {
        if (edid.Length < 128 || edid[0] != 0x00 || edid[1] != 0xFF) return null;
        // A zero pixel clock marks a display descriptor, not a timing.
        if (edid[54] != 0 || edid[55] != 0)
        {
            var widthMm = edid[66] | ((edid[68] & 0xF0) << 4);
            var heightMm = edid[67] | ((edid[68] & 0x0F) << 8);
            var fromTiming = FromPhysicalMm(widthMm, heightMm, pixelWidth, pixelHeight);
            if (fromTiming is not null) return fromTiming;
        }
        return FromPhysicalMm(edid[21] * 10.0, edid[22] * 10.0, pixelWidth, pixelHeight);
    }

    /// <summary>
    /// Diagonal over diagonal, so a rotated mode against the panel's
    /// unrotated size gives the same density.
    /// </summary>
    public static double? FromPhysicalMm(double widthMm, double heightMm, int pixelWidth, int pixelHeight)
    {
        if (widthMm <= 0 || heightMm <= 0 || pixelWidth <= 0 || pixelHeight <= 0) return null;
        var physicalAspect = Math.Max(widthMm, heightMm) / Math.Min(widthMm, heightMm);
        var pixelAspect = (double)Math.Max(pixelWidth, pixelHeight) / Math.Min(pixelWidth, pixelHeight);
        if (Math.Abs(pixelAspect / physicalAspect - 1) > MaxAspectMismatch) return null;
        var dpi = Math.Sqrt((double)pixelWidth * pixelWidth + (double)pixelHeight * pixelHeight)
            / (Math.Sqrt(widthMm * widthMm + heightMm * heightMm) / 25.4);
        return dpi is >= MinDpi and <= MaxDpi ? Math.Round(dpi, 1) : null;
    }
}
