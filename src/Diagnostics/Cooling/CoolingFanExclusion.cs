using Nexus.Service.Models.Cooling;

namespace Nexus.Service.Diagnostics.Cooling;

/// <summary>Fans Diagnostics leaves out of stall health and alerts: the user turned Nexus control off, or calibration found no tach signal and the fan reports no RPM now.</summary>
public static class CoolingFanExclusion
{
    public static bool IsExcluded(FanChannel channel) =>
        !channel.Controlled || (channel.Classification == "Unresponsive" && channel.Rpm <= 0);
}
