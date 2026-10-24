namespace Nexus.Service.Models.Devices;

/// <summary>Response of <c>GET /system/devices</c>: every detected cooler, fan and controller on this PC.</summary>
public sealed class SystemDevicesResponse
{
    public SystemDeviceDto[] Devices { get; set; } = System.Array.Empty<SystemDeviceDto>();
}

/// <summary>One detected device. Optional fields are omitted from the wire when unknown.</summary>
public sealed class SystemDeviceDto
{
    /// <summary>Stable per physical device.</summary>
    public string Key { get; set; } = "";

    /// <summary>Per model, not per unit: lowercase <c>vendor-source:model</c>.</summary>
    public string ModelKey { get; set; } = "";

    /// <summary>One of <see cref="SystemDeviceCategories"/>.</summary>
    public string Category { get; set; } = "";

    public string Vendor { get; set; } = "";

    /// <summary>Display name.</summary>
    public string Model { get; set; } = "";

    public int? Vid { get; set; }
    public int? Pid { get; set; }
    public string? Family { get; set; }

    /// <summary>Fans (or channels) this entry stands for, when known.</summary>
    public int? Count { get; set; }

    /// <summary>One of <see cref="SystemDeviceCountSources"/>; set whenever <see cref="Count"/> is.</summary>
    public string? CountSource { get; set; }

    /// <summary>Only when the device itself reports the radiator size.</summary>
    public int? RadiatorMm { get; set; }

    /// <summary>Key of the cooler this fan belongs to, only where the protocol says so.</summary>
    public string? PartOf { get; set; }
}

public static class SystemDeviceCategories
{
    public const string Cooler = "cooler";
    public const string Fan = "fan";
    public const string Lighting = "lighting";
    public const string Controller = "controller";
    public const string Keyboard = "keyboard";
    public const string Mouse = "mouse";
    public const string Headset = "headset";
    public const string Peripheral = "peripheral";
}

public static class SystemDeviceCountSources
{
    public const string Detected = "detected";
    public const string User = "user";
}
