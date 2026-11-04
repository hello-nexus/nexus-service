using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nexus.Service.Models.Cooling;

namespace Nexus.Service.Models.Widgets;

/// <summary>
/// Generic acknowledgement payload for fire-and-forget host actions.
/// AOT-safe replacement for the anonymous-type
/// <c>new { ok, message }</c> idiom which silently fails under the
/// source-gen JSON context.
/// </summary>
public sealed class AppActionAckDto
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("applied")] public string? Applied { get; set; }
}

/// <summary>Combined cooling read for a widget: fan channels + temperature sources.</summary>
public sealed class AppCoolingStateDto
{
    [JsonPropertyName("channels")] public List<FanChannel> Channels { get; set; } = new();
    [JsonPropertyName("sources")] public List<TemperatureSource> Sources { get; set; } = new();
}

/// <summary>One row in the screentime payload's <c>history</c> array.</summary>
public sealed class ScreentimeHistoryEntryDto
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("totalMs")] public long TotalMs { get; set; }
    [JsonPropertyName("formatted")] public string Formatted { get; set; } = "";
    [JsonPropertyName("pctOfMax")] public int PctOfMax { get; set; }
}

/// <summary>The active app session block.</summary>
public sealed class ScreentimeFocusDto
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("totalMs")] public long TotalMs { get; set; }
    [JsonPropertyName("formatted")] public string Formatted { get; set; } = "";
}

/// <summary>Top-level payload returned by the <c>screentime.today</c>
/// host action.</summary>
public sealed class ScreentimeTodayDto
{
    [JsonPropertyName("focus")] public ScreentimeFocusDto? Focus { get; set; }
    [JsonPropertyName("history")] public List<ScreentimeHistoryEntryDto> History { get; set; } = new();
    [JsonPropertyName("totalMs")] public long TotalMs { get; set; }
    [JsonPropertyName("totalFormatted")] public string TotalFormatted { get; set; } = "";
    [JsonPropertyName("maxMs")] public long MaxMs { get; set; }
    [JsonPropertyName("hasData")] public bool HasData { get; set; }
}

/// <summary>Payload returned by the <c>system.specs</c> host action: SMBIOS
/// Type 1 identity plus the pre-formatted System Specs strings. Every field
/// is a display string, empty when the platform couldn't resolve it.</summary>
public sealed class SystemInfoResponse
{
    [JsonPropertyName("manufacturer")] public string Manufacturer { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("family")] public string Family { get; set; } = "";
    [JsonPropertyName("serial")] public string Serial { get; set; } = "";
    [JsonPropertyName("windowsProductKey")] public string WindowsProductKey { get; set; } = "";
    // True activated, false not activated; omitted from the JSON when unknown (non-Windows or the query failed).
    [JsonPropertyName("windowsActivated")] public bool? WindowsActivated { get; set; }
    [JsonPropertyName("pcName")] public string PcName { get; set; } = "";
    [JsonPropertyName("osBuild")] public string OsBuild { get; set; } = "";
    [JsonPropertyName("processor")] public string Processor { get; set; } = "";
    [JsonPropertyName("motherboard")] public string Motherboard { get; set; } = "";
    [JsonPropertyName("memory")] public string Memory { get; set; } = "";
    [JsonPropertyName("storage")] public string Storage { get; set; } = "";
    [JsonPropertyName("graphicsCard")] public string GraphicsCard { get; set; } = "";
    [JsonPropertyName("monitor")] public string Monitor { get; set; } = "";
    [JsonPropertyName("soundCard")] public string SoundCard { get; set; } = "";
    [JsonPropertyName("networkCard")] public string NetworkCard { get; set; } = "";
}

/// <summary>One device row returned by the <c>devices.list</c> host action.
/// Deliberately narrower than the internal device list: only identity,
/// display name, category, presence and firmware string cross into an app.</summary>
public sealed class AppDeviceListItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("connected")] public bool Connected { get; set; }
    [JsonPropertyName("firmwareVersion")] public string FirmwareVersion { get; set; } = "";
}

/// <summary>Payload returned by the <c>devices.list</c> host action: every
/// registered device with its presence flag, so the app filters itself.</summary>
public sealed class AppDeviceListResponse
{
    [JsonPropertyName("devices")] public List<AppDeviceListItem> Devices { get; set; } = new();
}
