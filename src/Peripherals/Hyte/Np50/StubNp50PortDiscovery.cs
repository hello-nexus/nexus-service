using System.Collections.Generic;

namespace Qos.Service.Peripherals.Hyte.Np50;

/// <summary>
/// Non-Windows discovery stub. Returns an empty list so the rest of the
/// service composes cleanly on macOS/Linux dev builds. A real Linux
/// implementation would scan <c>/sys/class/tty/&lt;name&gt;/device/</c> and
/// match <c>idVendor</c>/<c>idProduct</c>; that's a v2 follow-up.
/// </summary>
public sealed class StubNp50PortDiscovery : INp50PortDiscovery
{
    public IReadOnlyList<Np50PortInfo> Discover() => System.Array.Empty<Np50PortInfo>();
}
