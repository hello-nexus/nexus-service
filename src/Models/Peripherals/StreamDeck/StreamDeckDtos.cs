using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nexus.Service.Deck;

namespace Nexus.Service.Models.Peripherals.StreamDeck;

/// <summary>One detected (real or simulated) Stream Deck, for GET /streamdeck/decks.</summary>
public sealed class StreamDeckSummaryDto
{
    public string Serial { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>Persisted display name; falls back to the model name when unset.</summary>
    public string Name { get; set; } = "";
    public bool Connected { get; set; }
    public bool Verified { get; set; }
    public int Rows { get; set; }
    /// <summary>Wire name "cols" per the streamdeck-support.md contract, not "columns".</summary>
    [JsonPropertyName("cols")]
    public int Columns { get; set; }
    public int KeyCount { get; set; }
    public int KeyPixels { get; set; }
    /// <summary>"bmp" | "jpeg".</summary>
    public string Format { get; set; } = "";
    /// <summary>"none" | "flipBoth" | "mirrorXRot90" | "rot90Ccw" - see StreamDeckModel.Transform.</summary>
    public string Transform { get; set; } = "";
    /// <summary>Dial count; 0 without dials.</summary>
    public int Encoders { get; set; }
    /// <summary>"below" | "above" | "sides"; always serialized, null on a model without dials.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? DialPlacement { get; set; }
    /// <summary>Screen beyond the keys, in logical pixels; always serialized, null when the model has none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public StreamDeckScreenDto? Screen { get; set; }
    /// <summary>Capacitive touch keys after the LCD keys (Neo).</summary>
    public int TouchKeys { get; set; }
    /// <summary>LEDs per dial ring; 0 without rings.</summary>
    public int EncoderRingLeds { get; set; }
    /// <summary>Key bitmap size; KeyPixels stays the smaller edge.</summary>
    public int KeyWidth { get; set; }
    public int KeyHeight { get; set; }
    /// <summary>Neo info screen mode: clock | page | off. Omitted on other models.</summary>
    public string? InfoScreen { get; set; }
    /// <summary>Persisted value, 0-100.</summary>
    public int Brightness { get; set; }
    /// <summary>User rotation, in quarter-turn degree steps.</summary>
    public int Orientation { get; set; }
    /// <summary>Seconds of no key input before the deck blanks; a non-positive value disables sleep-after.</summary>
    public int SleepAfterSeconds { get; set; }
    public bool SleepWhenLocked { get; set; }
    public string FirmwareVersion { get; set; } = "";
    /// <summary>"elgato-software-running" when Elgato's own app is contending for the deck; null otherwise.</summary>
    public string? Warning { get; set; }
    /// <summary>ConflictAppCatalog id to pass to POST /conflicts/kill when Warning is set; null otherwise.</summary>
    public string? ConflictAppId { get; set; }
    /// <summary>Current page index (0-based), same semantics as StreamDeckChangedFrame.Page; 0 when the deck has no tracked live state (disconnected).</summary>
    public int CurrentPage { get; set; }
    /// <summary>Current folder path, same semantics as StreamDeckChangedFrame.FolderPath; empty (root) when the deck has no tracked live state (disconnected).</summary>
    public List<int> FolderPath { get; set; } = new();
    /// <summary>This deck's deck-instance id ("streamdeck:&lt;serial&gt;"), for GET/PUT /deck/instances/{id}.</summary>
    public string InstanceId { get; set; } = "";
}

/// <summary>A deck screen beyond the keys. Kind: touchStrip | infoScreen | dialScreen.</summary>
public sealed class StreamDeckScreenDto
{
    public int Width { get; set; }
    public int Height { get; set; }
    public string Kind { get; set; } = "";
}

public sealed class GetStreamDecksResponse
{
    public List<StreamDeckSummaryDto> Decks { get; set; } = new();
}

/// <summary>POST /streamdeck/decks/{serial} - rename and/or set brightness/orientation/sleep-after/sleep-when-locked. Any field may be omitted.</summary>
public sealed class UpdateStreamDeckBody
{
    public string? Name { get; set; }
    public int? Brightness { get; set; }
    /// <summary>Degrees; clamped to the nearest quarter-turn.</summary>
    public int? Orientation { get; set; }
    /// <summary>Seconds of no key input before the deck blanks; clamped to a non-negative value.</summary>
    public int? SleepAfterSeconds { get; set; }
    public bool? SleepWhenLocked { get; set; }
    /// <summary>Neo info screen mode: clock | page | off. Other values are ignored.</summary>
    public string? InfoScreen { get; set; }
}

/// <summary>Multiplex frame for the "streamdeck" topic.</summary>
public sealed class StreamDeckChangedFrame
{
    public long Revision { get; set; }
    /// <summary>"decks" | "config" | "nav" | "press" | "editRequest".</summary>
    public string Kind { get; set; } = "";
    public string? Serial { get; set; }
    /// <summary>Current page index, for "nav" and "editRequest".</summary>
    public int? Page { get; set; }
    /// <summary>Current folder path, for "nav", "press", and "editRequest".</summary>
    public List<int>? FolderPath { get; set; }
    /// <summary>Logical slot index within the current folder view, for "press" and "editRequest".</summary>
    public int? KeyIndex { get; set; }
    /// <summary>One-shot id (creation epoch ms) for "editRequest", so a client consumes each blank-key hold once across the live frame and the boot-time GET.</summary>
    public long? Token { get; set; }
    /// <summary>"editRequest" from a long touch on an empty dial segment: the editor selects this dial and ignores KeyIndex.</summary>
    public int? DialIndex { get; set; }
}

/// <summary>
/// Multiplex frame for the "streamdeckTiles" topic: one live monitoring or
/// weather key render, the same pixels pushed to the physical key. Broadcast
/// only while the topic has a subscriber. SlotPath is page-relative
/// (DeckConfigNavigation.BuildSlotPath - "3", "3.2"), never the page-prefixed
/// ImageRefs form.
/// </summary>
public sealed class StreamDeckTileFrame
{
    public string Serial { get; set; } = "";
    public int Page { get; set; }
    public string SlotPath { get; set; } = "";
    public string Mime { get; set; } = "image/jpeg";
    /// <summary>Base64-encoded JPEG, upright: no orientation or model wire transform applied.</summary>
    public string Data { get; set; } = "";
}

/// <summary>
/// A pending blank-key hold-to-edit intent, served by GET
/// /streamdeck/pending-edit so a freshly-opened dashboard can navigate to the
/// deck's editor and select the held key.
/// </summary>
public sealed class StreamDeckPendingEditDto
{
    public string Serial { get; set; } = "";
    public int Page { get; set; }
    public List<int> FolderPath { get; set; } = new();
    /// <summary>Logical slot index within the current folder view, same semantics as StreamDeckChangedFrame.KeyIndex.</summary>
    public int KeyIndex { get; set; }
    /// <summary>Matches the "editRequest" frame's Token; the client dedupes on it.</summary>
    public long Token { get; set; }
    /// <summary>Set for a long touch on an empty dial segment: the editor selects this dial and ignores KeyIndex.</summary>
    public int? DialIndex { get; set; }
}

/// <summary>GET /streamdeck/pending-edit envelope; Edit is null when no intent is pending (or the pending one has aged out).</summary>
public sealed class StreamDeckPendingEditResponse
{
    // Always serialize; null = nothing pending. WhenWritingNull would omit it.
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public StreamDeckPendingEditDto? Edit { get; set; }
}

public sealed class StreamDeckSimPressBody
{
    public int KeyIndex { get; set; }
    public bool Pressed { get; set; }
}

/// <summary>
/// POST /streamdeck/dev/sim-input body. Kind: rotate | dialDown | dialUp |
/// tap | longTouch | swipe | touchKey. Index is the dial for rotate/dialDown/
/// dialUp and the touch key for touchKey (with Pressed); Ticks is signed
/// (positive clockwise); X/Y is the touch point and X2/Y2 the swipe end.
/// </summary>
public sealed class StreamDeckSimInputBody
{
    public string Serial { get; set; } = "";
    public string Kind { get; set; } = "";
    public int? Index { get; set; }
    public int? Ticks { get; set; }
    public bool? Pressed { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }
    public int? X2 { get; set; }
    public int? Y2 { get; set; }
}

/// <summary>POST /streamdeck/dev/inject-report body: one raw input report (hex, report id first) fed to a real deck's input path.</summary>
public sealed class StreamDeckInjectReportBody
{
    public string Serial { get; set; } = "";
    public string Hex { get; set; } = "";
}

/// <summary>POST /streamdeck/dev/simulate body: picks the model the simulated deck presents as.</summary>
public sealed class StreamDeckSimulateBody
{
    public int ProductId { get; set; }
}

/// <summary>POST /streamdeck/decks/{serial}/nav body: the editor's current page + folder path, mirrored onto the deck.</summary>
public sealed class StreamDeckNavBody
{
    public int Page { get; set; }
    public List<int>? FolderPath { get; set; }
}

/// <summary>One entry of GET /streamdeck/dev/models, for the dev-tools model picker.</summary>
public sealed class StreamDeckDevModelDto
{
    public int ProductId { get; set; }
    public string Name { get; set; } = "";
    public int Rows { get; set; }
    [JsonPropertyName("cols")]
    public int Columns { get; set; }
    public int KeyCount { get; set; }
    public int Encoders { get; set; }
    /// <summary>Always serialized, null when the model has no screen beyond the keys.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public StreamDeckScreenDto? Screen { get; set; }
    public int TouchKeys { get; set; }
}

public sealed class StreamDeckDevModelsResponse
{
    public List<StreamDeckDevModelDto> Models { get; set; } = new();
}
