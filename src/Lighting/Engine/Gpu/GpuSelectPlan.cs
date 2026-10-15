using System;

namespace Nexus.Service.Lighting.Engine.Gpu;

internal enum GpuSelectAction
{
    /// <summary>Run the throwaway probe subprocess, then init in-process unless
    /// the probe condemned the card.</summary>
    Probe,

    /// <summary>Init the remembered card directly, no probe.</summary>
    WarmRemembered,

    /// <summary>Do not touch the GPU this session, and write a fresh off latch.</summary>
    DeclineAndLatch,

    /// <summary>Do not touch the GPU this session; an off latch already holds.</summary>
    DeclineNoProbe,

    /// <summary>No adapter to judge (driver being installed, or none fitted):
    /// write nothing, probe nothing, and look again for a card later.</summary>
    WaitForAdapter,
}

/// <param name="RestampLatch">The held latch carries no timestamp, so the caller
/// writes one; the streak does not grow.</param>
/// <param name="FreshStreak">The probe is owed to a stale latch, so a failure
/// starts a new streak instead of lengthening the old wait.</param>
/// <param name="PreferDedicated">The Intel-integrated-plus-dedicated rule applies:
/// the dedicated card is probed first and the integrated card is the fallback.</param>
internal readonly record struct GpuSelectDecision(
    GpuSelectAction Action, string Card, string Reason, bool RestampLatch, bool FreshStreak = false,
    bool PreferDedicated = false);

/// <summary>
/// The render-GPU boot decision, with no platform or IO in it so it can be
/// tested anywhere. <see cref="GpuRenderSelect"/> supplies the persisted state,
/// the crash guard and the adapter count, and executes what comes back.
/// </summary>
internal static class GpuSelectPlan
{
    public static GpuSelectDecision NextAction(
        string? persistedState, string? crashGuard, int usableAdapters, DateTimeOffset now,
        TimeSpan? reprobeOverride = null, GpuEnvironment? env = null, bool intelIgpuWithDedicated = false)
    {
        var state = GpuSelectState.Parse(persistedState);
        var guard = string.IsNullOrWhiteSpace(crashGuard) ? null : crashGuard.Trim();

        // A failed probe with no adapter is a verdict on the driver's absence,
        // not on the card.
        if (usableAdapters <= 0)
        {
            return new(GpuSelectAction.WaitForAdapter, GpuSelectState.Unprobed, "no display adapter present", false);
        }

        if (usableAdapters == 1)
        {
            if (guard is not null)
            {
                return new(GpuSelectAction.DeclineAndLatch, GpuSelectState.Integrated,
                    "sole GPU crashed init last boot", false);
            }
            if (state.LatchStale(env))
            {
                return new(GpuSelectAction.Probe, GpuSelectState.Integrated, StaleReason(state, env), false, true);
            }
            if (state.IsOff && !state.ReprobeDue(now, reprobeOverride))
            {
                return new(GpuSelectAction.DeclineNoProbe, GpuSelectState.Integrated,
                    "sole GPU is latched off", state.OffSince is null);
            }
            return new(GpuSelectAction.Probe, GpuSelectState.Integrated,
                state.IsOff ? "off latch expired" : "single gpu", false);
        }

        // A crash under an off latch is the latest word on the card set: hold
        // the latch rather than let a stale environment re-run that init.
        if (guard is not null && state.IsOff)
        {
            return new(GpuSelectAction.DeclineAndLatch, GpuSelectState.Unprobed,
                $"{guard} crashed init last boot", false);
        }
        // A leftover crash guard naming the remembered card demotes it, so the
        // next step re-probes instead of repeating the init that crashed.
        var demoted = guard is not null && guard == state.Card;
        // An Intel iGPU spins a core in its OpenGL driver for the life of the
        // process, so a PC with a dedicated card renders on that. The dedicated
        // card is only ever reached through a probe, never initialized
        // in-process unproven. A crash on it last boot lifts the rule here, and
        // the caller records that for later boots.
        var dedicated = intelIgpuWithDedicated && guard != GpuSelectState.Discrete;
        var open = dedicated ? GpuSelectState.Discrete : GpuSelectState.Unprobed;
        if (!demoted && dedicated && state.Card == GpuSelectState.Integrated)
        {
            return new(GpuSelectAction.Probe, GpuSelectState.Discrete,
                "remembered:integrated; probing the dedicated GPU", false, PreferDedicated: true);
        }
        if (!demoted && (state.Card == GpuSelectState.Integrated || state.Card == GpuSelectState.Discrete))
        {
            return new(GpuSelectAction.WarmRemembered, state.Card, $"remembered:{state.Card}", false);
        }
        if (demoted)
        {
            return new(GpuSelectAction.Probe, open,
                $"{state.Card} crashed init last boot; demoting and re-probing", false, PreferDedicated: dedicated);
        }
        if (state.LatchStale(env))
        {
            return new(GpuSelectAction.Probe, open, StaleReason(state, env), false, true, dedicated);
        }
        if (state.IsOff && !state.ReprobeDue(now, reprobeOverride))
        {
            return new(GpuSelectAction.DeclineNoProbe, GpuSelectState.Unprobed,
                "no card produced a working context", state.OffSince is null);
        }
        return new(GpuSelectAction.Probe, open,
            state.IsOff ? "off latch expired" : "first run", false, PreferDedicated: dedicated);
    }

