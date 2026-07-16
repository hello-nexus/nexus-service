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
    /// <summary>"none" | "flipBoth" | "mirrorXRot90" - see StreamDeckModel.Transform.</summary>
    public string Transform { get; set; } = "";
    /// <summary>Persisted value, 0-100.</summary>
    public int Brightness { get; set; }
    /// <summary>User rotation, in quarter-turn degree steps.</summary>
    public int Orientation { get; set; }
    /// <summary>Seconds of no key input before the deck blanks; a non-positive value disables sleep-after.</summary>
    public int SleepAfterSeconds { get; set; }
    public string FirmwareVersion { get; set; } = "";
    /// <summary>"elgato-software-running" when Elgato's own app is contending for the deck; null otherwise.</summary>
    public string? Warning { get; set; }
    /// <summary>ConflictAppCatalog id to pass to POST /conflicts/kill when Warning is set; null otherwise.</summary>
    public string? ConflictAppId { get; set; }
    /// <summary>Current page index (0-based), same semantics as StreamDeckChangedFrame.Page; 0 when the deck has no tracked live state (disconnected).</summary>
    public int CurrentPage { get; set; }
    /// <summary>Current folder path, same semantics as StreamDeckChangedFrame.FolderPath; empty (root) when the deck has no tracked live state (disconnected).</summary>
    public List<int> FolderPath { get; set; } = new();
}

public sealed class GetStreamDecksResponse
{
    public List<StreamDeckSummaryDto> Decks { get; set; } = new();
}

/// <summary>POST /streamdeck/decks/{serial} - rename and/or set brightness/orientation/sleep-after. Any field may be omitted.</summary>
public sealed class UpdateStreamDeckBody
{
    public string? Name { get; set; }
    public int? Brightness { get; set; }
    /// <summary>Degrees; clamped to the nearest quarter-turn.</summary>
    public int? Orientation { get; set; }
    /// <summary>Seconds of no key input before the deck blanks; clamped to a non-negative value.</summary>
    public int? SleepAfterSeconds { get; set; }
}

/// <summary>Shared envelope for GET/PUT /streamdeck/decks/{serial}/config.</summary>
public sealed class StreamDeckConfigEnvelope
{
    public DeckConfig Config { get; set; } = new();
}

/// <summary>PUT /streamdeck/decks/{serial}/images/{slotPath}/{state} response.</summary>
public sealed class StreamDeckImageUploadResponse
{
    public string Hash { get; set; } = "";
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
}

public sealed class StreamDeckDevModelsResponse
{
    public List<StreamDeckDevModelDto> Models { get; set; } = new();
}

// ----- /streamdeck/decks/{serial}/presets -----

/// <summary>One deck preset's identity - GET .../presets never sends the config/imageRefs.</summary>
public sealed class DeckPresetDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class GetDeckPresetsResponse
{
    public List<DeckPresetDto> Presets { get; set; } = new();
    // Always serialize; null = no preset selected. WhenWritingNull would omit it.
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ActiveId { get; set; }
}

public sealed class CreateDeckPresetBody
{
    public string Name { get; set; } = "";
    /// <summary>When present, the preset is created from this config (deep-copied) instead of a snapshot of the deck's live config - the Elgato importer's path.</summary>
    public DeckConfig? Config { get; set; }
}

public sealed class CreateDeckPresetResponse
{
    public DeckPresetDto? Preset { get; set; }
    public string? ActiveId { get; set; }
}

public sealed class UpdateDeckPresetBody
{
    public string? Name { get; set; }
    public bool SaveCurrent { get; set; }
}

public sealed class SetActiveDeckPresetBody
{
    public string? Id { get; set; }
}

public sealed class DeleteDeckPresetResponse
{
    // Always serialize; null = no preset selected. WhenWritingNull would omit it.
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ActiveId { get; set; }
}
