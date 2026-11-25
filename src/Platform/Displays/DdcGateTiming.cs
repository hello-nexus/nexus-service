using System;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Timing shared between the helper-side DDC write gate (Windows only) and the cross-platform
/// service code that schedules work to land after it has settled.
/// </summary>
internal static class DdcGateTiming
{
    /// <summary>How long after an unlock the gate holds display writes.</summary>
    public static readonly TimeSpan UnlockSettle = TimeSpan.FromSeconds(5);
}
