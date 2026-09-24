using System.Collections.Concurrent;
using Nexus.Service.Models.Displays;
using Nexus.Service.Panel;
using Nexus.Service.Peripherals.Y70;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Hardware-facing write coordinator for display brightness. The panel can send
/// pointer-rate target values; this controller serializes per display, applies
/// provider policy, and coalesces queued targets so the hardware only sees the
/// newest requested value for each write window.
/// </summary>
public sealed class DisplayBrightnessController
{
    private readonly IDisplayBrightnessProvider _provider;
    private readonly PanelDeviceRegistry? _panelDevices;
    private readonly Nexus.Service.Persistence.IConfigStore? _store;
    private readonly DisplayTopologyService? _topology;
    private readonly IY70Provider? _y70;
    private readonly ConcurrentDictionary<string, DisplayWriteState> _states = new();

    public DisplayBrightnessController(
        IDisplayBrightnessProvider provider,
        PanelDeviceRegistry? panelDevices = null,
        Nexus.Service.Persistence.IConfigStore? store = null,
        DisplayTopologyService? topology = null,
        IY70Provider? y70 = null)
    {
        _provider = provider;
        _panelDevices = panelDevices;
        _store = store;
        _topology = topology;
        _y70 = y70;
    }

    /// <summary>Displays the user turned brightness control off for. Passed
    /// down to the provider so an excluded display is never probed, and
    /// re-checked here so a provider that ignores the argument still cannot
    /// surface a control for one.</summary>
    private HashSet<string> DdcDisabled()
        => new(_store?.Load().Devices.DdcDisabledDisplays ?? new List<string>(), StringComparer.Ordinal);

    public DisplayListResponse ListDisplays()
    {
        var disabled = DdcDisabled();
        var displays = new List<DisplayDto>(_provider.Enumerate(disabled));
        foreach (var display in displays)
        {
            if (disabled.Contains(display.Id))
            {
                display.DdcEnabled = false;
                SuppressBrightnessControl(display, "Brightness control is turned off for this display.");
                continue;
            }
            if (IsXeneonEdge(display.Id)) SuppressBrightnessControl(display);
            else if (IsY70(display.Id)) RouteThroughY70(display);
        }
        return new DisplayListResponse
        {
            Displays = displays,
            Hint = displays.Count == 0 ? _provider.Hint : "",
        };
    }

    public int? GetBrightness(string id)
    {
        if (IsXeneonEdge(id) || DdcDisabled().Contains(id)) return null;
        return IsY70(id) ? _y70!.GetBrightness() : _provider.GetBrightness(id);
    }

    /// <summary>
    /// Turn DDC/CI on or off for one display and persist it. Off means Nexus
    /// sends that monitor nothing at all, capability probe included - the
    /// escape hatch for a panel whose firmware hangs on DDC.
    /// </summary>
    public bool SetDdcEnabled(string id, bool enabled)
    {
        if (_store is null || string.IsNullOrEmpty(id)) return false;
        _store.Update(s =>
        {
            var list = s.Devices.DdcDisabledDisplays;
            if (enabled) list.RemoveAll(x => string.Equals(x, id, StringComparison.Ordinal));
            else if (!list.Contains(id)) list.Add(id);
        });
        ServiceLog.Info($"[ddc] brightness control {(enabled ? "enabled" : "disabled")} for display {id}");
        return true;
    }

    public async Task<DisplayBrightnessDto> SetBrightnessAsync(
        string id,
        int percent,
        CancellationToken cancellationToken = default)
    {
        if (IsXeneonEdge(id))
        {
            return new DisplayBrightnessDto
            {
                Id = id,
                RequestedBrightness = ClampPercent(percent),
                AppliedBrightness = 0,
                Brightness = 0,
                Status = DisplayBrightnessWriteStatuses.Unsupported,
                Error = "This panel's brightness is controlled through its native settings, not DDC.",
            };
        }

        if (DdcDisabled().Contains(id))
        {
            return new DisplayBrightnessDto
            {
                Id = id,
                RequestedBrightness = ClampPercent(percent),
                AppliedBrightness = 0,
                Brightness = 0,
                Status = DisplayBrightnessWriteStatuses.Unsupported,
                Error = "Brightness control is turned off for this display.",
            };
        }

        var requested = ClampPercent(percent);
        if (IsY70(id))
        {
            // The provider picks the panel's transport (serial FF CC, RGB
            // gains or VCP 0x10 by variant) and does its own coalescing.
            _y70!.SetBrightness(requested);
            return new DisplayBrightnessDto
            {
                Id = id,
                RequestedBrightness = requested,
                AppliedBrightness = requested,
                Brightness = requested,
                Status = DisplayBrightnessWriteStatuses.Applied,
            };
        }

        var state = _states.GetOrAdd(id, _ => new DisplayWriteState());
        var waiter = new TaskCompletionSource<DisplayBrightnessDto>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (state.Gate)
        {
            state.PendingTarget = requested;
            state.Waiters.Add(waiter);
            if (state.Worker is null || state.Worker.IsCompleted)
            {
                state.Worker = DrainAsync(id, state);
            }
        }

        using var registration = cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
        return await waiter.Task.ConfigureAwait(false);
    }

