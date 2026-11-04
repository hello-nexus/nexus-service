using System.Collections.Generic;

namespace Nexus.Service.Peripherals.Hyte.Y70Display;

/// <summary>
/// Discovers attached Y70 Touch display controllers at the OS layer. Windows
/// uses SetupAPI to find COM ports with hardware id <c>USB\VID_3402&amp;PID_0C00</c>
/// (Touch), <c>…0C01</c> (Infinite), <c>…0C02</c> (Truly) or <c>…0C06</c> (GT); Linux walks sysfs;
/// macOS returns empty.
/// </summary>
public interface IY70DisplayPortDiscovery
{
    IReadOnlyList<Y70DisplayPort> Discover();
}

/// <summary>One Y70 display controller as seen by the OS, before we open it.</summary>
public sealed class Y70DisplayPort
{
    /// <summary>Serial COM port name, e.g. "COM3" on Windows.</summary>
    public required string PortName { get; init; }

    /// <summary>USB instance-id segment, or empty when unavailable.</summary>
    public string Serial { get; init; } = "";

    /// <summary>"y70-touch" / "y70-infinite" / "y70-truly" / "y70-gt", derived from the matched USB PID.</summary>
    public required string Variant { get; init; }
}
