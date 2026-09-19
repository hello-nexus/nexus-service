using System;
using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Deck;

/// <summary>physical reserves key 0 for Back at folder depth >= 1 (DeckConfigNavigation.FitToGrid); widget does not.</summary>
public enum DeckTargetKind { Physical, Widget }

/// <summary>
/// Walks a <see cref="DeckConfig"/> tree by page index + folder path /
/// dot-joined slot path, mirroring nexus-web's <c>deckLayout.ts</c>
/// resolution rules. Shared by the connection worker (physical key -> slot)
/// and the routes (test-press, image slot addressing). ParseSlotPath,
/// BuildSlotPath, ResolveView and ResolveSlot stay page-independent (the
/// page is always a separate argument) - that is the grammar test-press and
/// physical key resolution use. ParseImageRefSlotPath/BuildImageRefSlotPath
/// below are the separate, page-qualified grammar the ImageRefs wire
/// contract (v2) uses instead: the page leads the same dot-chain as its own
/// new first segment, matching nexus-web's deckImageSlotPath.
/// </summary>
public static class DeckConfigNavigation
{
    /// <summary>
    /// The slot list at the given page + folder path, or null when the page
    /// is out of range or the folder path no longer resolves (a folder was
    /// removed or replaced by a leaf action).
    /// </summary>
    public static List<DeckSlot>? ResolveView(DeckConfig config, int page, IReadOnlyList<int> folderPath)
    {
        if (page < 0 || page >= config.Pages.Count)
        {
            return null;
        }
        var slots = config.Pages[page].Slots;
        foreach (var idx in folderPath)
        {
            if (idx < 0 || idx >= slots.Count)
            {
                return null;
            }
            var folder = slots[idx].Folder;
            if (folder is null)
            {
                return null;
            }
            slots = folder.Slots;
        }
        return slots;
    }

    /// <summary>The slot at a dot-joined index path from a page's root, or null if it does not resolve.</summary>
    public static DeckSlot? ResolveSlot(DeckConfig config, int page, IReadOnlyList<int> indices)
    {
        if (indices.Count == 0)
        {
            return null;
        }
        var parentPath = new List<int>(indices.Count - 1);
        for (var i = 0; i < indices.Count - 1; i++)
        {
            parentPath.Add(indices[i]);
        }
        var view = ResolveView(config, page, parentPath);
        if (view is null)
        {
            return null;
        }
        var last = indices[^1];
        return last >= 0 && last < view.Count ? view[last] : null;
    }

