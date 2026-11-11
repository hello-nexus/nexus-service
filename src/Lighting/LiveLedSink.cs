using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Zones;

namespace Nexus.Service.Lighting;

/// <summary>How a writer turns the engine bytes into the bytes it drives, so the dashboard copy matches it to the last bit.</summary>
public enum LiveScale
{
    /// <summary>Trim when set, else a full-brightness copy at or above the pass-through band, else a double scale.</summary>
    Banded,
    /// <summary>The trim math even with no trim set.</summary>
    Adjust,
    /// <summary>A double scale with no pass-through band when no trim is set.</summary>
    Direct,
}

/// <summary>The color each LED is actually driven with, collected from the frame writers for the dashboard preview, before the channel swap.</summary>
public sealed class LiveLedSink
{
    /// <summary>How long a report counts as the device's current output.</summary>
    public const long FreshMs = 500;

    private sealed class Entry
    {
        public readonly object Lock = new();
        public byte[] Bytes = Array.Empty<byte>();
        // Negative while the device is not being driven.
        public int Length = -1;
        public long StampMs;
        public byte[] Staged = Array.Empty<byte>();
        public int StagedLength = -1;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    internal Func<long> Clock { get; set; } = () => Environment.TickCount64;

    /// <summary>Set once per engine frame from the dashboard client count; writers skip all report work while false.</summary>
    public volatile bool Enabled;

    private Entry EntryFor(string id) =>
        _entries.TryGetValue(id, out var e) ? e : _entries.GetOrAdd(id, static _ => new Entry());

    /// <summary>Publish a device's output at once, for writers that send what they compute.</summary>
    public void Report(string id, ReadOnlySpan<byte> src, int ledCount, double mul, DeviceColorAdjust adjust, LiveScale scale = LiveScale.Banded)
    {
        if (Enabled)
        {
            Write(id, src, ledCount, mul, adjust, scale, false, false, 0, 0, 0);
        }
    }

    /// <summary>Publish with an identify flash running, which shows white on its on phase and black on its off phase.</summary>
    public void Report(string id, ReadOnlySpan<byte> src, int ledCount, double mul, DeviceColorAdjust adjust,
        bool hasIdentify, long identifyStartTicks, long nowTicks, int identifyHalfPeriodMs, LiveScale scale = LiveScale.Banded)
    {
        if (Enabled)
        {
            Write(id, src, ledCount, mul, adjust, scale, false, hasIdentify, identifyStartTicks, nowTicks, identifyHalfPeriodMs);
        }
    }

    /// <summary>Hold a device's output until <see cref="Commit"/>, for writers that may skip the push (dedupe, pacing).</summary>
    public void Stage(string id, ReadOnlySpan<byte> src, int ledCount, double mul, DeviceColorAdjust adjust,
        bool hasIdentify, long identifyStartTicks, long nowTicks, int identifyHalfPeriodMs, LiveScale scale = LiveScale.Banded)
    {
        if (Enabled)
        {
            Write(id, src, ledCount, mul, adjust, scale, true, hasIdentify, identifyStartTicks, nowTicks, identifyHalfPeriodMs);
        }
    }

    private void Write(string id, ReadOnlySpan<byte> src, int ledCount, double mul, DeviceColorAdjust adjust, LiveScale scale,
        bool staged, bool hasIdentify, long identifyStartTicks, long nowTicks, int identifyHalfPeriodMs)
    {
        var count = Math.Max(0, Math.Min(ledCount, src.Length / 3));
        var e = EntryFor(id);
        lock (e.Lock)
        {
            ref var buffer = ref staged ? ref e.Staged : ref e.Bytes;
            if (buffer.Length < count * 3)
            {
                buffer = new byte[count * 3];
            }
            var dst = buffer.AsSpan(0, count * 3);
            if (hasIdentify)
            {
                var elapsedMs = (nowTicks - identifyStartTicks) / TimeSpan.TicksPerMillisecond;
                dst.Fill((elapsedMs / identifyHalfPeriodMs) % 2 == 0 ? (byte)255 : (byte)0);
            }
            else
            {
                Compute(dst, src, count, mul, adjust, scale);
            }
            if (staged)
            {
                e.StagedLength = count * 3;
            }
            else
            {
                e.Length = count * 3;
                e.StampMs = Clock();
            }
        }
    }

    /// <summary>Report one color shown on every LED of a device, for rings the writer drives with a single color.</summary>
    public void ReportSolid(string id, byte r, byte g, byte b, int ledCount)
    {
        if (!Enabled)
        {
            return;
        }
        var e = EntryFor(id);
        lock (e.Lock)
        {
            var n = Math.Max(0, ledCount) * 3;
            if (e.Bytes.Length < n)
            {
                e.Bytes = new byte[n];
            }
            for (var i = 0; i < n; i += 3)
            {
                e.Bytes[i] = r;
                e.Bytes[i + 1] = g;
                e.Bytes[i + 2] = b;
            }
            e.Length = n;
            e.StampMs = Clock();
        }
    }

