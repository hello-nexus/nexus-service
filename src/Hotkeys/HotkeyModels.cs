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
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public string? Combo { get; set; }
    public bool Enabled { get; set; } = true;
    public string? Label { get; set; }
    public HotkeyTarget Target { get; set; } = new();
}

/// <summary>What a hotkey runs. Kind "action" carries a DeckAction; kind "deck" names the key or dial event in a deck preset that carries Tag.</summary>
public sealed class HotkeyTarget
{
    public const string KindAction = "action";
    public const string KindDeck = "deck";

    public string Kind { get; set; } = "";
    public DeckAction? Action { get; set; }
    public string? PresetId { get; set; }
    /// <summary>The DeckSlot.HotkeyTag or DeckDialHotkeyTags value to find in the preset; equals the binding id.</summary>
    public string? Tag { get; set; }
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

/// <summary>4xx body of the hotkey routes: the usual envelope plus a stable machine-readable code.</summary>
public sealed class HotkeyErrorResponse
{
    public bool Error { get; set; } = true;
    public string Msg { get; set; } = "";
    /// <summary>invalidId | invalidCombo | invalidTarget | idMismatch | tooMany | notFound.</summary>
    public string Code { get; set; } = "";
}

public sealed class HotkeySuspendBody
{
    public bool Suspended { get; set; }
}

public static class HotkeyErrorCodes
{
    public const string InvalidId = "invalidId";
    public const string InvalidCombo = "invalidCombo";
    public const string InvalidTarget = "invalidTarget";
    public const string IdMismatch = "idMismatch";
    public const string TooMany = "tooMany";
    public const string NotFound = "notFound";
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
