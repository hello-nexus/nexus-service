using System.Collections.Concurrent;
using Nexus.Service.Models.Displays;
using Nexus.Service.Panel;

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
    private readonly ConcurrentDictionary<string, DisplayWriteState> _states = new();

    public DisplayBrightnessController(IDisplayBrightnessProvider provider, PanelDeviceRegistry? panelDevices = null)
    {
        _provider = provider;
        _panelDevices = panelDevices;
    }

    public DisplayListResponse ListDisplays()
    {
        var displays = new List<DisplayDto>(_provider.Enumerate());
        foreach (var display in displays)
        {
            if (IsXeneonEdge(display.Id)) SuppressBrightnessControl(display);
        }
        return new DisplayListResponse
        {
            Displays = displays,
            Hint = displays.Count == 0 ? _provider.Hint : "",
        };
    }

    public int? GetBrightness(string id) => IsXeneonEdge(id) ? null : _provider.GetBrightness(id);

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

        var state = _states.GetOrAdd(id, _ => new DisplayWriteState());
        var requested = ClampPercent(percent);
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

    private static void SuppressBrightnessControl(DisplayDto display)
    {
        display.Capabilities.Brightness = false;
        display.BrightnessControl = new DisplayBrightnessControlDto
        {
            Supported = false,
            ControlPath = DisplayBrightnessControlPaths.Unsupported,
            WriteMode = DisplayBrightnessWriteModes.Unsupported,
            UnsupportedReason = "Use this panel's native brightness/backlight controls instead.",
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