    /// <summary>Parses a route's dot-joined slotPath ("3" or "2.5") into indices, or null if malformed.</summary>
    public static List<int>? ParseSlotPath(string slotPath)
    {
        var parts = slotPath.Split('.');
        var result = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var idx) || idx < 0)
            {
                return null;
            }
            result.Add(idx);
        }
        return result.Count > 0 ? result : null;
    }

    /// <summary>Builds the dot-joined slotPath for a slot at folderPath + slotIndex.</summary>
    public static string BuildSlotPath(IReadOnlyList<int> folderPath, int slotIndex)
    {
        if (folderPath.Count == 0)
        {
            return slotIndex.ToString();
        }
        var sb = new System.Text.StringBuilder();
        foreach (var idx in folderPath)
        {
            sb.Append(idx).Append('.');
        }
        sb.Append(slotIndex);
        return sb.ToString();
    }

    /// <summary>
    /// Splits a page-qualified ImageRefs slot path ("0.3", "2.1.5" - the
    /// page leads the dot-chain) into the page index plus the page-relative
    /// folder path and slot index that ResolveView/ResolveSlot expect. The
    /// reserved "back" key never reaches this - callers special-case it
    /// first, since it stays page-independent. Returns null when the string
    /// does not parse into at least a page segment and a slot index (a
    /// legacy pre-v2 single-segment path has no page and is treated as an
    /// orphan, not resolved).
    /// </summary>
    public static (int Page, List<int> FolderPath, int SlotIndex)? ParseImageRefSlotPath(string slotPath)
    {
        var indices = ParseSlotPath(slotPath);
        if (indices is null || indices.Count < 2)
        {
            return null;
        }
        var page = indices[0];
        var folderPath = indices.GetRange(1, indices.Count - 2);
        var slotIndex = indices[^1];
        return (page, folderPath, slotIndex);
    }

    /// <summary>Builds the page-qualified ImageRefs slot path ("0.3", "2.1.5") for a slot at folderPath + slotIndex on the given page, matching nexus-web's deckImageSlotPath.</summary>
    public static string BuildImageRefSlotPath(int page, IReadOnlyList<int> folderPath, int slotIndex) =>
        $"{page}.{BuildSlotPath(folderPath, slotIndex)}";

    /// <summary>Inner icon-grid dimensions for a Deck widget's panel size, matching nexus-web's innerGridForSize (deckLayout.ts).</summary>
    public static (int Cols, int Rows) InnerGridForSize(string size) => size switch
    {
        "4x4" => (4, 4),
        "4x2" => (4, 2),
        "2x4" => (2, 4),
        _ => (2, 2),
    };

    /// <summary>Cap on fitted pages a single overflowing authored page can expand into, guarding against a pathological (near-zero key count) target.</summary>
    private const int MaxFittedPagesPerAuthoredPage = 64;

    /// <summary>True when a slot carries anything a trailing-empty trim or fit-capacity truncation must preserve, matching nexus-web's deckLayout.ts slotHasContent (color alone does not count).</summary>
    public static bool SlotHasContent(DeckSlot slot) =>
        slot.Action is not null || slot.Folder is not null || slot.Icon is not null || !string.IsNullOrEmpty(slot.Label);

    /// <summary>True when any page of a config has a slot with content, recursing into folders - matching nexus-web's deckLayout.ts pageHasContent. Used by migrations deciding whether a legacy live config is worth hoisting into a preset.</summary>
    public static bool HasContent(DeckConfig config) =>
        config.Pages.Any(page => page.Slots.Any(SlotHasContentRecursive));

    private static bool SlotHasContentRecursive(DeckSlot slot) =>
        SlotHasContent(slot) || (slot.Folder?.Slots.Any(SlotHasContentRecursive) ?? false);

    /// <summary>
    /// Projects a host-wide preset (authored at presetCols x presetRows) onto a
    /// concrete instance grid: same key count and folder-reservation rules as
    /// the preset's own grid returns the preset's config unchanged (same
    /// reference - callers must treat it as read-only); otherwise each
    /// authored page is trimmed of trailing empty slots, padded when it fits
    /// the target key count, or chunked across synthetic next/prev pages
    /// (DeckSlot.Auto) when it does not. Folders are trimmed and truncated to
    /// the target's own folder capacity (slotCountAtDepth semantics) rather
    /// than paginated - DeckFolder has no page axis to chunk onto. Never
    /// persisted: callers resolve this fresh from the stored preset on every
    /// read.
    /// </summary>
    public static DeckConfig FitToGrid(int presetCols, int presetRows, DeckConfig presetDeck, int targetCols, int targetRows, DeckTargetKind kind)
    {
        var targetKeyCount = targetCols * targetRows;
        if (targetKeyCount <= 0)
        {
            return new DeckConfig();
        }
        if (presetCols * presetRows == targetKeyCount)
        {
            return presetDeck;
        }

        var outPages = new List<DeckPage>();
        foreach (var page in presetDeck.Pages)
        {
            var trimmed = TrimTrailingEmpty(page.Slots);
            foreach (var chunk in ChunkRoot(trimmed, targetKeyCount))
            {
                var fitted = chunk.Select(slot => FitSlot(slot, kind, targetKeyCount, depth: 0)).ToList();
                outPages.Add(new DeckPage { Slots = PadTo(fitted, targetKeyCount) });
            }
        }
        if (outPages.Count == 0)
        {
            outPages.Add(new DeckPage { Slots = PadTo(new List<DeckSlot>(), targetKeyCount) });
        }
        return new DeckConfig { Pages = outPages, DefaultTitleStyle = presetDeck.DefaultTitleStyle };
    }

    /// <summary>Removes trailing slots with no content (SlotHasContent false); interior gaps are kept.</summary>
    private static List<DeckSlot> TrimTrailingEmpty(List<DeckSlot> slots)
    {
        var end = slots.Count;
        while (end > 0 && !SlotHasContent(slots[end - 1]))
        {
            end--;
        }
        return slots.Take(end).ToList();
    }

    private static List<DeckSlot> PadTo(List<DeckSlot> slots, int count)
    {
        var result = slots.Take(count).ToList();
        while (result.Count < count)
        {
            result.Add(new DeckSlot());
        }
        return result;
    }

    private static DeckSlot NavKey(string op) => new() { Action = new DeckAction { Type = "page", Op = op }, Auto = true };

    /// <summary>
    /// Splits an authored (already trimmed) root slot list into one or more
    /// fitted pages of exactly targetKeyCount slots each. A list that already
    /// fits is returned as its own single chunk (padding happens in the
    /// caller). An overflowing list reserves the last slot of every
    /// non-final chunk for a "next" key and the first slot of every
    /// non-initial chunk for a "prev" key, per the FitToGrid contract.
    /// </summary>
    private static List<List<DeckSlot>> ChunkRoot(List<DeckSlot> slots, int targetKeyCount)
    {
        if (slots.Count <= targetKeyCount)
        {
            return new List<List<DeckSlot>> { slots };
        }
        // Fewer than 2 keys leaves no room for a nav key; degrade to a flat,
        // un-navigable split rather than looping forever on a zero-progress chunk.
        if (targetKeyCount < 2)
        {
            var flat = new List<List<DeckSlot>>();
            for (var i = 0; i < slots.Count && flat.Count < MaxFittedPagesPerAuthoredPage; i += targetKeyCount)
            {
                flat.Add(slots.Skip(i).Take(targetKeyCount).ToList());
            }
            return flat;
        }

        var chunks = new List<List<DeckSlot>>();
        var idx = 0;
        var isFirst = true;
        while (idx < slots.Count && chunks.Count < MaxFittedPagesPerAuthoredPage)
        {
            var remaining = slots.Count - idx;
            var isLast = remaining <= targetKeyCount - 1;
            if (isLast)
            {
                var chunk = new List<DeckSlot>();
                if (!isFirst)
                {
                    chunk.Add(NavKey("prev"));
                }
                chunk.AddRange(slots.GetRange(idx, remaining));
                idx += remaining;
                chunks.Add(chunk);
            }
            else
            {
                var take = isFirst ? targetKeyCount - 1 : targetKeyCount - 2;
                var chunk = new List<DeckSlot>();
                if (!isFirst)
                {
                    chunk.Add(NavKey("prev"));
                }
                chunk.AddRange(slots.GetRange(idx, take));
                chunk.Add(NavKey("next"));
                idx += take;
                chunks.Add(chunk);
            }
            isFirst = false;
        }
        return chunks;
    }

    /// <summary>Fits one slot for display at the given depth: a leaf/action slot passes through verbatim, a folder's own slots are trimmed and truncated (not paginated) to the target's folder capacity at depth+1.</summary>
    private static DeckSlot FitSlot(DeckSlot slot, DeckTargetKind kind, int targetKeyCount, int depth)
    {
        if (slot.Folder is null)
        {
            return slot;
        }
        var capacity = kind == DeckTargetKind.Physical ? Math.Max(0, targetKeyCount - 1) : targetKeyCount;
        var trimmed = TrimTrailingEmpty(slot.Folder.Slots);
        var limited = trimmed.Take(capacity).Select(s => FitSlot(s, kind, targetKeyCount, depth + 1)).ToList();
        return new DeckSlot
        {
            Icon = slot.Icon,
            Label = slot.Label,
            Color = slot.Color,
            Title = slot.Title,
            Action = slot.Action,
            Auto = slot.Auto,
            Folder = new DeckFolder { Slots = PadTo(limited, capacity) },
        };
    }

    /// <summary>Deep-copies a DeckConfig by round-tripping it through DeckConfigConverter - the tree nests DeckFolder/DeckSlot/DeckAction/DeckSequenceStep, so a hand-rolled clone would have to mirror every branch of that converter.</summary>
    public static DeckConfig DeepCopyConfig(DeckConfig source) =>
        System.Text.Json.JsonSerializer.Deserialize(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(source, Nexus.Service.Serialization.AppJsonContext.Default.DeckConfig),
            Nexus.Service.Serialization.AppJsonContext.Default.DeckConfig)!;
}
