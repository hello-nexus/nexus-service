using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Lifecycle;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;

namespace Nexus.Service.Diagnostics.Cooling;

/// <summary>Fans Diagnostics leaves out of stall health and alerts: Nexus control is off in settings, or calibration found no tach signal and the fan reports no RPM now. Empty while the Cooling feature is off.</summary>
public static class CoolingFanExclusion
{
    /// <summary>Control is read from settings: the provider's channels never carry it, only the Cooling route fills it in.</summary>
    public static HashSet<string> ExcludedIds(IEnumerable<FanChannel> channels, NexusSettings settings)
    {
        if (!FeatureGates.CoolingOf(settings))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
        var uncontrolled = CoolingSnapshots.Uncontrolled(settings.Cooling);
        return channels
            .Where(ch => uncontrolled.Contains(ch.Id) || (ch.Classification == "Unresponsive" && ch.Rpm <= 0))
            .Select(ch => ch.Id)
            .ToHashSet(StringComparer.Ordinal);
    }
}
