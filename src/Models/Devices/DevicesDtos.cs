using System.Collections.Generic;

namespace Nexus.Service.Models.Devices;

// ----- /devices/all - unified device list -----

public sealed class DeviceListItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public bool Connected { get; set; }
    public string FirmwareVersion { get; set; } = "";
    /// <summary>Firmware-catalog key for available-version lookup. Equals Id for most devices; the connected variant ("q60"/"q80") for Q-series.</summary>
    public string FirmwareType { get; set; } = "";
    /// <summary>True when Nexus is allowed to claim/control this device. False means Nexus still detects it but never opens its port/handle.</summary>
    public bool NexusControlEnabled { get; set; } = true;
    /// <summary>True only for first-party handlers whose gate does something: claiming the device through a gate-honoring connection worker, or starting/stopping the vendor driver process that drives it. False when the switch would gate nothing - a plugin handler managing its own hardware - and the UI hides it.</summary>
    public bool SupportsNexusControl { get; set; }
    /// <summary>True for a Nexus Control device driving non-Hyte/iBUYPOWER hardware (experimental support). Drives the "Experimental" badge in the UI. Always false when SupportsNexusControl is false.</summary>
    public bool Experimental { get; set; }
    /// <summary>Short code for a partial-detection issue (e.g. "usb-disconnected"), or null when there is nothing to flag.</summary>
    public string? Warning { get; set; }
    /// <summary>ConflictAppCatalog id of the third-party app that competes with this device (e.g. "icue", "lian-li-l-connect"), or null when none maps. Drives the device page's "close the app first" gate.</summary>
    public string? ConflictAppId { get; set; }
}

/// <summary>Body for POST /devices/control.</summary>
public sealed class DeviceControlRequest
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; }
}

// ----- /devices/firmware/status - current vs bundled-available firmware -----

public sealed class FirmwareStatusItem
{
    /// <summary>Device id matching IDeviceHandler.Id (e.g. "np50", "cnvs", "fan-hub"). Identity + icon key.</summary>
    public string DeviceType { get; set; } = "";
    /// <summary>Firmware-catalog key (connected variant, e.g. "cnvs-left", "q60"). Pass this to the flash endpoint.</summary>
    public string FirmwareType { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    /// <summary>Version the connected device reports, or empty when not yet read.</summary>
    public string CurrentVersion { get; set; } = "";
    /// <summary>Newest version bundled in this build for the device.</summary>
    public string AvailableVersion { get; set; } = "";
    /// <summary>True when the bundled version is strictly newer than the device's current version.</summary>
    public bool UpdateAvailable { get; set; }
    /// <summary>All bundled versions for this device's connected variant (newest first).</summary>
    public List<string> AvailableVersions { get; set; } = new();
    /// <summary>
    /// Every image the connected device can be flashed with - including
    /// sibling-variant images (e.g. a Gen1 CNVS can also take the Gen2 image).
    /// Drives the dev-only picker so cross-branch testing is possible; the prod
    /// Install path never uses these.
    /// </summary>
    public List<FlashableImage> DevImages { get; set; } = new();
}

public sealed class FlashableImage
{
    /// <summary>Firmware-catalog key for this image (pass to the flash endpoint).</summary>
    public string FirmwareType { get; set; } = "";
    public string Version { get; set; } = "";
}

// ----- /devices/firmware/flash - flash orchestration -----

public sealed class FlashRequest
{
    /// <summary>Firmware-catalog key to flash (the connected variant, e.g. "cnvs-left").</summary>
    public string DeviceType { get; set; } = "";
    /// <summary>Bundled version to flash. Any bundled version is allowed (dev downgrade).</summary>
    public string Version { get; set; } = "";
}

/// <summary>
/// Global flash progress. A single flash runs at a time; the UI polls this so
/// progress survives tab navigation (it's server-side state, not component state).
/// </summary>
public sealed class FlashStatusDto
{
    public bool Active { get; set; }
    public string DeviceType { get; set; } = "";
    public string Version { get; set; } = "";
    /// <summary>idle | preparing | entering-dfu | waiting-dfu | downloading | verifying | finalizing | done | failed</summary>
    public string Phase { get; set; } = "idle";
    public int Percent { get; set; }
    public string Message { get; set; } = "";
    public bool Success { get; set; }
    public string Error { get; set; } = "";
}

public sealed class FlashStartResponse : ApiResponse
{
    public bool Started { get; set; }
}

// ----- /devices/usb/all - raw USB device list with full details -----

public sealed class UsbDeviceDetail
{
    public string VendorId { get; set; } = "";   // "0x3402"
    public string ProductId { get; set; } = "";  // "0x0BFF"
    public string Name { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Serial { get; set; } = "";
    public string Location { get; set; } = "";
    public string Class { get; set; } = "";
    public string Speed { get; set; } = "";
    public string Driver { get; set; } = "";
    public string HardwareId { get; set; } = "";
}

// ----- /devices endpoints -----

public class IsDeviceConnectedResponse : ApiResponse { public bool Connected { get; set; } }
public class FirmwareVersionResponse : ApiResponse { public string Version { get; set; } = ""; }

public class GetCnvsSettingsResponse : ApiResponse
{
    public bool PlayAnimation { get; set; }
    public bool PlayWhenPCOff { get; set; }
    /// <summary>Reported CNVS firmware version (e.g. "1.0.2.2"), or empty when not yet probed / device offline.</summary>
    public string FirmwareVersion { get; set; } = "";
    /// <summary>
    /// True when the firmware honors the FF DC 07 / FF DC 08 settings
    /// commands (introduced in CNVS firmware v1.0.2.1 - see
    /// hyte-refs protocol doc CNVS/stm32-commands.md §3). The UI gates the
    /// two toggles on this: when false (older firmware) the writes silently
    /// no-op, so the toggles are disabled with a "Requires CNVS firmware
    /// 1.0.2.1+" hint.
    /// </summary>
    public bool SettingsSupported { get; set; }
}

public class SetCnvsSettingsBody
{
    public bool PlayAnimation { get; set; }
    public bool PlayWhenPCOff { get; set; }
}

public class UpdateBody { public string Id { get; set; } = ""; }

public class CheckForUpdateResponse : ApiResponse
{
    public bool IsUpdateAvailable { get; set; }
}

public class UpdateResponse : ApiResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}

public class MoboChannel
{
    public string Name { get; set; } = "";
    public int Max { get; set; }
    public int Count { get; set; }
}

public class GetMotherboardLEDsResponse : ApiResponse
{
    public List<MoboChannel> Channels { get; set; } = new();
}

public class SetMotherboardLEDsBody
{
    public List<SetChannel> Channels { get; set; } = new();
}

public class SetChannel
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
}

