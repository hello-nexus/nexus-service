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
internal readonly record struct GpuSelectDecision(
    GpuSelectAction Action, string Card, string Reason, bool RestampLatch, bool FreshStreak = false);

/// <summary>
/// The render-GPU boot decision, with no platform or IO in it so it can be
/// tested anywhere. <see cref="GpuRenderSelect"/> supplies the persisted state,
/// the crash guard and the adapter count, and executes what comes back.
/// </summary>
internal static class GpuSelectPlan
{
    public static GpuSelectDecision NextAction(
        string? persistedState, string? crashGuard, int usableAdapters, DateTimeOffset now,
        TimeSpan? reprobeOverride = null, GpuEnvironment? env = null)
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
        if (!demoted && (state.Card == GpuSelectState.Integrated || state.Card == GpuSelectState.Discrete))
        {
            return new(GpuSelectAction.WarmRemembered, state.Card, $"remembered:{state.Card}", false);
        }
        if (demoted)
        {
            return new(GpuSelectAction.Probe, GpuSelectState.Unprobed,
                $"{state.Card} crashed init last boot; demoting and re-probing", false);
        }
        if (state.LatchStale(env))
        {
            return new(GpuSelectAction.Probe, GpuSelectState.Unprobed, StaleReason(state, env), false, true);
        }
        if (state.IsOff && !state.ReprobeDue(now, reprobeOverride))
        {
            return new(GpuSelectAction.DeclineNoProbe, GpuSelectState.Unprobed,
                "no card produced a working context", state.OffSince is null);
        }
        return new(GpuSelectAction.Probe, GpuSelectState.Unprobed,
            state.IsOff ? "off latch expired" : "first run", false);
    }

    private static string StaleReason(GpuSelectState state, GpuEnvironment? env) =>
        state.Fingerprint is null || state.Boot is null ? "off latch names no environment"
        : state.Boot != env?.Boot ? "rebooted since the off latch"
        : "GPU set or driver changed since the off latch";
}
