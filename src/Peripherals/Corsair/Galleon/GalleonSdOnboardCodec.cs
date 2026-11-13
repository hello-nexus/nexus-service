using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.Hyte.Np50;

namespace Nexus.Service.Peripherals.Corsair.Galleon;

/// <summary>One static onboard layer: a single color over a set of key ids (ascending).</summary>
public sealed record GalleonQuantLayer(RgbColor Color, byte[] Keys);

/// <summary>
/// Pure byte-level pieces of the onboard writer: the color quantizer and the exact object layouts seen in
/// Web Hub captures (static layer, layer list, profile, registry). Strict parsers: anything that does not
/// match the captured layout is rejected, so a write is never built from a guess.
/// </summary>
public static class GalleonSdOnboardCodec
{
    public const int MaxLayers = 8;

    /// <summary>A key whose brightest channel is below this is treated as off and gets no layer.</summary>
    public const int BlackCutoff = 4;

    public const int ProfileSlotCount = 12;
    public const int ProfileSlotsOffset = 6;
    public const int ProfileTimestampOffset = 2;
    public const int MaxListLayers = 32;

    private static readonly byte[] LayerHeader = { 0x7E, 0x20, 0x01, 0x00, 0x00, 0x00, 0x01, 0xFF };
    private const int LayerColorOffset = 8;

    public static bool IsOff(RgbColor c) => Math.Max(c.R, Math.Max(c.G, c.B)) < BlackCutoff;

    /// <summary>
    /// Groups the frame (<paramref name="leds"/> in <see cref="GalleonSdProtocol.KeyIds"/> order) into at most
    /// <see cref="MaxLayers"/> colors. Off keys are skipped. Up to the limit the colors are kept exactly; above it a
    /// deterministic weighted median cut merges them. Layers are ordered by key count descending, then by color.
    /// </summary>
    public static List<GalleonQuantLayer> Quantize(ReadOnlySpan<RgbColor> leds)
    {
        var n = Math.Min(leds.Length, GalleonSdProtocol.KeyIds.Length);
        var byColor = new Dictionary<int, List<byte>>();
        for (var i = 0; i < n; i++)
        {
            if (IsOff(leds[i])) continue;
            var packed = Pack(leds[i]);
            if (!byColor.TryGetValue(packed, out var keys))
            {
                keys = new List<byte>();
                byColor[packed] = keys;
            }
            keys.Add((byte)GalleonSdProtocol.KeyIds[i]);
        }

        var groups = byColor.OrderBy(kv => kv.Key).Select(kv => (Color: kv.Key, Keys: kv.Value)).ToList();
        if (groups.Count > MaxLayers) groups = MedianCut(groups);

        return groups
            .Select(g => new GalleonQuantLayer(Unpack(g.Color), g.Keys.OrderBy(k => k).ToArray()))
            .OrderByDescending(l => l.Keys.Length)
            .ThenBy(l => Pack(l.Color))
            .ToList();
    }