public class CheckFirmwareFunctionBody
{
    public string Type { get; set; } = "";
    public string Version { get; set; } = "";
    public string FirmwareFunction { get; set; } = "";
}

public class CheckFirmwareFunctionResponse : ApiResponse
{
    public bool IsFunctionAvailable { get; set; }
}

// ----- /devices/lighting-devices -----

public class LightingDevice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string IconType { get; set; } = "";
    public bool LedsOn { get; set; }
    /// <summary>False when the user marked this device not controlled: Nexus stops pushing frames to it entirely so firmware/vendor lighting can take over. Distinct from <see cref="LedsOn"/> (power off still streams black).</summary>
    public bool Controlled { get; set; } = true;
    public int Brightness { get; set; }
    public float Hue { get; set; }
    public float Saturation { get; set; }
    public int LedCount { get; set; }
    /// <summary>Number of the card's LEDs not disabled in the resolved layout (applied mapping's disabled set layered under user overrides, which win in both directions). Equals <see cref="LedCount"/> when no disable data exists.</summary>
    public int EnabledLedCount { get; set; }
    public float CanvasX { get; set; }
    public float CanvasY { get; set; }
    public float CanvasW { get; set; } = 80;
    public float CanvasH { get; set; }
    public int CanvasRotation { get; set; }
    /// <summary>Set only for motherboard zone cards. Points at the parent OpenRGB device id (e.g. "openrgb-0") so the UI can group zones under a motherboard header.</summary>
    public string? ParentDeviceId { get; set; }
    /// <summary>Set only for motherboard zone cards. Zone index within the parent OpenRGB device (0..N-1).</summary>
    public int? ZoneIndex { get; set; }
    /// <summary>"single", "linear", or "matrix" - zone layout type reported by OpenRGB. Null for non-zone devices.</summary>
    public string? ZoneType { get; set; }
    /// <summary>True when the ARGB zone supports live resize via OpenRGB's RESIZEZONE opcode. Drives whether the UI shows the LED-count editor.</summary>
    public bool ZoneResizable { get; set; }
    /// <summary>Cross-install hardware fingerprint for community mapping lookup (see DeviceKeyComputer). Empty when the device cannot be fingerprinted; the mapping UI hides itself then.</summary>
    public string DeviceKey { get; set; } = "";
    /// <summary>Owning device for the device-level settings modal (zone editor routing target). Equals <see cref="Id"/> for single-zone standalone devices and non-partitionable cards.</summary>
    public string DeviceId { get; set; } = "";
    /// <summary>True when the owning device supports user zone partitions. False for hub ports, smart lights, and 1-LED devices so the UI hides zone management.</summary>
    public bool ZoneCustomizable { get; set; }
}

