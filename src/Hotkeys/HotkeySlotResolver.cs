using System.Collections.Generic;
using Nexus.Service.Deck;
using Nexus.Service.Persistence;

namespace Nexus.Service.Hotkeys;

/// <summary>
/// Resolves a "deck" hotkey target to the DeckAction currently stored in the
/// authored preset, or null when the preset, page, folder, slot or dial no
/// longer resolves to an action (the binding is then orphaned).
/// </summary>
public static class HotkeySlotResolver
{
    public static DeckAction? Resolve(HotkeyTarget target, StreamDeckSettings deck)
    {
        if (target.Kind != HotkeyTarget.KindDeck || string.IsNullOrEmpty(target.PresetId) || target.Slot is null)
        {
            return null;
        }
        var preset = deck.Presets.Find(p => p.Id == target.PresetId);
        if (preset is null)
        {
            return null;
        }
        var slot = target.Slot;
        return slot.Type switch
        {
            HotkeySlotAddress.TypeKey => ResolveKey(preset.Deck, slot),
            HotkeySlotAddress.TypeDial => ResolveDial(preset.Deck, slot),
            _ => null,
        };
    }

    private static DeckAction? ResolveKey(DeckConfig config, HotkeySlotAddress slot)
    {
        if (slot.Path is not { Count: > 0 } path)
        {
            return null;
        }
        var found = DeckConfigNavigation.ResolveSlot(config, slot.Page, path);
        return found is { Folder: null, Action: { } action } && !string.IsNullOrEmpty(action.Type) ? action : null;
    }

    private static DeckAction? ResolveDial(DeckConfig config, HotkeySlotAddress slot)
    {
        if (slot.Page < 0 || slot.Page >= config.Pages.Count || slot.Dial is not { } dialIndex)
        {
            return null;
        }
        var page = config.Pages[slot.Page];
        var dials = page.Dials;
        var slots = page.Slots;
        foreach (var idx in slot.FolderPath ?? new List<int>())
        {
            if (idx < 0 || idx >= slots.Count || slots[idx].Folder is not { } folder)
            {
                return null;
            }
            slots = folder.Slots;
            if (folder.Dials is not null)
            {
                dials = folder.Dials;
            }
        }
        if (dials is null || dialIndex < 0 || dialIndex >= dials.Count)
        {
            return null;
        }
        var dial = dials[dialIndex];
        if (slot.Stack is { } stackIndex)
        {
            if (dial.Stack is not { } stack || stackIndex < 0 || stackIndex >= stack.Count)
            {
                return null;
            }
            dial = stack[stackIndex];
        }
        else if (dial.Stack is { Count: >= 2 })
        {
            // A stacked dial ignores its own action; the address must name an entry.
            return null;
        }
        if (dial.Action is not { Type: "custom" } custom)
        {
            return null;
        }
        var action = slot.Event switch
        {
            "push" => custom.Push,
            "turnLeft" => custom.TurnLeft,
            "turnRight" => custom.TurnRight,
            "touch" => custom.Touch ?? custom.Push,
            _ => null,
        };
        return action is not null && !string.IsNullOrEmpty(action.Type) ? action : null;
    }
}
