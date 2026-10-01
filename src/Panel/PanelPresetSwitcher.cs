using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Activity;
using Nexus.Service.Lighting;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;

namespace Nexus.Service.Panel;

/// <summary>
/// Loads a panel preset when an app it is bound to takes focus, and restores
/// the preset that was loaded before once focus moves to an unbound app. Same
/// dwell timer and <see cref="AppPresetFocusTracker"/> as
/// <see cref="AppPresetSwitcher"/>, with one tracker per panel, as
/// <see cref="Nexus.Service.Deck.DeckAppPresetSwitcher"/> keeps one per deck.
/// </summary>
public sealed class PanelPresetSwitcher : BackgroundService
{
    private static readonly TimeSpan DwellSlack = TimeSpan.FromMilliseconds(50);

    private readonly IConfigStore _store;
    private readonly IScreenTimeProvider _screenTime;
    private readonly PanelDeviceRegistry _registry;
    private readonly MultiplexHub _hub;
    private readonly TimeProvider _time;

    private readonly Dictionary<string, AppPresetFocusTracker> _trackers = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    // Every evaluation runs under this: the focus thread, the dwell timer and
    // any thread that writes the store can all signal.
    private readonly object _tickGate = new();
    private ITimer? _promptTimer;
    private ITimer? _dwellTimer;
    private bool _subscribed;

    public PanelPresetSwitcher(
        IConfigStore store,
        IScreenTimeProvider screenTime,
        PanelDeviceRegistry registry,
        MultiplexHub hub,
        TimeProvider? time = null)
    {
        _store = store;
        _screenTime = screenTime;
        _registry = registry;
        _hub = hub;
        _time = time ?? TimeProvider.System;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        lock (_gate)
        {
            _subscribed = true;
        }
        _screenTime.FocusChanged += OnFocusChanged;
        // A binding saved while its app already holds focus produces no focus
        // event, so the store change is the other trigger.
        _store.OnChanged += OnStoreChanged;
        stoppingToken.Register(Unsubscribe);
        // Catch an app that was already focused when the service started.
        Schedule();
        return Task.CompletedTask;
    }

    private void Unsubscribe()
    {
        lock (_gate)
        {
            if (!_subscribed)
                return;
            _subscribed = false;
            _screenTime.FocusChanged -= OnFocusChanged;
            _store.OnChanged -= OnStoreChanged;
            _promptTimer?.Dispose();
            _promptTimer = null;
            _dwellTimer?.Dispose();
            _dwellTimer = null;
        }
    }

    public override void Dispose()
    {
        Unsubscribe();
        base.Dispose();
    }

    private void OnFocusChanged() => Schedule();

    private void OnStoreChanged() => Schedule();

    /// <summary>Evaluates once to record the candidate and again once the dwell
    /// has elapsed to act on it, both on the timer's thread: activation writes
    /// the store, which must not happen on a signalling route thread.</summary>
    private void Schedule()
    {
        lock (_gate)
        {
            if (!_subscribed)
                return;
            _promptTimer?.Dispose();
            _promptTimer = _time.CreateTimer(_ => SafeTick(), null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            _dwellTimer?.Dispose();
            _dwellTimer = _time.CreateTimer(
                _ => SafeTick(), null, AppPresetFocusTracker.Dwell + DwellSlack, Timeout.InfiniteTimeSpan);
        }
    }

    private void SafeTick()
    {
        try
        {
            lock (_tickGate)
            {
                Tick();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[panel-presets] evaluation failed: {ex.Message}");
        }
    }

    internal void Tick()
    {
        var focused = _screenTime.GetCurrentSession()?.Name ?? "";
        var now = MonotonicNowMs();

        var boundPanels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (panelId, presets, activeId) in SnapshotBoundPanels(_store.Load()))
        {
            boundPanels.Add(panelId);
            if (!_trackers.TryGetValue(panelId, out var tracker))
            {
                tracker = new AppPresetFocusTracker();
                _trackers[panelId] = tracker;
            }
            var target = tracker.Decide(focused, activeId, presets, now);
            if (target is not null && _registry.ActivatePreset(panelId, target) is not null)
                PanelTopics.BroadcastPanelDevice(_hub, panelId);
        }

        // A panel whose bindings are gone (or that was removed) drops its
        // tracker: a pending restore would otherwise bring back an arbitrarily
        // stale preset once bindings return.
        foreach (var staleId in _trackers.Keys.Where(id => !boundPanels.Contains(id)).ToList())
            _trackers.Remove(staleId);
    }

    // Monotonic: an NTP correction must not stall the dwell (backward) or short-circuit it (forward).
    private long MonotonicNowMs() =>
        (long)(_time.GetTimestamp() / (double)_time.TimestampFrequency * 1000.0);

    /// <summary>Panels with an app binding and a preset loaded: with none loaded, a switch would discard live
    /// personalization no preset holds. Copied because the store's update lock may be held elsewhere.</summary>
    private static List<(string PanelId, List<PanelPreset> Presets, string? ActiveId)> SnapshotBoundPanels(NexusSettings settings)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var result = new List<(string, List<PanelPreset>, string?)>();
                foreach (var (id, record) in settings.PanelDevices)
                {
                    if (record.ActivePresetId is not null
                        && record.Presets is { } presets
                        && presets.Any(p => p.Apps is { Count: > 0 }))
                    {
                        result.Add((id, new List<PanelPreset>(presets), record.ActivePresetId));
                    }
                }
                return result;
            }
            catch (InvalidOperationException) when (attempt < 2)
            {
            }
        }
    }
}
