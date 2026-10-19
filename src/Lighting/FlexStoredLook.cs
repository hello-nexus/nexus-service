using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Nexus.Service.Peripherals.LianLiFlex;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Lighting;

/// <summary>Turns a unit's stored-effect settings into the package the receiver plays.</summary>
public static class FlexStoredLook
{
    public sealed class Package
    {
        public required byte[] Compressed { get; init; }
        public required int Frames { get; init; }
        public required int Leds { get; init; }
        public required double IntervalMs { get; init; }
    }

    public static IReadOnlyList<RgbColor> ColorsOf(FlexFamily family, string key, IReadOnlyList<string> hex)
    {
        if (hex.Count == 0)
        {
            return FlexDefaultPalettes.RendersFromTable(family, key) ? Array.Empty<RgbColor>() : FlexDefaultPalettes.For(family, key);
        }
        var list = new List<RgbColor>(hex.Count);
        foreach (var h in hex)
        {
            var s = h.TrimStart('#');
            if (s.Length == 6 && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            {
                list.Add(new RgbColor((byte)(v >> 16), (byte)(v >> 8), (byte)v));
            }
        }
        var palette = FlexDefaultPalettes.For(family, key);
        for (var i = list.Count; i < palette.Count; i++)
        {
            list.Add(palette[i]);
        }
        return list.Count == 0 ? palette : list;
    }

    public static Dictionary<string, FlexZoneLook> ZoneLooks(FlexFamily family, LianLiFlexUnitSettings look)
    {
        var result = new Dictionary<string, FlexZoneLook>(StringComparer.Ordinal);
        if (look.Mode == LianLiFlexUnitSettings.ModePerZone)
        {
            var zones = FlexLightingDeviceProvider.ZonesOf(family);
            for (var i = 0; i < zones.Count; i++)
            {
                var z = i < look.Zones.Count ? look.Zones[i] : new LianLiFlexZoneSettings();
                result[zones[i]] = new FlexZoneLook(z.Mode, Math.Clamp(z.Speed, 0, 4), z.Direction != 0 ? 1 : 0,
                    Math.Clamp(z.Brightness, 0, 4), ColorsOf(family, z.Mode, z.Colors));
            }
        }
        else
        {
            result["all"] = new FlexZoneLook(look.Mode, Math.Clamp(look.Speed, 0, 4), look.Direction != 0 ? 1 : 0,
                Math.Clamp(look.Brightness, 0, 4), ColorsOf(family, look.Mode, look.Colors));
        }
        return result;
    }

    /// <summary>The units that play one merged effect: the leader's look drives it, the order is the light sequence.</summary>
    public sealed class MergeGroup
    {
        public required LianLiFlexUnitSettings Leader { get; init; }
        public required string LeaderMac { get; init; }
        public required IReadOnlyList<FlexMergeParticipant> Participants { get; init; }
        public required FlexFamily LeaderFamily { get; init; }
    }

    /// <summary>The colors of a merged effect, resolved once from the leader so every slice uses the same palette.</summary>
    public static IReadOnlyList<RgbColor> MergeColors(MergeGroup group)
    {
        var colors = ColorsOf(group.LeaderFamily, group.Leader.Mode, group.Leader.Colors);
        return colors.Count > 0 ? colors : FlexDefaultPalettes.MergeFallback;
    }

    private static bool Merges(LianLiFlexUnitSettings look, FlexFamily family) =>
        look.Merge
        && look.Mode != LianLiWirelessChainLighting.ModeCustom
        && look.Mode != LianLiFlexUnitSettings.ModePerZone
        && FlexMergeEffects.IsMergeable(family, look.Mode)
        && !FlexMergeEffects.IsPerReceiver(look.Mode);

    /// <summary>Groups every unit that has Merge on with a mergeable effect; empty when fewer than one such unit.</summary>
    public static MergeGroup? FindMergeGroup(IEnumerable<FlexUnit> units, IReadOnlyDictionary<string, LianLiFlexUnitSettings> looks)
    {
        var members = new List<(FlexUnit Unit, LianLiFlexUnitSettings Look)>();
        foreach (var u in units)
        {
            if (u.Controllable && looks.TryGetValue(u.Mac, out var l) && Merges(l, u.Family) && !FlexLightingDeviceProvider.PlaysMotherboardArgb(u, looks))
            {
                members.Add((u, l));
            }
        }
        if (members.Count == 0)
        {
            return null;
        }
        members.Sort((a, b) => a.Look.SortIndex != b.Look.SortIndex
            ? a.Look.SortIndex.CompareTo(b.Look.SortIndex)
            : string.CompareOrdinal(a.Unit.Mac, b.Unit.Mac));
        var participants = new List<FlexMergeParticipant>(members.Count);
        foreach (var (u, l) in members)
        {
            participants.Add(new FlexMergeParticipant(u.Mac, u.Family, u.Fans, Math.Clamp(l.MergeDirection, 0, 1), u.Family == FlexFamily.SlInfFlex && u.Info.FanNum >= 10));
        }
        return new MergeGroup { Leader = members[0].Look, LeaderMac = members[0].Unit.Mac, Participants = participants, LeaderFamily = members[0].Unit.Family };
    }

    public static bool InGroup(MergeGroup? group, string mac)
    {
        if (group is null)
        {
            return false;
        }
        foreach (var p in group.Participants)
        {
            if (p.Mac == mac)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Everything that changes the uploaded bytes, as one comparable string.</summary>
    public static string Signature(FlexUnit unit, LianLiFlexUnitSettings look, MergeGroup? group = null)
    {
        var sb = new StringBuilder();
        if (InGroup(group, unit.Mac))
        {
            var g = group!;
            sb.Append("merge|").Append(g.Leader.Mode).Append('|').Append(g.Leader.Speed).Append('|').Append(g.Leader.Direction)
                .Append('|').Append(g.Leader.Brightness);
            foreach (var c in MergeColors(g))
            {
                sb.Append(':').Append(c.R).Append(',').Append(c.G).Append(',').Append(c.B);
            }
            foreach (var p in g.Participants)
            {
                sb.Append('|').Append(p.Mac).Append(':').Append(p.Fans).Append(':').Append(p.InfRightAttach).Append(':').Append(p.SliceDirection);
            }
            return sb.ToString();
        }
        sb.Append(unit.Family).Append('|').Append(unit.Fans).Append(unit.Info.FanNum >= 10 ? "|ra" : "").Append('|').Append(look.UpDownSwap).Append('|').Append(look.Merge)
            .Append('|').Append(look.SortIndex);
        foreach (var (zone, z) in ZoneLooks(unit.Family, look))
        {
            sb.Append('|').Append(zone).Append(':').Append(z.Key).Append(':').Append(z.Speed).Append(':').Append(z.Direction)
                .Append(':').Append(z.Brightness);
            foreach (var c in z.Colors)
            {
                sb.Append(':').Append(c.R).Append(',').Append(c.G).Append(',').Append(c.B);
            }
        }
        return sb.ToString();
    }

    public static Package? Build(FlexUnit unit, LianLiFlexUnitSettings look, MergeGroup? group = null)
    {
        try
        {
            if (InGroup(group, unit.Mac))
            {
                var g = group!;
                var slices = FlexMergeEffects.Render(
                    g.Leader.Mode, g.Participants, Math.Clamp(g.Leader.Speed, 0, 4), g.Leader.Direction != 0 ? 1 : 0,
                    Math.Clamp(g.Leader.Brightness, 0, 4), MergeColors(g));
                foreach (var slice in slices)
                {
                    if (slice.Mac == unit.Mac)
                    {
                        return new Package
                        {
                            Compressed = TinyUz.Compress(slice.Frames),
                            Frames = slice.FrameCount,
                            Leds = slice.Leds,
                            IntervalMs = slice.IntervalMs,
                        };
                    }
                }
                return null;
            }
            var anim = FlexAnimationComposer.Compose(unit.Family, unit.Fans, ZoneLooks(unit.Family, look), look.UpDownSwap, unit.Info.FanNum >= 10 ? 0 : 1);
            return new Package
            {
                Compressed = TinyUz.Compress(anim.Raw),
                Frames = anim.Frames,
                Leds = anim.Leds,
                IntervalMs = anim.IntervalMs,
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or ArgumentOutOfRangeException)
        {
            Nexus.Service.Platform.ServiceLog.Warn($"[lianli-flex] package build failed for {unit.Mac}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Puts <paramref name="mac"/> at <paramref name="position"/> in the light sequence and renumbers every
    /// stored unit, present or not, so positions stay 0..n-1 and unique.
    /// </summary>
    public static void MoveSortIndex(Dictionary<string, LianLiFlexUnitSettings> units, string mac, int position)
    {
        var order = new List<string>(units.Keys);
        order.Sort((a, b) => units[a].SortIndex != units[b].SortIndex
            ? units[a].SortIndex.CompareTo(units[b].SortIndex)
            : string.CompareOrdinal(a, b));
        order.Remove(mac);
        order.Insert(Math.Clamp(position, 0, order.Count), mac);
        for (var i = 0; i < order.Count; i++)
        {
            units[order[i]] = WithSortIndex(units[order[i]], i);
        }
    }

    public static LianLiFlexUnitSettings WithSortIndex(LianLiFlexUnitSettings u, int sortIndex)
    {
        var c = u.Clone();
        c.SortIndex = sortIndex;
        return c;
    }

    public static LianLiFlexUnitSettings WithPackage(LianLiFlexUnitSettings u, string signature, string effectIndex)
    {
        var c = u.Clone();
        c.PackageSignature = signature;
        c.PackageEffectIndex = effectIndex;
        return c;
    }
}
