using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Nexus.Service.Deck;

/// <summary>
/// C# mirror of nexus-web's <c>panel/widgets/deck/types.ts</c> - the binding
/// model both the virtual touch deck widget and physical Stream Deck configs
/// share. Keep field-for-field in sync with that file. The wire shape is
/// byte-for-byte the TS union (nexus-web's api/streamdeck.ts round-trips the
/// native <c>DeckConfig</c> with no adapter), even though the reused JSON key
/// "action" (system/nexus/power) needs three distinct C# properties -
/// <see cref="SystemAction"/>, <see cref="NexusAction"/>,
/// <see cref="PowerAction"/> - handled by <see cref="DeckActionConverter"/>.
/// </summary>
[JsonConverter(typeof(DeckActionConverter))]
public sealed class DeckAction
{
    /// <summary>
    /// launchApp | openFile | openFolder | openUrl | system | hotkey | text |
    /// power | audioOutput | audioInput | nexus | sequence | toggle | page |
    /// pageIndicator | deckBrightness | deckSleep | hotkeySwitch | monitoring |
    /// weather | playAudio.
    /// </summary>
    public string Type { get; set; } = "";

    public string? AppId { get; set; }
    public string? Path { get; set; }
    public string? Url { get; set; }

    public DeckSystemAction? SystemAction { get; set; }
    public DeckNexusAction? NexusAction { get; set; }

    public string? Keys { get; set; }
    /// <summary>text: always pastes immediately (clipboard set + paste chord injection).</summary>
    public string? Text { get; set; }

    /// <summary>lock | sleep | shutdown | restart | logout.</summary>
    public string? PowerAction { get; set; }

    /// <summary>audioOutput / audioInput device id.</summary>
    public string? DeviceId { get; set; }

    public List<DeckSequenceStep>? Steps { get; set; }

    /// <summary>Branch taken when the toggle resolves on.</summary>
    public DeckAction? On { get; set; }
    /// <summary>Branch taken when the toggle resolves off.</summary>
    public DeckAction? Off { get; set; }
    public DeckToggleState? State { get; set; }

    /// <summary>page: next | prev | goto. deckBrightness: set | up | down.</summary>
    public string? Op { get; set; }
    /// <summary>page goto: 0-based target page index.</summary>
    public int? Target { get; set; }
    /// <summary>deckBrightness set: target percent, 0-100.</summary>
    public int? Value { get; set; }
    /// <summary>deckBrightness up/down: step percent; unset falls back to DeckActionExecutor.DeckBrightnessStep.</summary>
    public int? Step { get; set; }
    /// <summary>hotkeySwitch: first combo, same format as Keys.</summary>
    public string? KeysA { get; set; }
    /// <summary>hotkeySwitch: second combo, alternated with KeysA on each press.</summary>
    public string? KeysB { get; set; }

    /// <summary>monitoring: quick | cpu | gpu | memory | motherboard | storage.</summary>
    public string? Category { get; set; }
    /// <summary>monitoring: a concrete HardwareSensor.Id, never the "Temperature" preferred-temp sentinel.</summary>
    public string? Sensor { get; set; }
    /// <summary>monitoring: line | segments | backdrop | number. Legacy "radial" reads as segments, never written back.</summary>
    public string? Style { get; set; }
    /// <summary>monitoring: line/segments/backdrop accent hex color. Unset falls back to MonitoringTileRenderer.DefaultAccent.</summary>
    public string? Color { get; set; }
    /// <summary>monitoring: shows the sensor name label at top. Unset falls back to true.</summary>
    public bool? ShowName { get; set; }
    /// <summary>monitoring: none | taskManager | monitoringPage. Unset falls back to none.</summary>
    public string? Press { get; set; }

    /// <summary>monitoring: custom top label. Unset or empty falls back to the sensor's display name.</summary>
    public string? LabelText { get; set; }
    /// <summary>monitoring: adaptive | fixed. Unset falls back to adaptive, the existing pinned domain rules.</summary>
    public string? Scale { get; set; }
    /// <summary>monitoring: fixed-scale range floor. Ignored unless Scale is fixed and Max is a greater finite value.</summary>
    public double? Min { get; set; }
    /// <summary>monitoring: fixed-scale range ceiling. Ignored unless Scale is fixed and Min is a lesser finite value.</summary>
    public double? Max { get; set; }

    /// <summary>weather: manual location latitude. Unset falls back to IP geolocation.</summary>
    public double? Lat { get; set; }
    /// <summary>weather: manual location longitude. Unset falls back to IP geolocation.</summary>
    public double? Lon { get; set; }
    /// <summary>weather: manual location display label. Unset falls back to the provider's IP-derived label.</summary>
    public string? City { get; set; }
    /// <summary>weather: manual location ISO 3166-1 alpha-2 country code, used for the auto C/F unit pick.</summary>
    public string? Cc { get; set; }
    /// <summary>weather: C | F | auto. Unset falls back to auto.</summary>
    public string? Units { get; set; }

