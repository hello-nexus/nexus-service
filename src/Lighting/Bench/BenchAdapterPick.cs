using System.Collections.Generic;
using Nexus.Service.Sensors;

namespace Nexus.Service.Lighting.Bench;

/// <summary>Maps an adapter class to a concrete DXGI adapter. Integrated is a hardware adapter with
/// little dedicated memory, the same cut the sensor stack uses; dedicated is the one with the most.</summary>
internal static class BenchAdapterPick
{
    private const uint MicrosoftVendorId = 0x1414;

    public static GpuAdapterLuids.Adapter? Pick(IReadOnlyList<GpuAdapterLuids.Adapter> adapters, string kind)
    {
        GpuAdapterLuids.Adapter? best = null;
        foreach (var a in adapters)
        {
            // Basic Render Driver and indirect display adapters have no GL ICD.
            if (a.VendorId == MicrosoftVendorId)
            {
                continue;
            }
            var dedicated = a.DedicatedVramMb >= GpuAdapterLuids.DedicatedVramMinMb;
            if (kind == "integrated" && !dedicated)
            {
                best ??= a;
            }
            else if (kind == "dedicated" && dedicated && (best is null || a.DedicatedVramMb > best.Value.DedicatedVramMb))
            {
                best = a;
            }
        }
        return best;
    }
}