    private async Task DrainAsync(string id, DisplayWriteState state)
    {
        while (true)
        {
            var policy = _provider.GetBrightnessWritePolicy(id);
            var delayMs = DelayBeforeNextWriteMs(state.LastWriteAt, policy.MinWriteIntervalMs);
            if (delayMs > 0)
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
            }

            int target;
            List<TaskCompletionSource<DisplayBrightnessDto>> waiters;
            lock (state.Gate)
            {
                if (state.PendingTarget is null)
                {
                    state.Worker = null;
                    return;
                }

                target = state.PendingTarget.Value;
                state.PendingTarget = null;
                waiters = new List<TaskCompletionSource<DisplayBrightnessDto>>(state.Waiters);
                state.Waiters.Clear();
            }

            DisplayBrightnessDto result;
            try
            {
                result = _provider.SetBrightness(id, target);
                state.LastWriteAt = DateTimeOffset.UtcNow;

                if (policy.VerifyAfterWrite && policy.ReadAfterWriteDelayMs > 0)
                {
                    await Task.Delay(policy.ReadAfterWriteDelayMs).ConfigureAwait(false);
                    var verified = _provider.GetBrightness(id);
                    if (verified.HasValue && result.Status == DisplayBrightnessWriteStatuses.Applied)
                    {
                        result.Brightness = verified.Value;
                        result.AppliedBrightness = verified.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                result = new DisplayBrightnessDto
                {
                    Id = id,
                    RequestedBrightness = target,
                    AppliedBrightness = 0,
                    Brightness = 0,
                    Status = DisplayBrightnessWriteStatuses.Failed,
                    Error = ex.Message,
                };
            }

            foreach (var waiter in waiters)
            {
                waiter.TrySetResult(result);
            }
        }
    }

    private static int DelayBeforeNextWriteMs(DateTimeOffset lastWriteAt, int minIntervalMs)
    {
        if (minIntervalMs <= 0 || lastWriteAt == DateTimeOffset.MinValue)
            return 0;

        var elapsedMs = (int)(DateTimeOffset.UtcNow - lastWriteAt).TotalMilliseconds;
        return elapsedMs >= minIntervalMs ? 0 : minIntervalMs - elapsedMs;
    }

    private static int ClampPercent(int value) => value < 0 ? 0 : value > 100 ? 100 : value;

    /// <summary>
    /// True when <paramref name="displayId"/> is a promoted Xeneon Edge
    /// panel. Its DDC/CI brightness VCP drives the panel's Backlight
    /// register, not its Brightness register - both are now reachable
    /// through /displays/{id}/xeneon-settings, so the generic DDC path is
    /// retired for this family rather than left to silently fight it.
    /// </summary>
    private bool IsXeneonEdge(string displayId)
    {
        if (_panelDevices is null) return false;
        var record = _panelDevices.FindByDisplayId(displayId);
        return record is not null
            && string.Equals(record.Capabilities?.Family, KnownPanelDisplays.XeneonEdgeFamily, StringComparison.Ordinal);
    }

    /// <summary>
    /// True when <paramref name="displayId"/> is the Y70 panel's monitor. A
    /// raw DDC VCP 0x10 write is a no-op on the serial models (Infinite) and
    /// undercuts the gain path on the original Touch, so the generic DDC
    /// route never drives it; the Y70 provider owns the transport choice.
    /// </summary>
    private bool IsY70(string displayId)
        => _y70 is not null && _topology is not null && displayId.Length > 0
            && string.Equals(_topology.Y70DisplayId(), displayId, StringComparison.Ordinal);

    private void RouteThroughY70(DisplayDto display)
    {
        display.Capabilities.Brightness = true;
        display.BrightnessControl = new DisplayBrightnessControlDto
        {
            Supported = true,
            Current = _y70!.GetBrightness(),
            ControlPath = DisplayBrightnessControlPaths.Y70,
            WriteMode = DisplayBrightnessWriteModes.Immediate,
        };
    }

    private static void SuppressBrightnessControl(
        DisplayDto display,
        string reason = "Use this panel's native brightness/backlight controls instead.")
    {
        display.Capabilities.Brightness = false;
        display.BrightnessControl = new DisplayBrightnessControlDto
        {
            Supported = false,
            ControlPath = DisplayBrightnessControlPaths.Unsupported,
            WriteMode = DisplayBrightnessWriteModes.Unsupported,
            UnsupportedReason = reason,
        };
    }

    private sealed class DisplayWriteState
    {
        public object Gate { get; } = new();
        public int? PendingTarget { get; set; }
        public List<TaskCompletionSource<DisplayBrightnessDto>> Waiters { get; } = new();
        public Task? Worker { get; set; }
        public DateTimeOffset LastWriteAt { get; set; } = DateTimeOffset.MinValue;
    }
}
