using System;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Peripherals.LianLiWireless;

public static partial class Slv3StrimerEffects
{
    private static byte[] AllocateFrames(int frameCount, int lanes, int ledsPerLane) =>
        Slv3WirelessEffectMath.AllocateFrames(frameCount, lanes, ledsPerLane);

    private static void SetLed(byte[] buf, int frame, int lanes, int ledsPerLane, int lane, int led, RgbColor c) =>
        Slv3WirelessEffectMath.SetLed(buf, frame, lanes, ledsPerLane, lane, led, c);

    private static int Pos(int index, int count, int direction) => Slv3WirelessEffectMath.Pos(index, count, direction);

    private static RgbColor Scale(RgbColor c, double factor) => Slv3WirelessEffectMath.Scale(c, factor);

    private static RgbColor Lerp(RgbColor a, RgbColor b, double t) => Slv3WirelessEffectMath.Lerp(a, b, t);

    private static RgbColor Hue(double hue01) => Slv3WirelessEffectMath.Hue(hue01);

    private static double Breath(double t) => Slv3WirelessEffectMath.Breath(t);

    private static double BounceTravel(double phase) => Slv3WirelessEffectMath.BounceTravel(phase);

    private static void PaintMarker(
        byte[] buf, int frame, EffectContext ctx, int lane, double centerPos, double trailLen, RgbColor color, int direction) =>
        Slv3WirelessEffectMath.PaintMarker(buf, frame, ctx.Lanes, ctx.LedsPerLane, lane, centerPos, trailLen, color, direction);

    private static RawAnimation Loop(int frameCount, int lanes, int ledsPerLane, double intervalMs, Action<byte[], int> paint) =>
        Slv3WirelessEffectMath.Loop(frameCount, lanes, ledsPerLane, intervalMs, paint);

    private static Slv3StrimerAnimation Finalize(RawAnimation raw, int lanes, int ledsPerLane) =>
        Slv3WirelessEffectMath.Finalize(raw, lanes, ledsPerLane);

    private static bool TryCompress(ReadOnlySpan<byte> data, out int compressedLength) =>
        Slv3WirelessEffectMath.TryCompress(data, out compressedLength);
}
