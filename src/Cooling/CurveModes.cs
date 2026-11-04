using System;
using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Cooling;

/// <summary>
/// Shared curve-binding lookup used by the Linux fan providers (hwmon,
/// liquidctl, NVIDIA) so a curve-driven fan reports <see cref="Nexus.Service.Models.Cooling.FanModes.Curve"/>
/// the same way <see cref="WindowsFanControlProvider"/> does - the sidebar dot +
/// Cooling-view restoration rely on every provider agreeing. Centralized here
/// instead of re-rolling the same two-loop scan in each provider.
/// </summary>
internal static class CurveModes
{
    /// <summary>
    /// Fan-channel ids bound to any curve output. Matches the Windows provider:
    /// keyed by <c>CurveOutput.Id</c> (the fan channel id) across all curves.
    /// </summary>
    internal static HashSet<string> BoundFanIds(IConfigStore? config)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (config is null)
            return set;
        // Under the store lock: curve edits mutate the live lists in place, and an enumeration racing one throws.
        config.Read(s =>
        {
            foreach (var c in s.Cooling.Curves)
            {
                foreach (var o in c.Outputs)
                {
                    if (!string.IsNullOrEmpty(o.Id))
                        set.Add(o.Id);
                }
            }
        });
        return set;
    }
}
