using System;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Peripherals.LianLiWireless;

public static partial class Slv3StrimerEffects
{
    private static byte[] AllocateFrames(int frameCount, int lanes, int ledsPerLane) =>
        new byte[frameCount * lanes * ledsPerLane * 3];

    private static void SetLed(byte[] buf, int frame, int lanes, int ledsPerLane, int lane, int led, RgbColor c)
    {
        var stride = lanes * ledsPerLane * 3;
        var offset = frame * stride + (lane * ledsPerLane + led) * 3;
        buf[offset] = c.R;
        buf[offset + 1] = c.G;
        buf[offset + 2] = c.B;
    }

    /// <summary>Maps a logical position (0 = the wire's first LED) to the buffer index, honoring the scan direction.</summary>
    private static int Pos(int index, int count, int direction) => direction != 0 ? count - 1 - index : index;

    private static byte ScaleChannel(byte value, double factor) => (byte)Math.Clamp(value * factor, 0, 255);

    private static RgbColor Scale(RgbColor c, double factor) =>
        new(ScaleChannel(c.R, factor), ScaleChannel(c.G, factor), ScaleChannel(c.B, factor));

    private static RgbColor Lerp(RgbColor a, RgbColor b, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return new RgbColor(
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    /// <summary>Full-saturation HSV-to-RGB with a wrapping hue in any real range.</summary>
    private static RgbColor Hue(double hue01)
    {
        var h = hue01 - Math.Floor(hue01);
        var sector = h * 6.0;
        var i = (int)sector;
        var f = sector - i;
        double r, g, b;
        switch (i % 6)
        {
            case 0: r = 1; g = f; b = 0; break;
            case 1: r = 1 - f; g = 1; b = 0; break;
            case 2: r = 0; g = 1; b = f; break;
            case 3: r = 0; g = 1 - f; b = 1; break;
            case 4: r = f; g = 0; b = 1; break;
            default: r = 1; g = 0; b = 1 - f; break;
        }
        return new RgbColor((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    /// <summary>Sine-shaped 0..1 envelope, period 1.0 over <paramref name="t"/>.</summary>
    private static double Breath(double t) => (Math.Sin((t - 0.25) * 2 * Math.PI) + 1.0) / 2.0;

    /// <summary>Maps a repeating phase to a 0..1 travel value that bounces back at each end instead of wrapping.</summary>
    private static double BounceTravel(double phase)
    {
        phase -= Math.Floor(phase);
        var t = phase * 2.0;
        return t <= 1.0 ? t : 2.0 - t;
    }

    /// <summary>Paints a fading marker centered at a fractional position along one lane, overwriting whatever was there.</summary>
    private static void PaintMarker(
        byte[] buf, int frame, EffectContext ctx, int lane, double centerPos, double trailLen, RgbColor color, int direction)
    {
        for (var led = 0; led < ctx.LedsPerLane; led++)
        {
            var pos = Pos(led, ctx.LedsPerLane, direction);
            var dist = Math.Abs(pos - centerPos);
            if (dist > trailLen)
            {
                continue;
            }
            var intensity = 1.0 - dist / (trailLen + 1);
            SetLed(buf, frame, ctx.Lanes, ctx.LedsPerLane, lane, led, Scale(color, intensity));
        }
    }

    private static RawAnimation Loop(int frameCount, int lanes, int ledsPerLane, double intervalMs, Action<byte[], int> paint)
    {
        var buf = AllocateFrames(frameCount, lanes, ledsPerLane);
        for (var f = 0; f < frameCount; f++)
        {
            paint(buf, f);
        }
        return new RawAnimation(buf, frameCount, intervalMs);
    }

    /// <summary>
    /// Downsamples a frame set (evenly resampled, never truncated) until it
    /// compresses within the wire's <see cref="TinyUz.MaxCompressedLength"/>,
    /// scaling <see cref="RawAnimation.IntervalMs"/> up in step so total loop
    /// duration is unchanged.
    /// </summary>
    private static Slv3StrimerAnimation Finalize(RawAnimation raw, int lanes, int ledsPerLane)
    {
        var stride = lanes * ledsPerLane * 3;
        var frames = raw.Frames;
        var frameCount = raw.FrameCount;
        var intervalMs = raw.IntervalMs;

        while (true)
        {
            var used = frames.AsSpan(0, frameCount * stride);
            var fits = TryCompress(used, out var compressedLength);
            if ((fits && compressedLength <= TinyUz.MaxCompressedLength) || frameCount <= 1)
            {
                return new Slv3StrimerAnimation
                {
                    Frames = used.ToArray(),
                    FrameCount = frameCount,
                    IntervalMs = intervalMs,
                };
            }

            var nextCount = Math.Max(1, frameCount * 3 / 4);
            var resampled = new byte[nextCount * stride];
            for (var i = 0; i < nextCount; i++)
            {
                var srcIndex = (int)((long)i * frameCount / nextCount);
                Array.Copy(frames, srcIndex * stride, resampled, i * stride, stride);
            }
            intervalMs *= (double)frameCount / nextCount;
            frames = resampled;
            frameCount = nextCount;
        }
    }

    /// <summary>TinyUz throws rather than returning an oversized stream; that case reads here as "does not fit".</summary>
    private static bool TryCompress(ReadOnlySpan<byte> data, out int compressedLength)
    {
        try
        {
            compressedLength = TinyUz.Compress(data).Length;
            return true;
        }
        catch (InvalidOperationException)
        {
            compressedLength = int.MaxValue;
            return false;
        }
    }

    /// <summary>Deterministic xorshift64* generator; the same seed always yields the same sequence.</summary>
    private sealed class DeterministicRandom
    {
        private ulong _state;

        public DeterministicRandom(ulong seed) => _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

        public double NextDouble()
        {
            _state ^= _state >> 12;
            _state ^= _state << 25;
            _state ^= _state >> 27;
            var scrambled = _state * 0x2545F4914F6CDD1DUL;
            return (scrambled >> 11) * (1.0 / (1UL << 53));
        }

        public int NextInt(int exclusiveMax) => (int)(NextDouble() * exclusiveMax);
    }
}
