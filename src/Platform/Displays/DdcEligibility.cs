using System;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Whether a display can carry DDC/CI at all. A display behind a USB or other indirect adapter
/// has no DDC wire, and a DDC transaction sent to its driver can make the device re-enumerate,
/// so it must never be probed, not even once. Platform-neutral so the rule is unit-testable off Windows.
/// </summary>
internal static class DdcEligibility
{
    // DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY values for indirect display drivers.
    internal const uint OutputTechnologyIndirectWired = 16;
    internal const uint OutputTechnologyIndirectVirtual = 17;

    /// <param name="outputTechnology">Target output technology from QueryDisplayConfig; null when unknown.</param>
    /// <param name="adapterDeviceId">PnP DeviceID of the GDI display adapter, e.g. a PCI or USB path.</param>
    internal static bool CanCarryDdc(uint? outputTechnology, string? adapterDeviceId)
    {
        if (outputTechnology is OutputTechnologyIndirectWired or OutputTechnologyIndirectVirtual)
        {
            return false;
        }
        return string.IsNullOrEmpty(adapterDeviceId)
            || !adapterDeviceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase);
    }
}
