using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Persistence;

namespace Nexus.Service.Deck;

/// <summary>One key of a Recent Apps view page: an app entry, an auto page-nav key, or a blank filler.</summary>
public sealed class RecentKey
{
    /// <summary>app | navNext | navPrev | blank.</summary>
    public string Kind { get; set; } = "blank";
    public string? ProcessKey { get; set; }
    public string? Name { get; set; }
    public string? ShortcutId { get; set; }
    public string? ExePath { get; set; }
    public bool Focused { get; set; }
}

/// <summary>
/// Pure MRU ring maintenance and view layout for Deck Modes' Recent Apps
/// mode. Ported field-for-field to nexus-web's recentAppsView.ts against the
/// shared tests/Deck/recentAppsView.vectors.json vectors. Never touches
/// IConfigStore or any provider itself - RecentAppsService owns persistence
/// and focus wiring.
/// </summary>
public static class RecentAppsTracker
{
    /// <summary>Cap enforced here so every ring writer (RecentAppsService, the routes) gets it for free.</summary>
    public const int RingCap = 64;

    /// <summary>Auto page-nav pages a Recent Apps view splits into before dropping the rest of the ring.</summary>
    private const int MaxPages = 4;

    /// <summary>Built-in shell/desktop processes never tracked, regardless of RecentAppsExcluded.</summary>
    public static readonly IReadOnlyCollection<string> BuiltInDenylist = new HashSet<string>(StringComparer.Ordinal)
    {
        "nexus", "nexus-overlay", "explorer", "searchhost", "startmenuexperiencehost", "shellexperiencehost",
        "lockapp", "applicationframehost", "finder", "dock", "loginwindow", "spotlight", "control center",
        "notification center", "wallpaper", "systemuiserver", "screensaverengine", "plasmashell", "kwin_wayland", "kwin_x11",
    };

    public static bool IsExcluded(string processKey, IReadOnlyCollection<string> excluded) =>
        BuiltInDenylist.Contains(processKey) || excluded.Contains(processKey);

    /// <summary>
    /// Moves candidate to the front of ring by ProcessKey, preserving an
    /// already-resolved ShortcutId across the move (RecentAppsService resolves
    /// off the focus thread, on its broadcast timer), dedupes, and caps at
    /// RingCap.
    /// No-op (returns false, ring untouched) for an empty or excluded process
    /// key.
    /// </summary>
    public static bool UpdateRing(List<RecentApp> ring, RecentApp candidate, IReadOnlyCollection<string> excluded)
    {
        if (string.IsNullOrEmpty(candidate.ProcessKey) || IsExcluded(candidate.ProcessKey, excluded))
        {
            return false;
        }
        var existingIndex = ring.FindIndex(a => a.ProcessKey == candidate.ProcessKey);
        if (existingIndex >= 0)
        {
            var existing = ring[existingIndex];
            if (candidate.ShortcutId is null && existing.ShortcutId is not null)
            {
                // A focus event only knows the process name; the resolved
                // Start-menu entry (id + label) outranks it.
                candidate.ShortcutId = existing.ShortcutId;
                candidate.Name = existing.Name;
            }
            ring.RemoveAt(existingIndex);
        }
        ring.Insert(0, candidate);
        while (ring.Count > RingCap)
        {
            ring.RemoveAt(ring.Count - 1);
        }
        return true;
    }

    /// <summary>
    /// Lays the ring out onto cols x rows keys: focused entry first (if it
    /// resolves in the ring) rendered with Focused=true, then the rest in MRU
    /// order. A view that fits in one page pads with blank keys; an
    /// overflowing one paginates with FitToGrid-style next/prev reservations,
    /// capped at MaxPages - entries beyond the cap are dropped.
    /// </summary>
    public static List<List<RecentKey>> BuildView(IReadOnlyList<RecentApp> ring, string? focusedProcessKey, int cols, int rows)
    {
        var targetKeyCount = cols * rows;
        if (targetKeyCount <= 0)
        {
            return new List<List<RecentKey>>();
        }

        var focused = focusedProcessKey is null ? null : ring.FirstOrDefault(a => a.ProcessKey == focusedProcessKey);
        var ordered = new List<RecentApp>(ring.Count);
        if (focused is not null)
        {
            ordered.Add(focused);
        }
        foreach (var app in ring)
        {
            if (focused is not null && app.ProcessKey == focused.ProcessKey)
            {
                continue;
            }
            ordered.Add(app);
        }

        if (ordered.Count <= targetKeyCount)
        {
            var page = ordered.Select(a => ToAppKey(a, focused)).ToList();
            PadTo(page, targetKeyCount);
            return new List<List<RecentKey>> { page };
        }

        if (targetKeyCount < 2)
        {
            var flat = new List<List<RecentKey>>();
            for (var i = 0; i < ordered.Count && flat.Count < MaxPages; i += targetKeyCount)
            {
                flat.Add(ordered.Skip(i).Take(targetKeyCount).Select(a => ToAppKey(a, focused)).ToList());
            }
            return flat;
        }

        // Truncating to exactly what MaxPages of targetKeyCount (with next/prev
        // reservations) can hold up front means the chunk loop below always
        // finds its true last page on its own - it never needs to cut a page
        // short at the cap and leave a dangling "next" key with nothing after it.
        var maxCapacity = 2 * (targetKeyCount - 1) + (MaxPages - 2) * (targetKeyCount - 2);
        var truncated = ordered.Take(Math.Min(ordered.Count, maxCapacity)).ToList();

        var pages = new List<List<RecentKey>>();
        var idx = 0;
        var isFirst = true;
        while (idx < truncated.Count && pages.Count < MaxPages)
        {
            var remaining = truncated.Count - idx;
            var isLast = remaining <= targetKeyCount - 1;
            var page = new List<RecentKey>();
            if (!isFirst)
            {
                page.Add(NavKey("navPrev"));
            }
            var take = isLast ? remaining : (isFirst ? targetKeyCount - 1 : targetKeyCount - 2);
            for (var i = 0; i < take; i++)
            {
                page.Add(ToAppKey(truncated[idx + i], focused));
            }
            idx += take;
            if (!isLast)
            {
                page.Add(NavKey("navNext"));
            }
            PadTo(page, targetKeyCount);
            pages.Add(page);
            isFirst = false;
        }
        return pages;
    }

    private static RecentKey ToAppKey(RecentApp app, RecentApp? focused) => new()
    {
        Kind = "app",
        ProcessKey = app.ProcessKey,
        Name = app.Name,
        ShortcutId = app.ShortcutId,
        ExePath = app.ExePath,
        Focused = focused is not null && app.ProcessKey == focused.ProcessKey,
    };

    private static RecentKey NavKey(string kind) => new() { Kind = kind };

    private static void PadTo(List<RecentKey> page, int count)
    {
        while (page.Count < count)
        {
            page.Add(new RecentKey { Kind = "blank" });
        }
    }
}
