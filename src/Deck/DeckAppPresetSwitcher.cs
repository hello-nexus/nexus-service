using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Activity;
using Nexus.Service.Lighting;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;

namespace Nexus.Service.Deck;

/// <summary>
/// Activates a deck preset when an app it is bound to takes focus, mirroring
/// AppPresetSwitcher's dwell timer and restore-previous state machine (same
/// AppPresetFocusTracker, generalized over IAppBoundPreset) but with one
/// tracker per physical or widget instance in appAware mode instead of a
/// single global one - each instance can be bound to a different set of
/// presets. Paused while a deck editor is open (a streamdeckTiles or
/// deck-edit subscriber), so an in-progress edit is never yanked away by a
/// focus change; the last editor closing runs one settle pass in case focus
/// changed while paused.
/// </summary>
public sealed class DeckAppPresetSwitcher : BackgroundService
{
    private static readonly TimeSpan DwellSlack = TimeSpan.FromMilliseconds(50);

    private readonly IConfigStore _store;
    private readonly IScreenTimeProvider _screenTime;
    private readonly MultiplexHub _hub;
    private readonly DeckPresetActivator _activator;
    private readonly TimeProvider _time;

    private readonly Dictionary<string, AppPresetFocusTracker> _trackers = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    // Every evaluation runs under this, matching AppPresetSwitcher's _tickGate:
    // the focus thread, the dwell timer, a store write, and a topic
    // subscriber-count transition can all signal.
    private readonly object _tickGate = new();
    private ITimer? _promptTimer;
    private ITimer? _dwellTimer;
    private bool _subscribed;

    public DeckAppPresetSwitcher(
        IConfigStore store,
        IScreenTimeProvider screenTime,
        MultiplexHub hub,
        DeckPresetActivator activator,
        TimeProvider? time = null)
    {
        _store = store;
        _screenTime = screenTime;
        _hub = hub;
        _activator = activator;
        _time = time ?? TimeProvider.System;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        lock (_gate)
        {
            _subscribed = true;
        }
        _screenTime.FocusChanged += OnFocusChanged;
        _store.OnChanged += OnStoreChanged;
        _hub.OnTopicLastUnsubscriber += OnTopicLastUnsubscriber;
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
            {
                return;
            }
            _subscribed = false;
            _screenTime.FocusChanged -= OnFocusChanged;
            _store.OnChanged -= OnStoreChanged;
            _hub.OnTopicLastUnsubscriber -= OnTopicLastUnsubscriber;
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

    /// <summary>Runs a settle pass the moment the last deck editor closes, in case focus moved while switching was paused.</summary>
    private void OnTopicLastUnsubscriber(string topic)
    {
        if (topic is PanelTopics.DeckEdit or PanelTopics.StreamDeckTiles)
        {
            Schedule();
        }
    }

    private void Schedule()
    {
        lock (_gate)
        {
            if (!_subscribed)
            {
                return;
            }
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
            Console.Error.WriteLine($"[deck-app-presets] evaluation failed: {ex.Message}");
        }
    }

    internal void Tick()
    {
        // An open editor (device-page live tiles, or a widget's edit sheet)
        // must never have its preset yanked out from under it mid-edit.
        if (_hub.TopicHasSubscribers(PanelTopics.StreamDeckTiles) || _hub.TopicHasSubscribers(PanelTopics.DeckEdit))
        {
            return;
        }

        var settings = _store.Load();
        var presets = SnapshotPresets(settings);
        var anyBindings = AnyBindings(presets);
        var focused = _screenTime.GetCurrentSession()?.Name ?? "";
        var now = MonotonicNowMs();

        var appAwareInstanceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (instanceId, instance) in SnapshotInstances(settings))
        {
            if (instance.Mode != "appAware")
            {
                continue;
            }
            appAwareInstanceIds.Add(instanceId);

            if (!anyBindings)
            {
                // Not just an early-out: a pending restore target refers to a
                // binding that no longer exists, and holding it would restore
                // an arbitrarily stale preset once bindings come back.
                if (_trackers.Remove(instanceId, out var dropped))
                {
                    dropped.Reset();
                }
                continue;
            }

            if (!_trackers.TryGetValue(instanceId, out var tracker))
            {
                tracker = new AppPresetFocusTracker();
                _trackers[instanceId] = tracker;
            }

            var target = tracker.Decide(focused, instance.ActivePresetId, presets, now);
            if (target is null)
            {
                continue;
            }
            _activator.Activate(instanceId, target);
        }

        // An instance that left appAware mode (manual switch, or the instance
        // was removed entirely) drops its tracker instead of resuming a stale
        // restore-previous state if it returns to appAware later.
        foreach (var staleId in _trackers.Keys.Where(id => !appAwareInstanceIds.Contains(id)).ToList())
        {
            _trackers.Remove(staleId);
        }
    }

    // Monotonic: an NTP correction must not stall the dwell (backward) or short-circuit it (forward).
    private long MonotonicNowMs() =>
        (long)(_time.GetTimestamp() / (double)_time.TimestampFrequency * 1000.0);

    /// <summary>Copies the preset list before reading it outside the store's update lock, which another thread can be inside.</summary>
    private static List<DeckPreset> SnapshotPresets(NexusSettings settings)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new List<DeckPreset>(settings.StreamDeck.Presets);
            }
            catch (InvalidOperationException) when (attempt < 2)
            {
            }
        }
    }

    private static List<KeyValuePair<string, DeckInstance>> SnapshotInstances(NexusSettings settings)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return settings.StreamDeck.Instances.ToList();
            }
            catch (InvalidOperationException) when (attempt < 2)
            {
            }
        }
    }

    private static bool AnyBindings(List<DeckPreset> presets)
    {
        foreach (var preset in presets)
        {
            if (preset.Apps is { Count: > 0 })
            {
                return true;
            }
        }
        return false;
    }
}
