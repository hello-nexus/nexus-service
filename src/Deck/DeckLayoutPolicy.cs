using System;
using System.Collections.Generic;
using System.Text.Json;
using Nexus.Service.Models.Panel;
using Nexus.Service.Serialization;

namespace Nexus.Service.Deck;

/// <summary>
/// What a panel session may do with the deck keys in its own layout. A paired
/// panel presses keys; it does not get to author the ones that name a file, a
/// key chord, text, or an audio file, because the service executes those in
/// the console user's session and that is code execution as the user. Such
/// keys are authored from the desktop app and only triggered from a panel,
/// through <c>POST /panel/deck/dispatch</c> (PanelDeckRoutes).
/// </summary>
public static class DeckLayoutPolicy
{
    public const string DeckWidgetType = "deck";
    private const string DeckConfigKey = "deck";

    private static readonly HashSet<string> PrivilegedTypes = new(StringComparer.Ordinal)
    {
        "openFile", "openFolder", "hotkey", "hotkeySwitch", "text", "playAudio",
    };

    /// <summary>The deck config a deck widget carries under <c>config.deck</c>, or null for any other widget.</summary>
    public static DeckConfig? ReadDeckConfig(PanelWidgetDto widget)
    {
        if (!string.Equals(widget.Type, DeckWidgetType, StringComparison.Ordinal))
        {
            return null;
        }
        if (widget.Config is null || !widget.Config.TryGetValue(DeckConfigKey, out var raw) || raw.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize(raw, AppJsonContext.Default.DeckConfig);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when <paramref name="incoming"/> carries a privileged action that
    /// <paramref name="stored"/> does not already hold verbatim. Rearranging,
    /// copying, relabelling or deleting existing keys stays allowed; authoring a
    /// new file / hotkey / text / audio key, or changing what one does, does not.
    /// </summary>
    public static bool IntroducesPrivilegedActions(PanelLayoutDto? incoming, PanelLayoutDto? stored)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in PrivilegedActions(stored))
        {
            known.Add(Serialize(action));
        }
        foreach (var action in PrivilegedActions(incoming))
        {
            if (!known.Contains(Serialize(action)))
            {
                return true;
            }
        }
        return false;
    }

    public static IEnumerable<DeckAction> PrivilegedActions(PanelLayoutDto? layout)
    {
        if (layout is null)
        {
            yield break;
        }
        foreach (var page in layout.Pages)
        {
            foreach (var widget in page.Widgets)
            {
                var config = ReadDeckConfig(widget);
                if (config is null)
                {
                    continue;
                }
                foreach (var deckPage in config.Pages)
                {
                    foreach (var action in PrivilegedActions(deckPage.Slots))
                    {
                        yield return action;
                    }
                }
            }
        }
    }

    private static IEnumerable<DeckAction> PrivilegedActions(List<DeckSlot> slots)
    {
        foreach (var slot in slots)
        {
            foreach (var action in PrivilegedActions(slot.Action))
            {
                yield return action;
            }
            if (slot.Folder is not null)
            {
                foreach (var action in PrivilegedActions(slot.Folder.Slots))
                {
                    yield return action;
                }
            }
        }
    }

    private static IEnumerable<DeckAction> PrivilegedActions(DeckAction? action)
    {
        if (action is null)
        {
            yield break;
        }
        if (PrivilegedTypes.Contains(action.Type))
        {
            yield return action;
            yield break;
        }
        if (action.Steps is not null)
        {
            foreach (var step in action.Steps)
            {
                foreach (var nested in PrivilegedActions(step.Action))
                {
                    yield return nested;
                }
            }
        }
        foreach (var nested in PrivilegedActions(action.On))
        {
            yield return nested;
        }
        foreach (var nested in PrivilegedActions(action.Off))
        {
            yield return nested;
        }
    }

    private static string Serialize(DeckAction action)
        => JsonSerializer.Serialize(action, AppJsonContext.Default.DeckAction);
}
