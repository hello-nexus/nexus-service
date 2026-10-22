using System;
using Nexus.Service.Peripherals.LianLiFlex;

namespace Nexus.Service.Lighting;

/// <summary>
/// Physical position of every wire LED of a Flex fan, on the fan face as seen from the front with the
/// cable side at the right (u right, v down, both 0..1). Edge strips are the octagon frame: a bar and
/// two diagonals per half, the left and right edges unlit. Each strip starts at the cable side and
/// runs to the far side. TL: LED 0..12 is the lower half (right, bottom bar, left), 13..25 the upper
/// half. SL-INF: inner 8..17 and outer 18..25 are the lower half, inner 26..35 and outer 36..43 the
/// upper half; center 0..7 is the blade ring, LED 0 at the bottom, ascending clockwise.
/// </summary>
internal static class FlexFanLayout
{
    private const float BladeRingRadius = 0.25f;
    private const float P28RingRadius = 0.35f;

    public static (float U, float V) Position(FlexFamily family, int wireIndex, bool swap = false)
    {
        var perFan = FlexProtocol.LedsPerFan(family);
        if (family == FlexFamily.P28V2)
        {
            // Single ring, LED 0 at the bottom, ascending clockwise; the real order is unconfirmed.
            var a = (Math.PI / 2.0) + (wireIndex * 2.0 * Math.PI / perFan);
            return (0.5f + (P28RingRadius * (float)Math.Cos(a)), 0.5f + (P28RingRadius * (float)Math.Sin(a)));
        }
        if (family == FlexFamily.TlFlex)
        {
            var idx = swap ? (wireIndex + (perFan / 2)) % perFan : wireIndex;
            return EdgePosition(idx < 13 ? Lower : Upper, idx % 13, 13, 0.03f);
        }
        if (wireIndex < 8)
        {
            var a = (Math.PI / 2.0) + (wireIndex * Math.PI / 4.0);
            return (0.5f + (BladeRingRadius * (float)Math.Cos(a)), 0.5f + (BladeRingRadius * (float)Math.Sin(a)));
        }
        if (wireIndex < 18)
        {
            return EdgePosition(Lower, wireIndex - 8, 10, InnerInset);
        }
        if (wireIndex < 26)
        {
            return EdgePosition(Lower, wireIndex - 18, 8, OuterInset);
        }
        if (wireIndex < 36)
        {
            return EdgePosition(Upper, wireIndex - 26, 10, InnerInset);
        }
        return EdgePosition(Upper, wireIndex - 36, 8, OuterInset);
    }

    /// <summary>U and V of a zone's LEDs in zone order, per fan.</summary>
    public static (float[] U, float[] V) ZonePositions(FlexFamily family, string zone, int fans, bool swap)
    {
        var map = FlexProtocol.ZoneWireIndexes(family, zone);
        var u = new float[fans * map.Count];
        var v = new float[fans * map.Count];
        for (var f = 0; f < fans; f++)
        {
            for (var k = 0; k < map.Count; k++)
            {
                var (pu, pv) = Position(family, map[k], swap);
                u[(f * map.Count) + k] = (f + pu) / fans;
                v[(f * map.Count) + k] = pv;
            }
        }
        return (u, v);
    }

    private const float InnerInset = 0.06f;
    private const float OuterInset = 0.01f;

    private const int Lower = 1;
    private const int Upper = -1;

    // Strip from the cable-side diagonal across the bar to the far diagonal, LEDs at the centers of
    // equal arc-length bins. side is Lower (+) or Upper (-) of the fan's horizontal midline.
    private static (float U, float V) EdgePosition(int side, int index, int count, float inset)
    {
        const float BarHalf = 0.23f;
        const float DiagEndOffset = 0.21f;
        var reach = 0.5f - inset;
        var pts = new (float X, float Y)[]
        {
            (0.5f + reach, 0.5f + (side * DiagEndOffset)),
            (0.5f + BarHalf, 0.5f + (side * reach)),
            (0.5f - BarHalf, 0.5f + (side * reach)),
            (0.5f - reach, 0.5f + (side * DiagEndOffset)),
        };
        var seg = new float[3];
        var total = 0f;
        for (var i = 0; i < 3; i++)
        {
            seg[i] = MathF.Sqrt(MathF.Pow(pts[i + 1].X - pts[i].X, 2) + MathF.Pow(pts[i + 1].Y - pts[i].Y, 2));
            total += seg[i];
        }
        var d = (index + 0.5f) / count * total;
        var s = 0;
        while (s < 2 && d > seg[s])
        {
            d -= seg[s];
            s++;
        }
        var t = d / seg[s];
        return (pts[s].X + ((pts[s + 1].X - pts[s].X) * t), pts[s].Y + ((pts[s + 1].Y - pts[s].Y) * t));
    }
}
