using System.Collections.Generic;

namespace Nexus.Service.Sensors;

/// <summary>
/// Whether the machine is a portable form factor, from the SMBIOS chassis type (SMBIOS 3.x System Enclosure, Type 3): portable,
/// laptop, notebook, sub notebook, convertible and detachable. Laptop GPUs share device ids with desktop cards and carry the
/// OEM's subsystem, so a laptop never reports a card maker. An unreadable chassis type is not portable: servers and VMs often lack it.
/// </summary>
internal static class PortableChassis
{
    private static readonly HashSet<int> Types = new() { 8, 9, 10, 14, 31, 32 };

    public static bool IsPortable(int? chassisType) => chassisType is { } type && Types.Contains(type);
}
