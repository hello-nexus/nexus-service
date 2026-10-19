using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Nexus.Service.Peripherals.LianLiFlex;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>One member of the Quick/Sync group.</summary>
public sealed record FlexSyncMember(string Mac, FlexFamily Family, int SortIndex, int SliceDirection);

/// <summary>A change to the group; null fields keep what the leader has.</summary>
public sealed class FlexSyncPatch
{
    public bool? Enabled { get; init; }
    public string? Mode { get; init; }
    public int? Speed { get; init; }
    public int? Direction { get; init; }
    public int? Brightness { get; init; }
    public IReadOnlyList<string>? Colors { get; init; }
    /// <summary>Members in light-sequence order with their slice direction; null keeps the current order.</summary>
    public IReadOnlyList<(string Mac, int SliceDirection)>? Members { get; init; }
}

/// <summary>The Quick/Sync control: one look written to every Flex unit with merge on, in one store update.</summary>
public sealed partial class FlexGroupLighting
{
    private readonly FlexHub _hub;
    private readonly IConfigStore _store;

    public FlexGroupLighting(FlexHub hub, IConfigStore store)
    {
        _hub = hub;
        _store = store;
    }

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();

    /// <summary>Controllable units in light-sequence order (sort index, then MAC).</summary>
    public IReadOnlyList<FlexSyncMember> Members()
    {
        var looks = _store.Load().Devices.FlexUnits;
        var members = new List<FlexSyncMember>();
        foreach (var u in _hub.Units)
        {
            if (!u.Controllable)
            {
                continue;
            }
            looks.TryGetValue(u.Mac, out var l);
            members.Add(new FlexSyncMember(u.Mac, u.Family, l?.SortIndex ?? 0, l?.MergeDirection ?? 0));
        }
        members.Sort((a, b) => a.SortIndex != b.SortIndex ? a.SortIndex.CompareTo(b.SortIndex) : string.CompareOrdinal(a.Mac, b.Mac));
        return members;
    }

    /// <summary>The MAC of the unit whose look drives the merged effect (the same pick the writer uses); empty when none merges.</summary>
    public string LeaderMac()
    {
        var group = FlexStoredLook.FindMergeGroup(_hub.Units, _store.Load().Devices.FlexUnits);
        return group?.LeaderMac ?? "";
    }

    /// <summary>The look of the merge leader, or a default look when no unit merges.</summary>
    public LianLiFlexUnitSettings Leader() =>
        FlexStoredLook.FindMergeGroup(_hub.Units, _store.Load().Devices.FlexUnits)?.Leader
        ?? new LianLiFlexUnitSettings { Mode = DefaultMode };

    public const string DefaultMode = "rainbow";

    /// <summary>A group mode is always one of the shared effects; anything else (custom, per zone, unknown) reads as the default.</summary>
    public static string SyncModeOf(string? mode) =>
        mode is not null && FlexMergeEffects.MergeKeys(FlexFamily.TlFlex).Contains(mode) ? mode : DefaultMode;

    public bool Enabled => _store.Load().Devices.FlexSyncEnabled;

    /// <summary>Error message, or null when applied.</summary>
    public string? Apply(FlexSyncPatch patch)
    {
        var members = Members();
        if (members.Count == 0)
        {
            return "no fans";
        }
        var order = new List<(string Mac, int SliceDirection)>();
        if (patch.Members is { } given)
        {
            foreach (var (mac, dir) in given)
            {
                if (members.All(m => !string.Equals(m.Mac, mac, StringComparison.OrdinalIgnoreCase)))
                {
                    return "unknown fan";
                }
                if (order.Any(o => string.Equals(o.Mac, mac, StringComparison.OrdinalIgnoreCase)))
                {
                    return "duplicate fan";
                }
                order.Add((members.First(m => string.Equals(m.Mac, mac, StringComparison.OrdinalIgnoreCase)).Mac, Math.Clamp(dir, 0, 1)));
            }
            foreach (var m in members)
            {
                if (order.All(o => o.Mac != m.Mac))
                {
                    order.Add((m.Mac, m.SliceDirection));
                }
            }
        }
        else
        {
            order.AddRange(members.Select(m => (m.Mac, m.SliceDirection)));
        }
        if (patch.Colors is { } colors && (colors.Count > 4 || colors.Any(c => !HexColor().IsMatch(c))))
        {
            return "invalid colors";
        }
        var enabled = patch.Enabled ?? Enabled;
        var leader = Leader();
        if (patch.Mode is not null && !FlexMergeEffects.MergeKeys(FlexFamily.TlFlex).Contains(patch.Mode))
        {
            return "unknown effect";
        }
        var mode = SyncModeOf(patch.Mode ?? leader.Mode);
        if (enabled)
        {
            foreach (var m in members)
            {
                if (!FlexMergeEffects.IsMergeable(m.Family, mode))
                {
                    return "effect cannot be merged";
                }
            }
        }
        _store.Update(s =>
        {
            var next = new Dictionary<string, LianLiFlexUnitSettings>(s.Devices.FlexUnits, StringComparer.Ordinal);
            for (var i = 0; i < order.Count; i++)
            {
                next.TryGetValue(order[i].Mac, out var old);
                var u = (old ?? new LianLiFlexUnitSettings()).Clone();
                u.SortIndex = i;
                u.MergeDirection = order[i].SliceDirection;
                if (enabled)
                {
                    u.Mode = mode;
                    u.EffectMode = mode;
                    u.Speed = Math.Clamp(patch.Speed ?? leader.Speed, 0, 4);
                    u.Direction = Math.Clamp(patch.Direction ?? leader.Direction, 0, 1);
                    u.Brightness = Math.Clamp(patch.Brightness ?? leader.Brightness, 0, 4);
                    u.Colors = patch.Colors is { } c ? new List<string>(c) : leader.Colors;
                    u.Merge = true;
                }
                else
                {
                    u.Merge = false;
                }
                next[order[i].Mac] = u;
            }
            s.Devices.FlexUnits = next;
            s.Devices.FlexSyncEnabled = enabled;
        });
        return null;
    }
}
