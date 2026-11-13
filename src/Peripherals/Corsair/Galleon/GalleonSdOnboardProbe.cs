using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.Corsair.Galleon;

/// <summary>
/// Best-effort decoding of the onboard lighting objects the Galleon 100 SD keeps
/// (registry, profile, layer list), found by signature inside a read reply because
/// the reply framing has never been captured. Pure and read-only: it only inspects
/// bytes the hub already received. A miss returns false and the probe just logs the raw hex.
/// </summary>
public static class GalleonSdOnboardProbe
{
    public const ushort RegistryResource = 0x000F;
    public const int MaxProfiles = 4;
    public const int MaxLayers = 8;
    public const int MaxResources = 24;

    /// <summary>Profile object slot that references the layer list (zero based, after the 4-byte timestamp).</summary>
    public const int LayerListSlot = 3;

    private const int ProfileSlotsOffset = 6;

    // Past the command echo and the status byte, so a status value can never pose as a signature.
    private const int ScanStart = GalleonSdProtocol.ArgsIndex + 1;

    public static ReadOnlySpan<byte> Strip(ReadOnlySpan<byte> reply) =>
        reply.Length == GalleonSdProtocol.WireLength + 1 && reply[0] == 0 ? reply[1..] : reply;

    /// <summary>Registry object <c>13 00 | count u16 | refs u16 LE</c>.</summary>
    public static bool TryParseRegistry(ReadOnlySpan<byte> reply, out ushort[] refs)
    {
        refs = Array.Empty<ushort>();
        var r = Strip(reply);
        for (var i = ScanStart; i + 4 <= r.Length; i++)
        {
            if (r[i] != 0x13 || r[i + 1] != 0x00) continue;
            var count = r[i + 2] | (r[i + 3] << 8);
            if (count is < 1 or > 32 || i + 4 + (count * 2) > r.Length) continue;
            refs = ReadRefs(r, i + 4, count);
            return true;
        }
        return false;
    }

    /// <summary>Profile object <c>1E F1 | u32 ts | slot refs u16 LE ...</c>; returns the layer list reference in <see cref="LayerListSlot"/>.</summary>
    public static bool TryParseProfileLayerList(ReadOnlySpan<byte> reply, out ushort layerList)
    {
        layerList = 0;
        var r = Strip(reply);
        for (var i = ScanStart; i + 2 <= r.Length; i++)
        {
            if (r[i] != 0x1E || r[i + 1] != 0xF1) continue;
            var at = i + ProfileSlotsOffset + (LayerListSlot * 2);
            if (at + 2 > r.Length) continue;
            var res = (ushort)(r[at] | (r[at + 1] << 8));
            if (res == 0) continue;
            layerList = res;
            return true;
        }
        return false;
    }

    /// <summary>Layer list <c>69 6C | count u16 | count x [08 00 | res u16 LE]</c>.</summary>
    public static bool TryParseLayerList(ReadOnlySpan<byte> reply, out ushort[] layers)
    {
        layers = Array.Empty<ushort>();
        var r = Strip(reply);
        for (var i = ScanStart; i + 4 <= r.Length; i++)
        {
            if (r[i] != 0x69 || r[i + 1] != 0x6C) continue;
            var count = r[i + 2] | (r[i + 3] << 8);
            if (count is < 1 or > 32) continue;
            var list = new List<ushort>(count);
            var at = i + 4;
            for (var n = 0; n < count && at + 4 <= r.Length; n++, at += 4)
            {
                if (r[at] != 0x08 || r[at + 1] != 0x00) break;
                list.Add((ushort)(r[at + 2] | (r[at + 3] << 8)));
            }
            if (list.Count == 0) continue;
            layers = list.ToArray();
            return true;
        }
        return false;
    }

    private static ushort[] ReadRefs(ReadOnlySpan<byte> r, int at, int count)
    {
        var refs = new ushort[count];
        for (var n = 0; n < count; n++) refs[n] = (ushort)(r[at + (n * 2)] | (r[at + (n * 2) + 1] << 8));
        return refs;
    }
}