public class GetLightingDevicesResponse
{
    public bool IsInit { get; set; }
    public List<LightingDevice> Devices { get; set; } = new();
}

public class SetDisabledLedsBody { public List<string> Devices { get; set; } = new(); }
public class SetLightingDevicePowerBody { public string Id { get; set; } = ""; public bool On { get; set; } }
public class SetLightingDeviceControlledBody { public string Id { get; set; } = ""; public bool Controlled { get; set; } }
public class SetLightingDeviceBrightness { public string Id { get; set; } = ""; public int Brightness { get; set; } }
public class SetLightingDeviceHue { public string Id { get; set; } = ""; public float Hue { get; set; } }
public class SetLightingDeviceSaturation { public string Id { get; set; } = ""; public float Saturation { get; set; } }
public class SetLightingDeviceColor { public string Id { get; set; } = ""; public float Hue { get; set; } public float Saturation { get; set; } }
public class SetZoneLedCountBody { public string Id { get; set; } = ""; public int Count { get; set; } }
public class IdentifyLightingDeviceBody { public string Id { get; set; } = ""; public int DurationMs { get; set; } = 2000; }

public class SaveDeviceLayoutBody
{
    public string Id { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float W { get; set; }
    public float H { get; set; }
    public int Rotation { get; set; }
}

// ----- /devices/lighting-devices/layout-presets -----

public sealed class LayoutPresetDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public Dictionary<string, Nexus.Service.Persistence.DeviceLayout> Layouts { get; set; } = new();
}

public sealed class LayoutPresetsResponse
{
    public List<LayoutPresetDto> Presets { get; set; } = new();
    // Always serialize; null = no preset selected. WhenWritingNull would omit it.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public string? ActiveId { get; set; }
}

public sealed class CreateLayoutPresetBody
{
    public string Name { get; set; } = "";
}

public sealed class CreateLayoutPresetResponse
{
    public LayoutPresetDto? Preset { get; set; }
    public string? ActiveId { get; set; }
}

public sealed class UpdateLayoutPresetBody
{
    public string? Name { get; set; }
    public bool SaveCurrent { get; set; }
}

public sealed class SetActivePresetBody
{
    public string? Id { get; set; }
}

public sealed class DeletePresetResponse
{
    // Always serialize; null = no preset selected. WhenWritingNull would omit it.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public string? ActiveId { get; set; }
}

// Batch-apply a full layouts map (used by undo/redo and preset load on the client).
public sealed class BatchApplyLayoutsBody
{
    public Dictionary<string, Nexus.Service.Persistence.DeviceLayout> Layouts { get; set; } = new();
}

// ----- /devices/lighting-devices/{id}/led-map -----

public sealed class LedMapResponse
{
    public string Id { get; set; } = "";
    public int LedCount { get; set; }
    public List<LedMapEntry> Leds { get; set; } = new();
    public bool HasCustomOverrides { get; set; }
    public float AspectRatio { get; set; }
    /// <summary>Named LED segments (resolved: user delta wins over the applied mapping's groups).</summary>
    public List<Nexus.Service.Lighting.Mappings.MappingGroup> Groups { get; set; } = new();
    /// <summary>Set when a community/file mapping is applied to this device.</summary>
    public AppliedMappingSummary? Applied { get; set; }
    public string DeviceKey { get; set; } = "";
}

public sealed class LedMapEntry
{
    public int Index { get; set; }
    public float U { get; set; }
    public float V { get; set; }
    public string Name { get; set; } = "";
    public string ZoneType { get; set; } = "";
    public bool IsCustom { get; set; }
    /// <summary>True when the LED is "parked" / removed from the effect mapping. The render engine writes black for these LEDs; the editor lays them out in a row below the device frame so the user can drag them back in to re-enable.</summary>
    public bool Disabled { get; set; }
}

public sealed class SaveLedMapBody
{
    public List<Nexus.Service.Persistence.LedPositionOverride> Overrides { get; set; } = new();
    public float AspectRatio { get; set; }
    /// <summary>Optional group save: null leaves the stored groups untouched (older clients), a list (even empty) replaces them.</summary>
    public List<Nexus.Service.Lighting.Mappings.MappingGroup>? Groups { get; set; }
}

public sealed class LedHighlightBody
{
    public List<int> Indices { get; set; } = new();
}

public sealed class LedTestPatternBody
{
    public string Pattern { get; set; } = "horizontal";
}

public sealed class LedPreviewPosition
{
    public int Index { get; set; }
    public float U { get; set; }
    public float V { get; set; }
    public bool Disabled { get; set; }
}

public sealed class LedPreviewLayoutBody
{
    public int LedCount { get; set; }
    public List<LedPreviewPosition> Leds { get; set; } = new();
}
