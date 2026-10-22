using System.Collections.Generic;
using Nexus.Service.Deck;

namespace Nexus.Service.Hotkeys;

/// <summary>Machine-wide global hotkeys. Not profile-scoped: a profile switch never touches it.</summary>
public sealed class HotkeySettings
{
    public List<HotkeyBinding> Bindings { get; set; } = new();
    /// <summary>Set once the built-in bindings have been seeded, so a deleted built-in stays deleted until reset-defaults.</summary>
    public bool DefaultsSeeded { get; set; }
}

/// <summary>One global hotkey: a combo bound to a target.</summary>
public class HotkeyBinding
{
    public string Id { get; set; } = "";
    /// <summary>Canonical combo text (see <see cref="HotkeyCombo"/>); null when unbound.</summary>
    public string? Combo { get; set; }
    public bool Enabled { get; set; } = true;
    public string? Label { get; set; }
    public HotkeyTarget Target { get; set; } = new();
}

/// <summary>What a hotkey runs. Kind "action" carries a DeckAction; kind "deck" points at a slot of an authored deck preset.</summary>
public sealed class HotkeyTarget
{
    public const string KindAction = "action";
    public const string KindDeck = "deck";

    public string Kind { get; set; } = "";
    public DeckAction? Action { get; set; }
    public string? PresetId { get; set; }
    public HotkeySlotAddress? Slot { get; set; }
}

/// <summary>
/// Identifies a key, or a dial plus event, in the AUTHORED grid of a deck preset.
/// Indices are positions in the authored lists, not physical positions after
/// DeckConfigNavigation.FitToGrid.
/// </summary>
public sealed class HotkeySlotAddress
{
    public const string TypeKey = "key";
    public const string TypeDial = "dial";

    /// <summary>"key" or "dial".</summary>
    public string Type { get; set; } = "";
    public int Page { get; set; }
    /// <summary>key: slot index at each depth; every entry but the last is a folder slot.</summary>
    public List<int>? Path { get; set; }
    /// <summary>dial: folder slot indices from the page root; empty or absent means the page's own dials.</summary>
    public List<int>? FolderPath { get; set; }
    /// <summary>dial: index in the resolved dials list.</summary>
    public int? Dial { get; set; }
    /// <summary>dial: index into the dial's stack when it is a stacked dial.</summary>
    public int? Stack { get; set; }
    /// <summary>dial: push | turnLeft | turnRight | touch.</summary>
    public string? Event { get; set; }
}

/// <summary>A binding as the API returns it: the stored fields plus its live registration status.</summary>
public sealed class HotkeyBindingView : HotkeyBinding
{
    /// <summary>active | disabled | unbound | conflict | orphaned | unsupported.</summary>
    public string Status { get; set; } = HotkeyStatus.Unbound;
}

/// <summary>GET /hotkeys body, and the payload of the "hotkeys" WebSocket topic.</summary>
public sealed class HotkeysResponse
{
    public bool Supported { get; set; }
    /// <summary>macos | windows | linux.</summary>
    public string Platform { get; set; } = "";
    public bool Suspended { get; set; }
    public List<HotkeyBindingView> Bindings { get; set; } = new();
}

public sealed class HotkeySuspendBody
{
    public bool Suspended { get; set; }
}

public static class HotkeyStatus
{
    public const string Active = "active";
    public const string Disabled = "disabled";
    public const string Unbound = "unbound";
    public const string Conflict = "conflict";
    public const string Orphaned = "orphaned";
    public const string Unsupported = "unsupported";
}
