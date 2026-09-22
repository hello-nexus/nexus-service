using System.Collections.Generic;

namespace Nexus.Service.Deck;

/// <summary>Resolves a package's per-platform hotkeys into the single Keys a stored preset carries.</summary>
public static class DeckPlatformKeys
{
    /// <summary>Mutates deck in place: on macOS every action with a KeysMac takes it as Keys; KeysMac is cleared on every platform.</summary>
    public static void ApplyHost(DeckConfig deck, bool mac)
    {
        foreach (var page in deck.Pages)
        {
            ApplySlots(page.Slots, mac);
        }
    }

    private static void ApplySlots(List<DeckSlot> slots, bool mac)
    {
        foreach (var slot in slots)
        {
            if (slot.Action is not null)
            {
                ApplyAction(slot.Action, mac);
            }
            if (slot.Folder is not null)
            {
                ApplySlots(slot.Folder.Slots, mac);
            }
        }
    }

    private static void ApplyAction(DeckAction action, bool mac)
    {
        if (mac && !string.IsNullOrEmpty(action.KeysMac))
        {
            action.Keys = action.KeysMac;
        }
        action.KeysMac = null;
        if (action.On is not null)
        {
            ApplyAction(action.On, mac);
        }
        if (action.Off is not null)
        {
            ApplyAction(action.Off, mac);
        }
        if (action.Steps is not null)
        {
            foreach (var step in action.Steps)
            {
                ApplyAction(step.Action, mac);
            }
        }
    }
}
