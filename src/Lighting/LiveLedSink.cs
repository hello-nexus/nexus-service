using System;
using System.Collections.Concurrent;
using Nexus.Service.Lighting.Engine;

namespace Nexus.Service.Lighting;

/// <summary>
/// The color each LED is actually driven with, collected from the frame writers
/// for the dashboard preview. A writer reports right where it computes its own
/// output, using the same <see cref="DeviceColorAdjust"/> math but stopping
/// before the channel swap, so the preview shows the true color of a strip wired
/// in a different order. Reporting is a no-op until a dashboard client is
/// connected, and writers never read anything back, so their own bytes are
/// untouched.
/// </summary>
public sealed class LiveLedSink
{
    /// <summary>How long a report counts as the device's current output.</summary>
    public const long FreshMs = 500;

    private sealed record Entry(byte[] Bytes, long StampMs);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    internal Func<long> Clock { get; set; } = () => Environment.TickCount64;

    /// <summary>Set while at least one dashboard client is connected.</summary>
    public volatile bool Enabled;

    /// <summary>
    /// Report a device whose bytes come from the shared engine frame through the
    /// per-device brightness multiplier and color trim, the pattern most writers
    /// share. Black stays black; identify flashes report what the strip shows.
    /// </summary>
    public void Report(string id, ReadOnlySpan<byte> src, int ledCount, double brightnessMul,
        DeviceColorAdjust adjust, bool hasIdentify = false, long identifyStartTicks = 0, long nowTicks = 0,
        int identifyHalfPeriodMs = 250)
    {
        if (!Enabled)
        {
            return;
        }
        var count = Math.Max(0, Math.Min(ledCount, src.Length / 3));
        var bytes = new byte[count * 3];
        if (hasIdentify)
        {
            var elapsedMs = (nowTicks - identifyStartTicks) / TimeSpan.TicksPerMillisecond;
            if ((elapsedMs / identifyHalfPeriodMs) % 2 == 0)
            {
                Array.Fill(bytes, (byte)255);
            }
        }
        else
        {
            Compute(bytes, src, count, brightnessMul, adjust);
        }
        Store(id, bytes);
    }

    /// <summary>Report bytes the writer already holds in true-color order.</summary>
    public void ReportBytes(string id, ReadOnlySpan<byte> rgb)
    {
        if (!Enabled)
        {
            return;
        }
        Store(id, rgb.ToArray());
    }

    /// <summary>Drop a device's output at once: it is not being driven (handed back to firmware).</summary>
    public void ReportNone(string id)
    {
        if (!Enabled)
        {
            return;
        }
        _entries.TryRemove(id, out _);
    }

    /// <summary>Report one color shown on every LED of a device, for rings the writer drives with a single color.</summary>
    public void ReportSolid(string id, byte r, byte g, byte b, int ledCount)
    {
        if (!Enabled)
        {
            return;
        }
        var bytes = new byte[Math.Max(0, ledCount) * 3];
        for (var i = 0; i < bytes.Length; i += 3)
        {
            bytes[i] = r;
            bytes[i + 1] = g;
            bytes[i + 2] = b;
        }
        Store(id, bytes);
    }

    /// <summary>Black out part of an already reported device, for LEDs a writer blanks after composing.</summary>
    public void BlankRange(string id, int firstLed, int count)
    {
        if (!Enabled || !_entries.TryGetValue(id, out var entry))
        {
            return;
        }
        var bytes = (byte[])entry.Bytes.Clone();
        var from = Math.Min(bytes.Length, firstLed * 3);
        var to = Math.Min(bytes.Length, (firstLed + count) * 3);
        Array.Clear(bytes, from, to - from);
        _entries[id] = entry with { Bytes = bytes };
    }

    private void Store(string id, byte[] bytes)
    {
        _entries[id] = new Entry(bytes, Clock());
    }

    /// <summary>The latest fresh report for <paramref name="id"/>, or null when the device is not being driven.</summary>
    public byte[]? TryGetFresh(string id) =>
        _entries.TryGetValue(id, out var e) && Clock() - e.StampMs <= FreshMs ? e.Bytes : null;

    /// <summary>
    /// The color the standard writers drive: brightness first, then the trim
    /// without the channel swap. Identity trim takes the writers' own scaling so
    /// the bytes match exactly.
    /// </summary>
    public static void Compute(Span<byte> dst, ReadOnlySpan<byte> src, int ledCount, double mul, DeviceColorAdjust adjust)
    {
        if (mul <= 0.0)
        {
            dst[..(ledCount * 3)].Clear();
            return;
        }
        var passthrough = adjust.IsIdentity && mul >= 0.999;
        for (var i = 0; i < ledCount; i++)
        {
            var off = i * 3;
            if (adjust.IsIdentity)
            {
                if (passthrough)
                {
                    dst[off] = src[off];
                    dst[off + 1] = src[off + 1];
                    dst[off + 2] = src[off + 2];
                }
                else
                {
                    dst[off] = (byte)(src[off] * mul);
                    dst[off + 1] = (byte)(src[off + 1] * mul);
                    dst[off + 2] = (byte)(src[off + 2] * mul);
                }
            }
            else
            {
                adjust.ApplyUnordered(src[off], src[off + 1], src[off + 2], mul, out dst[off], out dst[off + 1], out dst[off + 2]);
            }
        }
    }
}