    private static List<(int Color, List<byte> Keys)> MedianCut(List<(int Color, List<byte> Keys)> colors)
    {
        var boxes = new List<List<(int Color, List<byte> Keys)>> { colors };
        while (boxes.Count < MaxLayers)
        {
            var pick = -1;
            var pickWeight = 0;
            for (var b = 0; b < boxes.Count; b++)
            {
                if (boxes[b].Count < 2) continue;
                var w = boxes[b].Sum(c => c.Keys.Count);
                if (w > pickWeight)
                {
                    pickWeight = w;
                    pick = b;
                }
            }
            if (pick < 0) break;

            var box = boxes[pick];
            var channel = WidestChannel(box);
            var sorted = box.OrderBy(c => Channel(c.Color, channel)).ThenBy(c => c.Color).ToList();
            var half = (sorted.Sum(c => c.Keys.Count) + 1) / 2;
            var run = 0;
            var cut = 1;
            for (var i = 0; i < sorted.Count - 1; i++)
            {
                run += sorted[i].Keys.Count;
                cut = i + 1;
                if (run >= half) break;
            }
            boxes[pick] = sorted.Take(cut).ToList();
            boxes.Add(sorted.Skip(cut).ToList());
        }

        var merged = new SortedDictionary<int, List<byte>>();
        foreach (var box in boxes)
        {
            var total = box.Sum(c => c.Keys.Count);
            long r = 0, g = 0, b = 0;
            foreach (var c in box)
            {
                r += Channel(c.Color, 0) * (long)c.Keys.Count;
                g += Channel(c.Color, 1) * (long)c.Keys.Count;
                b += Channel(c.Color, 2) * (long)c.Keys.Count;
            }
            var rep = Pack(new RgbColor((byte)((r + (total / 2)) / total), (byte)((g + (total / 2)) / total), (byte)((b + (total / 2)) / total)));
            if (!merged.TryGetValue(rep, out var keys))
            {
                keys = new List<byte>();
                merged[rep] = keys;
            }
            foreach (var c in box) keys.AddRange(c.Keys);
        }
        return merged.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private static int WidestChannel(List<(int Color, List<byte> Keys)> box)
    {
        var best = 0;
        var bestRange = -1;
        for (var ch = 0; ch < 3; ch++)
        {
            var lo = 255;
            var hi = 0;
            foreach (var c in box)
            {
                var v = Channel(c.Color, ch);
                lo = Math.Min(lo, v);
                hi = Math.Max(hi, v);
            }
            if (hi - lo > bestRange)
            {
                bestRange = hi - lo;
                best = ch;
            }
        }
        return best;
    }

    private static int Pack(RgbColor c) => (c.R << 16) | (c.G << 8) | c.B;

    private static RgbColor Unpack(int p) => new((byte)(p >> 16), (byte)(p >> 8), (byte)p);

    private static int Channel(int packed, int ch) => ch switch { 0 => (packed >> 16) & 0xFF, 1 => (packed >> 8) & 0xFF, _ => packed & 0xFF };

    /// <summary><c>7E 20 01 00 00 00 01 FF | B G R | N | N key ids ascending</c>.</summary>
    public static byte[] BuildLayerBody(GalleonQuantLayer layer)
    {
        var body = new byte[LayerColorOffset + 4 + layer.Keys.Length];
        LayerHeader.CopyTo(body, 0);
        body[LayerColorOffset] = layer.Color.B;
        body[LayerColorOffset + 1] = layer.Color.G;
        body[LayerColorOffset + 2] = layer.Color.R;
        body[LayerColorOffset + 3] = (byte)layer.Keys.Length;
        layer.Keys.CopyTo(body, LayerColorOffset + 4);
        return body;
    }

    /// <summary>The color of a static layer body this codec wrote; false for any other layer type or a malformed body.</summary>
    public static bool TryReadLayerColor(ReadOnlySpan<byte> body, out RgbColor color)
    {
        color = default;
        if (body.Length < LayerColorOffset + 4 || !body[..LayerHeader.Length].SequenceEqual(LayerHeader)) return false;
        if (body.Length != LayerColorOffset + 4 + body[LayerColorOffset + 3]) return false;
        color = new RgbColor(body[LayerColorOffset + 2], body[LayerColorOffset + 1], body[LayerColorOffset]);
        return true;
    }

    /// <summary><c>69 6C | count u16 | count x [08 00 | res u16 LE]</c>.</summary>
    public static byte[] BuildLayerList(IReadOnlyList<ushort> refs)
    {
        var data = new byte[4 + (refs.Count * 4)];
        data[0] = 0x69;
        data[1] = 0x6C;
        data[2] = (byte)(refs.Count & 0xFF);
        data[3] = (byte)(refs.Count >> 8);
        for (var i = 0; i < refs.Count; i++)
        {
            var at = 4 + (i * 4);
            data[at] = 0x08;
            data[at + 1] = 0x00;
            data[at + 2] = (byte)(refs[i] & 0xFF);
            data[at + 3] = (byte)(refs[i] >> 8);
        }
        return data;
    }

    /// <summary>Strict: the body is exactly the list, an empty list included.</summary>
    public static bool TryParseLayerList(ReadOnlySpan<byte> data, out ushort[] refs)
    {
        refs = Array.Empty<ushort>();
        if (data.Length < 4 || data[0] != 0x69 || data[1] != 0x6C) return false;
        var count = data[2] | (data[3] << 8);
        if (count > MaxListLayers || data.Length != 4 + (count * 4)) return false;
        var list = new ushort[count];
        for (var i = 0; i < count; i++)
        {
            var at = 4 + (i * 4);
            if (data[at] != 0x08 || data[at + 1] != 0x00) return false;
            list[i] = (ushort)(data[at + 2] | (data[at + 3] << 8));
        }
        refs = list;
        return true;
    }

    /// <summary>Strict: <c>13 00 | count u16 | refs u16 LE</c> with nothing after the refs.</summary>
    public static bool TryParseRegistry(ReadOnlySpan<byte> data, out ushort[] refs)
    {
        refs = Array.Empty<ushort>();
        if (data.Length < 4 || data[0] != 0x13 || data[1] != 0x00) return false;
        var count = data[2] | (data[3] << 8);
        if (count is < 1 or > 32 || data.Length != 4 + (count * 2)) return false;
        var list = new ushort[count];
        for (var i = 0; i < count; i++) list[i] = (ushort)(data[4 + (i * 2)] | (data[5 + (i * 2)] << 8));
        refs = list;
        return true;
    }

    /// <summary>Profile <c>1E F1 | ts u32 LE | slot refs u16 LE ...</c>; returns the slot refs (zero for an empty slot).</summary>
    public static bool TryParseProfile(ReadOnlySpan<byte> data, out uint timestamp, out ushort[] slots)
    {
        timestamp = 0;
        slots = Array.Empty<ushort>();
        var end = ProfileSlotsOffset + (ProfileSlotCount * 2);
        if (data.Length < end + 2 || data[0] != 0x1E || data[1] != 0xF1) return false;
        timestamp = (uint)(data[2] | (data[3] << 8) | (data[4] << 16) | (data[5] << 24));
        var list = new ushort[ProfileSlotCount];
        for (var i = 0; i < ProfileSlotCount; i++) list[i] = (ushort)(data[ProfileSlotsOffset + (i * 2)] | (data[ProfileSlotsOffset + (i * 2) + 1] << 8));
        slots = list;
        return true;
    }

    /// <summary>A copy of the profile with only the timestamp replaced, as Web Hub rewrites it after every edit.</summary>
    public static byte[] WithTimestamp(ReadOnlySpan<byte> profile, uint timestamp)
    {
        var copy = profile.ToArray();
        copy[ProfileTimestampOffset] = (byte)timestamp;
        copy[ProfileTimestampOffset + 1] = (byte)(timestamp >> 8);
        copy[ProfileTimestampOffset + 2] = (byte)(timestamp >> 16);
        copy[ProfileTimestampOffset + 3] = (byte)(timestamp >> 24);
        return copy;
    }
}
