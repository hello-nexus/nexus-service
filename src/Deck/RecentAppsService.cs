using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Activity;
using Nexus.Service.Lighting;
using Nexus.Service.Models.Deck;
using Nexus.Service.Peripherals.StreamDeck;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;

namespace Nexus.Service.Deck;

/// <summary>
/// Subscribes IScreenTimeProvider.FocusChanged (no dwell - Recent Apps
/// reorders like alt+tab) and maintains the host-wide MRU ring: resolves each
/// new entry's shortcutId once against a reverse index over
/// IShortcutsProvider.GetAll() (rebuilt at most every ShortcutsRefreshInterval,
/// or once more immediately on a miss), persists the ring, and coalesces the
/// "recents" deck-topic broadcast plus a RefreshView on every physical
/// instance in recentApps mode to CoalesceDelay after the last focus change,
/// so an alt+tab sweep writes HID once.
/// </summary>
public sealed class RecentAppsService : BackgroundService
{
    private static readonly TimeSpan CoalesceDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ShortcutsRefreshInterval = TimeSpan.FromSeconds(60);

    private readonly IConfigStore _store;
    private readonly IScreenTimeProvider _screenTime;
    private readonly IFocusDetailsProvider? _focusDetails;
    private readonly IShortcutsProvider _shortcuts;
    private readonly MultiplexHub _hub;
    private readonly StreamDeckConnectionWorker _worker;
    private readonly RecentAppsState _state;
    private readonly TimeProvider _time;

    private readonly object _gate = new();
    private ITimer? _coalesceTimer;
    private bool _subscribed;

    private Dictionary<string, string> _shortcutsByProcessKey = new(StringComparer.Ordinal);
    private DateTimeOffset _shortcutsIndexedAt = DateTimeOffset.MinValue;

    public RecentAppsService(
        IConfigStore store,
        IScreenTimeProvider screenTime,
        IShortcutsProvider shortcuts,
        MultiplexHub hub,
        StreamDeckConnectionWorker worker,
        RecentAppsState state,
        IFocusDetailsProvider? focusDetails = null,
        TimeProvider? time = null)
    {
        _store = store;
        _screenTime = screenTime;
        _shortcuts = shortcuts;
        _hub = hub;
        _worker = worker;
        _state = state;
        _focusDetails = focusDetails;
        _time = time ?? TimeProvider.System;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        lock (_gate)
        {
            _subscribed = true;
        }
        _screenTime.FocusChanged += OnFocusChanged;
        stoppingToken.Register(Unsubscribe);
        // Catch an app that was already focused when the service started.
        OnFocusChanged();
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
            _coalesceTimer?.Dispose();
            _coalesceTimer = null;
        }
    }

    public override void Dispose()
    {
        Unsubscribe();
        base.Dispose();
    }

    private void OnFocusChanged()
    {
        try
        {
            Tick();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[recent-apps] focus handling failed: {ex.Message}");
        }
    }

    internal void Tick()
    {
        var session = _screenTime.GetCurrentSession();
        var focusedName = session?.Name ?? "";
        var processKey = AppPresetMatching.ProcessKey(focusedName);
        if (processKey.Length == 0)
        {
            return;
        }

        var excluded = _store.Load().StreamDeck.RecentAppsExcluded;
        if (RecentAppsTracker.IsExcluded(processKey, excluded))
        {
            // The desktop/shell itself holding focus is not "an app" for
            // Recent Apps purposes - no key is shown selected until a real
            // app takes focus again.
            _state.SetFocused(null);
            ScheduleBroadcast();
            return;
        }

        var details = _focusDetails?.GetCurrentFocusDetails();
        var candidate = new RecentApp
        {
            ProcessKey = processKey,
            Name = focusedName,
            Pid = details?.Pid,
            ExePath = details?.ExePath,
            ShortcutId = ResolveShortcutId(processKey),
            LastFocusedUtcMs = _time.GetUtcNow().ToUnixTimeMilliseconds(),
        };

        _store.Update(s => RecentAppsTracker.UpdateRing(s.StreamDeck.RecentApps, candidate, s.StreamDeck.RecentAppsExcluded));
        _state.SetFocused(processKey);
        ScheduleBroadcast();
    }

    private string? ResolveShortcutId(string processKey)
    {
        var now = _time.GetUtcNow();
        if (now - _shortcutsIndexedAt >= ShortcutsRefreshInterval)
        {
            RebuildShortcutsIndex();
        }
        if (_shortcutsByProcessKey.TryGetValue(processKey, out var id))
        {
            return id;
        }
        // A miss can mean the index is stale (a shortcut installed since the
        // last rebuild) rather than truly unresolvable - rebuild once more
        // before giving up for this focus event.
        RebuildShortcutsIndex();
        return _shortcutsByProcessKey.TryGetValue(processKey, out var retried) ? retried : null;
    }

    private void RebuildShortcutsIndex()
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var shortcut in _shortcuts.GetAll())
        {
            if (shortcut.ProcessName.Length == 0)
            {
                continue;
            }
            var key = AppPresetMatching.ProcessKey(shortcut.ProcessName);
            if (key.Length > 0 && !index.ContainsKey(key))
            {
                index[key] = shortcut.Id;
            }
        }
        _shortcutsByProcessKey = index;
        _shortcutsIndexedAt = _time.GetUtcNow();
    }

    private void ScheduleBroadcast()
    {
        lock (_gate)
        {
            if (!_subscribed)
            {
                return;
            }
            _coalesceTimer?.Dispose();
            _coalesceTimer = _time.CreateTimer(_ => SafeBroadcast(), null, CoalesceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void SafeBroadcast()
    {
        try
        {
            Broadcast();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[recent-apps] broadcast failed: {ex.Message}");
        }
    }

    internal void Broadcast()
    {
        var settings = _store.Load().StreamDeck;
        PanelTopics.BroadcastDeck(_hub, new DeckChangedFrame
        {
            Kind = "recents",
            Apps = new List<RecentApp>(settings.RecentApps),
            FocusedProcessKey = _state.FocusedProcessKey,
        });

        foreach (var (instanceId, instance) in settings.Instances)
        {
            if (instance.Mode != "recentApps" || !instanceId.StartsWith(DeckInstanceIdPrefix, StringComparison.Ordinal))
            {
                continue;
            }
            _worker.RefreshView(instanceId[DeckInstanceIdPrefix.Length..]);
        }
    }

    private const string DeckInstanceIdPrefix = "streamdeck:";
}