    /// <summary>playAudio: playback volume percent, 0-100. Unset falls back to AudioFilePlayer's default.</summary>
    public int? Volume { get; set; }
}

public sealed class DeckSystemAction
{
    /// <summary>
    /// volumeUp | volumeDown | volumeSet | muteToggle | mediaPlayPause |
    /// mediaNext | mediaPrev | brightnessUp | brightnessDown | brightnessSet |
    /// openSettings.
    /// </summary>
    public string Op { get; set; } = "";

    /// <summary>volumeSet (0..1), brightnessSet (0..100).</summary>
    public double? Value { get; set; }
    /// <summary>Up/down increment as a percent (0..100) of the target's own range, for both volume* and brightness*.</summary>
    public double? Step { get; set; }
    /// <summary>brightness* target display id.</summary>
    public string? DisplayId { get; set; }
    /// <summary>media* source id; omitted picks the active session.</summary>
    public string? Source { get; set; }
}

public sealed class DeckNexusAction
{
    /// <summary>
    /// rgbEffect | rgbScene | lightingBrightness | lightingPower | fanProfile |
    /// fanSpeed | y70Power | y70Brightness | y70Rotation.
    /// </summary>
    public string Op { get; set; } = "";

    /// <summary>rgbEffect.</summary>
    public string? Effect { get; set; }
    /// <summary>rgbScene.</summary>
    public string? ProfileId { get; set; }
    /// <summary>fanProfile preset name.</summary>
    public string? Profile { get; set; }
    /// <summary>lightingPower.</summary>
    public string? DeviceId { get; set; }
    /// <summary>fanSpeed.</summary>
    public string? FanId { get; set; }
    /// <summary>lightingBrightness (0..1), fanSpeed (0..100), y70Brightness (0..100).</summary>
    public double? Value { get; set; }
    /// <summary>lightingPower, y70Power.</summary>
    public bool? On { get; set; }
    /// <summary>y70Rotation.</summary>
    public string? Orientation { get; set; }
}

public sealed class DeckSequenceStep
{
    public DeckAction Action { get; set; } = new();
    /// <summary>Milliseconds this step's action is held before release.</summary>
    public int? PressMs { get; set; }
    /// <summary>Milliseconds idled before the next step.</summary>
    public int? GapAfterMs { get; set; }
}

public sealed class DeckToggleState
{
    /// <summary>mute | lightingPower | internal.</summary>
    public string Kind { get; set; } = "";
    /// <summary>lightingPower.</summary>
    public string? DeviceId { get; set; }
}

public sealed class DeckIcon
{
    /// <summary>lucide | emoji | app | image -> the uploaded image id (sha256 hex), served from /deck/images/{id}.</summary>
    public string Kind { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class DeckSlot
{
    /// <summary>Unset falls back to an auto icon by action category.</summary>
    public DeckIcon? Icon { get; set; }
    public string? Label { get; set; }
    /// <summary>Unset falls back to an auto color by action category.</summary>
    public string? Color { get; set; }
    /// <summary>Styling for Label. Unset falls back to nexus-web's deckTitleStyle.ts defaults.</summary>
    public DeckTitleStyle? Title { get; set; }
    /// <summary>A slot is an action, a folder, or empty - never both.</summary>
    public DeckAction? Action { get; set; }
    public DeckFolder? Folder { get; set; }
}

public sealed class DeckTitleStyle
{
    public bool? Show { get; set; }
    /// <summary>top | middle | bottom.</summary>
    public string? Align { get; set; }
    public string? Font { get; set; }
    public int? Size { get; set; }
    public bool? Bold { get; set; }
    public bool? Italic { get; set; }
    public bool? Underline { get; set; }
    public string? Color { get; set; }
}

public sealed class DeckFolder
{
    public List<DeckSlot> Slots { get; set; } = new();
}

/// <summary>One page's grid. Folders still nest within a page via DeckSlot.Folder.</summary>
public sealed class DeckPage
{
    public List<DeckSlot> Slots { get; set; } = new();
}

/// <summary>
/// An ordered list of pages, the axis a deck's page-navigation keys move
/// between. See <see cref="DeckConfigConverter"/> for the wire shape and its
/// legacy-slots back-compat.
/// </summary>
[JsonConverter(typeof(DeckConfigConverter))]
public sealed class DeckConfig
{
    public List<DeckPage> Pages { get; set; } = new();
    /// <summary>Deck-wide default title style seeded onto newly bound keys. Web-owned; the service only persists it.</summary>
    public DeckTitleStyle? DefaultTitleStyle { get; set; }
}
