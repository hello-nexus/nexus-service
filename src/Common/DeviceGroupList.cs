using System;
using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Common;

/// <summary>
/// Shared sanitizer for the lighting and cooling pages' user-made groups, and
/// for the lighting page's device stacks. The client owns order and membership
/// and PUTs the whole list, so this is the one place the invariants hold: at
/// most <see cref="MaxGroups"/> groups, a member in at most one of them, and no
/// blank ids or names.
/// </summary>
public static class DeviceGroupList
{
    public const int MaxGroups = 10;

    /// <summary>Name length cap, matching the client's rename field.</summary>
    public const int MaxNameLength = 20;

    /// <summary>
    /// The list as it will be stored. Groups past the cap are dropped and a
    /// member id repeated across groups stays in the first group that claims it,
    /// so a card can never render twice. An empty group is kept: one is created
    /// empty and stays that way until the user drags a card into it.
    /// </summary>
    public static List<DeviceGroup> Sanitize(IReadOnlyList<DeviceGroup>? incoming)
    {
        var result = new List<DeviceGroup>();
        if (incoming is null) return result;

        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in incoming)
        {
            if (result.Count >= MaxGroups) break;

            var id = (group.Id ?? "").Trim();
            if (id.Length == 0 || !ids.Add(id)) continue;

            var members = new List<string>(group.Members.Count);
            foreach (var member in group.Members)
            {
                var trimmed = (member ?? "").Trim();
                if (trimmed.Length == 0) continue;
                if (!claimed.Add(trimmed)) continue;
                members.Add(trimmed);
            }
            var name = (group.Name ?? "").Trim();
            if (name.Length > MaxNameLength) name = name[..MaxNameLength];

            var parent = group.Parent?.Trim();
            if (string.IsNullOrEmpty(parent) || parent == id) parent = null;

            // null and "" mean different things: never placed vs pinned to the
            // top of the rail. Defaulting a missing anchor to "" sent every new
            // group to the top on its first round-trip.
            result.Add(new DeviceGroup { Id = id, Name = name, Members = members, After = group.After?.Trim(), Parent = parent });
        }
        UnplaceDanglingParents(incoming, result);
        return result;
    }

    /// <summary>The frame layouts a stack may name; anything else is stored as null (overlap).</summary>
    public static readonly string[] StackLayouts = { "overlap", "parallel", "series" };

    /// <summary>
    /// A stack list as it will be stored: the group invariants minus the cap and
    /// the rail fields, and a stack holding fewer than two cards dissolves. The
    /// layout keeps only a known value, so a client never reads a word it does
    /// not draw.
    /// </summary>
    public static List<DeviceGroup> SanitizeStacks(IReadOnlyList<DeviceGroup>? incoming)
    {
        var result = new List<DeviceGroup>();
        if (incoming is null) return result;

        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stack in incoming)
        {
            var id = (stack.Id ?? "").Trim();
            if (id.Length == 0 || !ids.Add(id)) continue;

            var members = new List<string>(stack.Members.Count);
            foreach (var member in stack.Members)
            {
                var trimmed = (member ?? "").Trim();
                if (trimmed.Length == 0 || !claimed.Add(trimmed)) continue;
                members.Add(trimmed);
            }
            if (members.Count < 2) continue;
            var name = (stack.Name ?? "").Trim();
            if (name.Length > MaxNameLength) name = name[..MaxNameLength];
            result.Add(new DeviceGroup { Id = id, Name = name, Members = members, Layout = Known(stack.Layout, StackLayouts) });
        }
        return result;
    }

    private static string? Known(string? value, string[] allowed)
    {
        var trimmed = value?.Trim();
        return trimmed is not null && Array.IndexOf(allowed, trimmed) >= 0 ? trimmed : null;
    }

    // A parent naming a group this pass dropped, or one whose chain leads back
    // to the group itself, is unplaced. A hardware group id is never in the
    // incoming set, so it passes through: only the client knows those.
    private static void UnplaceDanglingParents(IReadOnlyList<DeviceGroup> incoming, List<DeviceGroup> kept)
    {
        var sent = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in incoming) sent.Add((group.Id ?? "").Trim());
        var byId = new Dictionary<string, DeviceGroup>(StringComparer.Ordinal);
        foreach (var group in kept) byId[group.Id] = group;

        foreach (var group in kept)
        {
            if (group.Parent is null) continue;
            if (sent.Contains(group.Parent) && !byId.ContainsKey(group.Parent)) { group.Parent = null; continue; }
            var seen = new HashSet<string>(StringComparer.Ordinal) { group.Id };
            var cursor = group.Parent;
            while (cursor is not null && byId.TryGetValue(cursor, out var next))
            {
                if (!seen.Add(cursor)) { group.Parent = null; break; }
                cursor = next.Parent;
            }
        }
    }
}