    /// <summary>Report that a device is not being driven (handed back to firmware).</summary>
    public void ReportNone(string id)
    {
        if (!Enabled)
        {
            return;
        }
        var e = EntryFor(id);
        lock (e.Lock)
        {
            e.Length = -1;
        }
    }

    /// <summary>Publish what <see cref="Stage"/> held, because the writer pushed it.</summary>
    public void Commit(string id)
    {
        if (!Enabled || !_entries.TryGetValue(id, out var e))
        {
            return;
        }
        lock (e.Lock)
        {
            if (e.StagedLength < 0)
            {
                return;
            }
            if (e.Bytes.Length < e.StagedLength)
            {
                e.Bytes = new byte[e.Staged.Length];
            }
            Buffer.BlockCopy(e.Staged, 0, e.Bytes, 0, e.StagedLength);
            e.Length = e.StagedLength;
            e.StagedLength = -1;
            e.StampMs = Clock();
        }
    }

    /// <summary>Keep the last published output alive on a tick that pushed nothing new.</summary>
    public void Touch(string id)
    {
        if (!Enabled || !_entries.TryGetValue(id, out var e))
        {
            return;
        }
        lock (e.Lock)
        {
            if (e.Length >= 0)
            {
                e.StampMs = Clock();
            }
        }
    }

    public void CommitZones(IReadOnlyList<ResolvedZone> zones)
    {
        for (var i = 0; i < zones.Count; i++)
        {
            Commit(zones[i].Id);
        }
    }

    public void ReportNoneZones(IReadOnlyList<ResolvedZone> zones)
    {
        for (var i = 0; i < zones.Count; i++)
        {
            ReportNone(zones[i].Id);
        }
    }

    public void TouchZones(IReadOnlyList<ResolvedZone> zones)
    {
        for (var i = 0; i < zones.Count; i++)
        {
            Touch(zones[i].Id);
        }
    }

    /// <summary>Black out LEDs of the staged output that a writer blanks after composing.</summary>
    public void BlankStaged(string id, int firstLed, int count)
    {
        if (!Enabled || !_entries.TryGetValue(id, out var e))
        {
            return;
        }
        lock (e.Lock)
        {
            if (e.StagedLength < 0)
            {
                return;
            }
            var from = Math.Min(e.StagedLength, firstLed * 3);
            var to = Math.Min(e.StagedLength, (firstLed + count) * 3);
            Array.Clear(e.Staged, from, to - from);
        }
    }

    /// <summary>Copy a device's fresh output into <paramref name="dst"/> and return its length, or -1 when it is not being driven.</summary>
    public int CopyFresh(string id, Span<byte> dst)
    {
        if (!_entries.TryGetValue(id, out var e))
        {
            return -1;
        }
        lock (e.Lock)
        {
            if (e.Length < 0 || Clock() - e.StampMs > FreshMs)
            {
                return -1;
            }
            var n = Math.Min(e.Length, dst.Length);
            e.Bytes.AsSpan(0, n).CopyTo(dst);
            return n;
        }
    }

    /// <summary>The latest fresh output as a new array, or null when the device is not being driven.</summary>
    public byte[]? TryGetFresh(string id)
    {
        if (!_entries.TryGetValue(id, out var e))
        {
            return null;
        }
        lock (e.Lock)
        {
            return e.Length < 0 || Clock() - e.StampMs > FreshMs ? null : e.Bytes.AsSpan(0, e.Length).ToArray();
        }
    }

    /// <summary>Drop entries for devices that left the engine's device set.</summary>
    public void Prune(DeviceFrame[] devices)
    {
        foreach (var id in _entries.Keys)
        {
            var present = false;
            for (var i = 0; i < devices.Length; i++)
            {
                if (devices[i].Id == id)
                {
                    present = true;
                    break;
                }
            }
            if (!present)
            {
                _entries.TryRemove(id, out _);
            }
        }
    }

    /// <summary>The bytes a writer drives for <paramref name="ledCount"/> LEDs, before the channel swap.</summary>
    public static void Compute(Span<byte> dst, ReadOnlySpan<byte> src, int ledCount, double mul, DeviceColorAdjust adjust, LiveScale scale = LiveScale.Banded)
    {
        if (scale == LiveScale.Adjust)
        {
            for (var i = 0; i < ledCount; i++)
            {
                var off = i * 3;
                adjust.ApplyUnordered(src[off], src[off + 1], src[off + 2], mul, out dst[off], out dst[off + 1], out dst[off + 2]);
            }
            return;
        }
        if (mul <= 0.0)
        {
            dst[..(ledCount * 3)].Clear();
            return;
        }
        var passthrough = scale == LiveScale.Banded && adjust.IsIdentity && mul >= 0.999;
        for (var i = 0; i < ledCount; i++)
        {
            var off = i * 3;
            if (!adjust.IsIdentity)
            {
                adjust.ApplyUnordered(src[off], src[off + 1], src[off + 2], mul, out dst[off], out dst[off + 1], out dst[off + 2]);
            }
            else if (passthrough)
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
    }
}
