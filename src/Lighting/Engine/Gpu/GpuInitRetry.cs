using System;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>
/// In-process retry budget for a GL init that threw, sized for a driver whose
/// context creation is intermittent rather than dead. Only a TERMINATED attempt
/// qualifies: an abandoned one leaves its thread parked in native GL code.
/// </summary>
internal static class GpuInitRetry
{
    public const int MaxAttempts = 3;

    /// <summary>How many times an off latch may rearm the context within one
    /// process. Latches are spaced by hours, so this covers days of uptime while
    /// still bounding the hidden windows an attempt that fails after window
    /// creation leaks.</summary>
    public const int MaxLatchRearms = 8;

    /// <summary>How many times a lost GL context is re-created within one process before it stays down until restart, so a flapping device cannot loop.</summary>
    public const int MaxContextLossRecreates = 5;

    public static TimeSpan DelayFor(int attempt) => attempt switch
    {
        <= 1 => TimeSpan.FromSeconds(30),
        2 => TimeSpan.FromMinutes(2),
        _ => TimeSpan.FromMinutes(8),
    };
}
