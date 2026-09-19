using System;
using System.Collections.Generic;
using System.Linq;
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
/// reorders like alt+tab) and maintains the host-wide MRU ring in
/// RecentAppsState, the live in-memory authority every reader uses: a
/// reorder is visible the instant it happens, never waiting on a disk
/// write. Persistence to settings.json is a background concern, coalesced
/// to at most once per PersistInterval (plus a flush on shutdown) - writing
/// through IConfigStore.Update on every focus change would pulse every
/// OnChanged consumer (profile dirty-marking, settings.json rewrites) on
/// every alt+tab, most of which happen with no recentApps-mode instance
/// even watching. On Windows, FocusChanged fires synchronously from
/// HelperConnection.ReadLoopAsync (WindowsScreenTimeProvider.OnEnvelope ->
/// FocusChanged), so the focus handler itself never calls
/// IShortcutsProvider - a Windows HelperShortcutsProxy call blocks on a
/// reply that only that same read loop can deliver, which would deadlock
/// it against itself. Shortcut ids are resolved instead in Broadcast(),
/// which the coalesce timer runs on a threadpool thread; a resolve queued
/// there stays queued (never rebuilding the shortcuts index early) until
/// ResolveShortcutId's own ShortcutsRefreshInterval check next rebuilds it,
/// so a miss is negative-cached for that window rather than retried per
/// focus event.
/// </summary>
public sealed class RecentAppsService : BackgroundService
{
    private static readonly TimeSpan CoalesceDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(5);
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
    private ITimer? _persistTimer;
    private bool _subscribed;
    private volatile bool _dirty;

    private Dictionary<string, (string Id, string Name)> _shortcutsByProcessKey = new(StringComparer.Ordinal);
    private DateTimeOffset _shortcutsIndexedAt = DateTimeOffset.MinValue;
    private readonly HashSet<string> _pendingShortcutResolve = new(StringComparer.Ordinal);

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
        var settings = _store.Load().StreamDeck;
        // A pid from a previous run can be reused by an unrelated process by
        // the time this run starts, so it is never trusted across a restart.
        var seeded = settings.RecentApps.ConvertAll(a => new RecentApp
        {
            ProcessKey = a.ProcessKey,
            Name = a.Name,
            Pid = null,
            ExePath = a.ExePath,
            ShortcutId = a.ShortcutId,
            LastFocusedUtcMs = a.LastFocusedUtcMs,
        });
        _state.Seed(seeded, settings.RecentAppsExcluded);

