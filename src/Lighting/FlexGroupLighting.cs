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

/// <summary>Result of copying one unit's settings onto the others.</summary>
public sealed record FlexApplyAllResult(IReadOnlyList<string> Applied, IReadOnlyList<string> Skipped);

/// <summary>Apply To All: copies one unit's look (and fan profile) to the other units.</summary>
public sealed class FlexApplyAll
{
    private readonly FlexHub _hub;
    private readonly IConfigStore _store;
    private readonly Func<string, LianLiFlexFanProfileSettings, string?>? _applyProfile;

    public FlexApplyAll(FlexHub hub, IConfigStore store, Func<string, LianLiFlexFanProfileSettings, string?>? applyProfile = null)
    {
        _hub = hub;
        _store = store;
        _applyProfile = applyProfile;
    }

    /// <param name="scope">"family" (default) copies to units of the same family; "all" also copies across families where the effect exists in both catalogs.</param>
    public (FlexApplyAllResult? Result, string? Error) Apply(string mac, bool lighting, bool fan, string scope)
    {
        var source = _hub.Find(mac);
        if (source is null)
        {
            return (null, "fan not found");
        }
        if (!lighting && !fan)
        {
            return (null, "nothing to apply");
        }
        if (scope is not ("family" or "all"))
        {
            return (null, "scope must be family or all");
        }
        var looks = _store.Load().Devices.FlexUnits;
        looks.TryGetValue(source.Mac, out var from);
        from ??= new LianLiFlexUnitSettings();
        var applied = new List<string>();
        var skipped = new List<string>();
        var targets = _hub.Units.Where(u => u.Controllable && u.Mac != source.Mac).ToList();
        var lightingDone = new Dictionary<string, LianLiFlexUnitSettings>(StringComparer.Ordinal);
        foreach (var t in targets)
        {
            var sameFamily = t.Family == source.Family;
            if (!sameFamily && scope == "family")
            {
                continue;
            }
            var ok = true;
            if (lighting)
            {
                if (!sameFamily && !LookFitsFamily(from, t.Family))
                {
                    ok = false;
                }
                else
                {
                    looks.TryGetValue(t.Mac, out var old);
                    var next = (old ?? new LianLiFlexUnitSettings()).Clone();
                    next.Mode = from.Mode;
                    next.EffectMode = from.EffectMode;
                    next.Speed = from.Speed;
                    next.Direction = from.Direction;
                    next.Brightness = from.Brightness;
                    next.Colors = from.Colors;
                    next.Merge = from.Merge;
                    if (sameFamily)
                    {
                        next.Zones = from.Zones;
                        next.UpDownSwap = from.UpDownSwap;
                    }
                    lightingDone[t.Mac] = next;
                }
            }
            if (ok)
            {
                applied.Add(t.Mac);
            }
            else
            {
                skipped.Add(t.Mac);
            }
        }
        if (lightingDone.Count > 0)
        {
            _store.Update(s =>
            {
                var next = new Dictionary<string, LianLiFlexUnitSettings>(s.Devices.FlexUnits, StringComparer.Ordinal);
                foreach (var (m, l) in lightingDone)
                {
                    next[m] = l;
                }
                s.Devices.FlexUnits = next;
            });
        }
        if (fan && _applyProfile is not null)
        {
            var profile = from.FanProfile;
            foreach (var m in applied.ToList())
            {
                var error = _applyProfile(m, profile.Clone());
                if (error is not null)
                {
                    applied.Remove(m);
                    skipped.Add(m);
                }
            }
        }
        return (new FlexApplyAllResult(applied, skipped), null);
    }

    private static bool LookFitsFamily(LianLiFlexUnitSettings look, FlexFamily family)
    {
        if (look.Mode == LianLiWirelessChainLighting.ModeCustom)
        {
            return true;
        }
        if (look.Mode == LianLiFlexUnitSettings.ModePerZone)
        {
            return false;
        }
        return FlexAnimationComposer.Keys(family).Contains(look.Mode);
    }
}
