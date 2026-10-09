using System.Collections.Generic;

namespace Nexus.Service.Deck;

/// <summary>Walks dial trees (page and folder dials, stack entries, custom-action children) for the checks that otherwise only visit slots.</summary>
public static class DeckDials
{
    /// <summary>Every dial in the list plus its stack entries.</summary>
    public static IEnumerable<DeckDial> Flatten(IEnumerable<DeckDial>? dials)
    {
        if (dials is null)
        {
            yield break;
        }
        foreach (var dial in dials)
        {
            yield return dial;
            if (dial.Stack is null)
            {
                continue;
            }
            foreach (var entry in dial.Stack)
            {
                yield return entry;
            }
        }
    }

    /// <summary>The key actions a custom dial action runs.</summary>
    public static IEnumerable<DeckAction> CustomActions(DeckDial dial)
    {
        var action = dial.Action;
        if (action is null || action.Type != "custom")
        {
            yield break;
        }
        if (action.TurnRight is not null)
        {
            yield return action.TurnRight;
        }
        if (action.TurnLeft is not null)
        {
            yield return action.TurnLeft;
        }
        if (action.Push is not null)
        {
            yield return action.Push;
        }
        if (action.Touch is not null)
        {
            yield return action.Touch;
        }
    }

    /// <summary>Every key action reachable from the dials.</summary>
    public static IEnumerable<DeckAction> AllActions(IEnumerable<DeckDial>? dials)
    {
        foreach (var dial in Flatten(dials))
        {
            foreach (var action in CustomActions(dial))
            {
                yield return action;
            }
        }
    }
}