        lock (_gate)
        {
            _subscribed = true;
            _persistTimer = _time.CreateTimer(_ => SafePersist(), null, PersistInterval, PersistInterval);
            // Re-resolve every seeded entry so a label or shortcut that was
            // missing (or stale) in the persisted ring is refreshed off the
            // focus thread on the first broadcast.
            foreach (var entry in seeded)
            {
                _pendingShortcutResolve.Add(entry.ProcessKey);
            }
        }
        _screenTime.FocusChanged += OnFocusChanged;
        stoppingToken.Register(Unsubscribe);
        // Catch an app that was already focused when the service started.
        OnFocusChanged();
        if (seeded.Count > 0)
        {
            ScheduleBroadcast();
        }
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
            _persistTimer?.Dispose();
            _persistTimer = null;
        }
        // A clean shutdown must not lose ring changes the periodic persist
        // has not caught up with yet.
        SafePersist();
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

        if (RecentAppsTracker.IsExcluded(processKey, _state.ExcludedSnapshot()))
        {
            // The desktop/shell itself holding focus is not "an app" for
            // Recent Apps purposes - no key is shown selected until a real
            // app takes focus again.
            _state.SetFocused(null);
            ScheduleBroadcast();
            return;
        }

        var details = _focusDetails?.GetCurrentFocusDetails();
        // ShortcutId starts unresolved: UpdateRing carries an already-known id
        // forward from the existing ring entry (RecentAppsTracker.UpdateRing),
        // and resolution for a genuinely new one runs in Broadcast(), off this
        // (possibly helper-read-loop) thread.
        var candidate = new RecentApp
        {
            ProcessKey = processKey,
            Name = focusedName,
            Pid = details?.Pid,
            ExePath = details?.ExePath,
            ShortcutId = null,
            LastFocusedUtcMs = _time.GetUtcNow().ToUnixTimeMilliseconds(),
        };

        if (_state.UpdateRing(candidate))
        {
            _dirty = true;
        }
        if (candidate.ShortcutId is null)
        {
            lock (_gate)
            {
                _pendingShortcutResolve.Add(processKey);
            }
        }
        _state.SetFocused(processKey);
        ScheduleBroadcast();
    }

    /// <summary>
    /// The Start-menu entry for a process key: its id launches the app and
    /// its name is the label a key shows ("Microsoft Edge", not "msedge").
    /// A miss stays a miss until ShortcutsRefreshInterval's own time check
    /// next rebuilds the index (no forced rebuild-on-miss) - that is the
    /// negative cache: a shortcut installed moments ago simply waits out the
    /// same window a genuinely unresolvable app does.
    /// </summary>
    private (string Id, string Name)? ResolveShortcut(string processKey)
    {
        var now = _time.GetUtcNow();
        if (now - _shortcutsIndexedAt >= ShortcutsRefreshInterval)
        {
            RebuildShortcutsIndex();
        }
        return _shortcutsByProcessKey.TryGetValue(processKey, out var hit) ? hit : null;
    }

    private void RebuildShortcutsIndex()
    {
        var hadShortcuts = _shortcutsByProcessKey.Count > 0;
        var index = new Dictionary<string, (string Id, string Name)>(StringComparer.Ordinal);
        foreach (var shortcut in _shortcuts.GetAll())
        {
            if (shortcut.ProcessName.Length == 0)
            {
                continue;
            }
            var key = AppPresetMatching.ProcessKey(shortcut.ProcessName);
            if (key.Length > 0 && !index.ContainsKey(key))
            {
                index[key] = (shortcut.Id, shortcut.Name);
            }
        }
        _shortcutsByProcessKey = index;
        _shortcutsIndexedAt = _time.GetUtcNow();
        // The first non-empty index usually arrives after the user-session
        // helper connects, i.e. after the seeded ring already missed once; give
        // every entry another look so labels and launch ids catch up.
        if (!hadShortcuts && index.Count > 0)
        {
            lock (_gate)
            {
                foreach (var entry in _state.RingSnapshot())
                {
                    _pendingShortcutResolve.Add(entry.ProcessKey);
                }
            }
            ScheduleBroadcast();
        }
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
        ResolvePendingShortcuts();

        PanelTopics.BroadcastDeck(_hub, new DeckChangedFrame
        {
            Kind = "recents",
            Apps = _state.RingSnapshot(),
            FocusedProcessKey = _state.FocusedProcessKey,
        });

        foreach (var (instanceId, instance) in _store.Load().StreamDeck.Instances)
        {
            if (instance.Mode != "recentApps" || !instanceId.StartsWith(DeckInstanceIdPrefix, StringComparison.Ordinal))
            {
                continue;
            }
            _worker.RefreshView(instanceId[DeckInstanceIdPrefix.Length..]);
        }
    }

    /// <summary>
    /// Resolves shortcut ids Tick() queued instead of resolving inline. The
    /// coalesce timer that calls this runs on a threadpool thread, never the
    /// helper's pipe read loop, so a Windows IShortcutsProvider.GetAll() round
    /// trip here cannot deadlock against WindowsScreenTimeProvider's own
    /// envelope handling.
    /// </summary>
    private void ResolvePendingShortcuts()
    {
        List<string> pending;
        lock (_gate)
        {
            if (_pendingShortcutResolve.Count == 0)
            {
                return;
            }
            pending = new List<string>(_pendingShortcutResolve);
            _pendingShortcutResolve.Clear();
        }
        foreach (var processKey in pending)
        {
            var resolved = ResolveShortcut(processKey);
            if (resolved is not null && _state.SetShortcut(processKey, resolved.Value.Id, resolved.Value.Name))
            {
                _dirty = true;
            }
        }
    }

    private void SafePersist()
    {
        try
        {
            Persist();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[recent-apps] persist failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes the live ring and excluded list to settings.json. force is
    /// used by the PUT/DELETE recent-apps routes to flush an explicit user
    /// edit immediately rather than waiting out PersistInterval; the
    /// periodic timer and shutdown flush call it unforced, a no-op unless a
    /// focus-driven ring change is pending AND some instance is actually in
    /// recentApps mode - every alt+tab on every host would otherwise pulse
    /// IConfigStore.OnChanged (profile dirty-marking, a settings.json
    /// rewrite) every PersistInterval regardless of whether Recent Apps is
    /// even in use. _dirty is left set when skipped for this reason, so the
    /// pending change persists as soon as an instance switches into the mode.
    /// </summary>
    internal void Persist(bool force = false)
    {
        if (!force)
        {
            if (!_dirty)
            {
                return;
            }
            if (!_store.Load().StreamDeck.Instances.Values.Any(i => i.Mode == "recentApps"))
            {
                return;
            }
        }
        _dirty = false;
        var ring = _state.RingSnapshot();
        var excluded = _state.ExcludedSnapshot();
        _store.Update(s =>
        {
            s.StreamDeck.RecentApps = ring;
            s.StreamDeck.RecentAppsExcluded = excluded;
        });
    }

    private const string DeckInstanceIdPrefix = "streamdeck:";
}
