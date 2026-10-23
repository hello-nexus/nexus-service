using System.Collections.Generic;

namespace Nexus.Service.Sensors;

/// <summary>
/// Whether the machine is a portable or integrated form factor, from the SMBIOS chassis type (SMBIOS 3.x System Enclosure, Type 3):
/// portable, laptop, notebook, hand held, docking station, all in one, sub notebook, tablet, convertible, detachable, mini PC
/// and stick PC. These carry laptop GPUs that share device ids with desktop cards and the OEM's subsystem, so they never report
/// a card maker. An unreadable chassis type is not portable: servers and VMs often lack it.
/// </summary>
internal static class PortableChassis
{
    private static readonly HashSet<int> Types = new() { 8, 9, 10, 11, 12, 13, 14, 30, 31, 32, 35, 36 };

    public static bool IsPortable(int? chassisType) => chassisType is { } type && Types.Contains(type);
}
