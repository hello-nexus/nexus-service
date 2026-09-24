using System;
using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// How a stack's members share the one canvas frame they all carry. Overlap
/// samples every member over the whole frame; parallel cuts the frame into
/// equal bands top to bottom and series into equal columns left to right, each
/// member taking the slot its position in the stack names. The cut is made on
/// the unturned rect; the frame's rotation then turns the slots with it.
/// Mirrors nexus-web's stackSlots.ts, which places the canvas LED dots the same
/// way. Change them together.
/// </summary>
public static class StackSlots
{
    public readonly record struct Slot(string Layout, int Index, int Count);

    public static bool IsLaidOut(string? layout) => layout is "parallel" or "series";

    /// <summary>The part of the unturned frame <paramref name="slot"/> samples; the whole frame when the stack overlaps.</summary>
    public static (float X, float Y, float W, float H) Slice(float x, float y, float w, float h, Slot slot)
    {
        if (!IsLaidOut(slot.Layout) || slot.Count < 2) return (x, y, w, h);
        var share = 1f / slot.Count;
        return slot.Layout == "parallel"
            ? (x, y + slot.Index * share * h, w, share * h)
            : (x + slot.Index * share * w, y, share * w, h);
    }

    /// <summary>Every device's slot, keyed by device id. An overlap stack contributes nothing: its members sample the whole frame.</summary>
    public static Dictionary<string, Slot> Index(IReadOnlyList<DeviceGroup> stacks)
    {
        var slots = new Dictionary<string, Slot>(StringComparer.Ordinal);
        foreach (var stack in stacks)
        {
            if (!IsLaidOut(stack.Layout) || stack.Members.Count < 2) continue;
            for (var i = 0; i < stack.Members.Count; i++)
            {
                slots[stack.Members[i]] = new Slot(stack.Layout!, i, stack.Members.Count);
            }
        }
        return slots;
    }
}
