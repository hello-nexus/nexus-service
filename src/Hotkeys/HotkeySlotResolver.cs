using System.Collections.Generic;
using Nexus.Service.Deck;
using Nexus.Service.Persistence;

namespace Nexus.Service.Hotkeys;

/// <summary>
/// Resolves a "deck" hotkey target to the DeckAction currently stored in the
/// preset slot or dial event that carries its tag, or null when the preset or
/// tag is gone or the tagged item no longer holds an action (the binding is then
/// orphaned). Search order is deterministic: per page, its slots in order with
/// each folder searched in place (folder slots, then folder dials), then the
/// page's dials in order with each stack entry after its dial; the first match wins.
/// </summary>
public static class HotkeySlotResolver
{
    public static DeckAction? Resolve(HotkeyTarget target, StreamDeckSettings deck)
    {
        if (target.Kind != HotkeyTarget.KindDeck || string.IsNullOrEmpty(target.PresetId) || string.IsNullOrEmpty(target.Tag))
        {
            return null;
        }
        var preset = deck.Presets.Find(p => p.Id == target.PresetId);
        if (preset is null)
        {
            return null;
        }
        foreach (var page in preset.Deck.Pages)
        {
            if (FindInSlots(page.Slots, target.Tag, out var found) || FindInDials(page.Dials, target.Tag, out found))
            {
                return found;
            }
        }
        return null;
    }

    // True when the tag is present at all; `action` is then the item's current action or null when it holds none.
    private static bool FindInSlots(List<DeckSlot> slots, string tag, out DeckAction? action)
    {
        foreach (var slot in slots)
        {
            if (slot.HotkeyTag == tag)
            {
                action = slot.Folder is null && slot.Action is { Type.Length: > 0 } a ? a : null;
                return true;
            }
            if (slot.Folder is { } folder
                && (FindInSlots(folder.Slots, tag, out action) || FindInDials(folder.Dials, tag, out action)))
            {
                return true;
            }
        }
        action = null;
        return false;
    }

    private static bool FindInDials(List<DeckDial>? dials, string tag, out DeckAction? action)
    {
        action = null;
        if (dials is null)
        {
            return false;
        }
        foreach (var dial in dials)
        {
            if (FindInDial(dial, tag, out action))
            {
                return true;
            }
            // Entries only run while the stack is active (two or more).
            if (dial.Stack is not { Count: >= 2 })
            {
                continue;
            }
            foreach (var entry in dial.Stack)
            {
                if (FindInDial(entry, tag, out action))
                {
                    return true;
                }
            }
        }
        action = null;
        return false;
    }

    private static bool FindInDial(DeckDial dial, string tag, out DeckAction? action)
    {
        action = null;
        // A stacked dial runs its entries and ignores its own action.
        if (dial.Stack is { Count: >= 2 } || dial.Action is not { Type: "custom" } custom || custom.HotkeyTags is not { } tags)
        {
            return false;
        }
        DeckAction? candidate;
        if (tags.Push == tag)
        {
            candidate = custom.Push;
        }
        else if (tags.TurnLeft == tag)
        {
            candidate = custom.TurnLeft;
        }
        else if (tags.TurnRight == tag)
        {
            candidate = custom.TurnRight;
        }
        else if (tags.Touch == tag)
        {
            candidate = custom.Touch ?? custom.Push;
        }
        else
        {
            return false;
        }
        action = candidate is { Type.Length: > 0 } ? candidate : null;
        return true;
    }
}