    /// <summary>Probes the dedicated card may time out across boots before the
    /// rule gives up for the current environment.</summary>
    public const int MaxInconclusiveAttempts = 3;

    /// <summary>Whether the rule runs this boot: the adapter set qualifies and
    /// the dedicated card has not failed here. The marker holds the adapter
    /// fingerprint it was written under, so a driver or card change tries again.
    /// A failure, crash or hang disables the rule at once; inconclusive probes
    /// (timeout, spawn failure) are counted and tolerated up to the cap.</summary>
    public static bool RuleActive(bool detected, string? marker, GpuEnvironment env)
    {
        if (!detected)
        {
            return false;
        }
        var parts = (marker ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !string.Equals(parts[0], env.Fingerprint, StringComparison.Ordinal))
        {
            return true;
        }
        var count = InconclusiveCount(marker, env);
        return count > 0 && count < MaxInconclusiveAttempts;
    }

    /// <summary>The inconclusive probes recorded for this environment, 0 when
    /// the marker is absent, from another environment, or records a failure.</summary>
    public static int InconclusiveCount(string? marker, GpuEnvironment env)
    {
        var parts = (marker ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !string.Equals(parts[0], env.Fingerprint, StringComparison.Ordinal)
            || !parts[1].StartsWith(InconclusiveKey, StringComparison.Ordinal))
        {
            return 0;
        }
        return int.TryParse(parts[1][InconclusiveKey.Length..], System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 0;
    }

    /// <summary>The marker text: a failure (the default, so a crash or hang
    /// mid-attempt leaves it) or a count of inconclusive probes.</summary>
    public static string DedicatedMarker(GpuEnvironment env, int inconclusive = 0) =>
        inconclusive > 0
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{env.Fingerprint} {InconclusiveKey}{inconclusive}")
            : $"{env.Fingerprint} failed";

    private const string InconclusiveKey = "inconclusive=";

    /// <summary>True when the adapter set holds an Intel integrated GPU and at
    /// least one dedicated one, by the same video memory cut the adapter matcher
    /// uses.</summary>
    public static bool IntelIgpuWithDedicated(System.Collections.Generic.IEnumerable<(uint VendorId, long DedicatedVramMb)> adapters)
    {
        var intelIgpu = false;
        var dedicated = false;
        foreach (var (vendor, vram) in adapters)
        {
            if (vram >= Nexus.Service.Sensors.GpuAdapterLuids.DedicatedVramMinMb)
            {
                dedicated = true;
            }
            else if (vendor == IntelVendorId)
            {
                intelIgpu = true;
            }
        }
        return intelIgpu && dedicated;
    }

    private const uint IntelVendorId = 0x8086;

    private static string StaleReason(GpuSelectState state, GpuEnvironment? env) =>
        state.Fingerprint is null || state.Boot is null ? "off latch names no environment"
        : state.Boot != env?.Boot ? "rebooted since the off latch"
        : "GPU set or driver changed since the off latch";
}
