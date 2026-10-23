using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Lighting;
using Nexus.Service.Models.Panel;
using Nexus.Service.Platform;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Owns streamed-panel sessions end to end: polls each registered
/// <see cref="IStreamedPanelDiscovery"/>, allocates/reuses the panel device
/// record per serial, opens the device transport, publishes desired sessions
/// for the overlay's render engine (GET /panel/streams/assignments), binds
/// the overlay's ingest connection, and runs one <see cref="PacedStreamWriter"/>
/// per session. Device presence is runtime state: it never touches
/// NexusSettings, and overlay lifetime is handled here (Start when sessions
/// exist; teardown rides the overlay's idle-exit).
/// </summary>
public sealed class StreamedPanelCoordinator : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    // Absorbs the D213's known bus drops on host power events without
    // tearing down the render host; past this the assignment is unpublished.
    private static readonly TimeSpan DetachLinger = TimeSpan.FromSeconds(60);

    private sealed class DeviceSession
    {
        public required StreamSession Session { get; init; }
        public required PacedStreamWriter Writer { get; init; }
        public required IStreamedPanelDiscovery Discovery { get; init; }
        public required StreamedPanelDeviceInfo Info { get; init; }
        public IStreamedPanelTransport? Transport { get; set; }
        public long LastPresentAtMs { get; set; }
        public HttpContext? IngestContext { get; set; }
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, DeviceSession> _bySerial = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeviceSession> _bySessionId = new(StringComparer.Ordinal);
    // Panel record ids listed as present while their device holds the glass.
    private readonly Dictionary<IStreamedPanelDiscovery, string> _heldByDevice = new();
    private readonly IReadOnlyList<IStreamedPanelDiscovery> _discoveries;
    private readonly StreamedPanelStore _store;
    private readonly PanelDeviceRegistry _registry;
    private readonly Func<string, bool> _removePanel;
    private readonly Func<string, bool> _isCustomized;
    private readonly DeviceControlGate _gate;
    private readonly Action? _notifyOverlay;
    private readonly Action<string>? _notifyPanelChanged;
    private readonly Func<long> _nowMs;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly LcdLockState? _lockState;
    // Lock fades in flight, per panel record id. A leaf lock: the brightness getter reads it
    // from frame threads, so nothing else is ever taken while holding it.
    private readonly object _fadeGate = new();
    private readonly Dictionary<string, LcdFade> _fades = new(StringComparer.Ordinal);
    internal LcdFadeRunner FadeRunner { get; }
    private readonly LcdLockSleepSet? _lockSleep;

    /// <summary>Backlight percent used only when a panel with no stored value never revealed its pre-lock level.</summary>
    internal const int UnlockDefaultBrightness = 100;

    public StreamedPanelCoordinator(
        IEnumerable<IStreamedPanelDiscovery> discoveries,
        StreamedPanelStore store,
        PanelDeviceRegistry registry,
        DeviceControlGate gate,
        Action? notifyOverlay = null,
        Func<long>? nowMs = null,
        Action<string>? notifyPanelChanged = null,
        LcdLockState? lockState = null,
        Func<string, bool>? removePanel = null,
        Func<string, bool>? isCustomized = null)
    {
        _removePanel = removePanel ?? registry.Remove;
        _isCustomized = isCustomized ?? registry.IsCustomized;
        _discoveries = discoveries.ToList();
        _store = store;
        _registry = registry;
        _gate = gate;
        _notifyOverlay = notifyOverlay;
        _notifyPanelChanged = notifyPanelChanged;
        _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        FadeRunner = new LcdFadeRunner(StepFades, onFault: FaultAllFades);
        if (lockState is not null)
        {
            _lockState = lockState;
            _lockSleep = new LcdLockSleepSet(lockState);
            lockState.Changed += OnLockChanged;
        }
    }

    public override void Dispose()
    {
        _lockState?.Changed -= OnLockChanged;
        base.Dispose();
    }

    /// <summary>
    /// True while the session lock holds this panel dark. Decided once per lock from the
    /// record's SleepWhenLocked (null = on), only for panels whose profile can dim.
    /// </summary>
    public bool IsLockSleeping(string panelDeviceId)
    {
        if (_lockSleep is null)
        {
            return false;
        }
        // The decision callback runs on frame threads holding a transport's brightness lock,
        // so it must never take the coordinator lock: ApplyBrightness takes that lock first
        // and the transport's second.
        return _lockSleep.IsSleeping(panelDeviceId, () =>
        {
            var record = _registry.Get(panelDeviceId);
            var on = record?.SleepWhenLocked != false && record?.Capabilities?.SupportsBrightness == true;
            if (on)
            {
                ServiceLog.Info($"[lcd-lock] streamed panel {panelDeviceId} sleeping");
            }
            return on;
        });
    }

    // Takes no coordinator lock; see IsLockSleeping.
    private int? EffectiveBrightness(string panelDeviceId)
    {
        // A fade in flight answers with its stepped level; a finished one falls through to the
        // settled answer, which is the same number.
        lock (_fadeGate)
        {
            if (_fades.TryGetValue(panelDeviceId, out var fade))
            {
                var now = _nowMs();
                if (!fade.IsDoneAt(now))
                {
                    return fade.LevelAt(now);
                }
            }
        }
        if (IsLockSleeping(panelDeviceId))
        {
            return 0;
        }
        var record = _registry.Get(panelDeviceId);
        return record?.LcdBrightness ?? record?.LockRestoreBrightness;
    }

    private IBrightnessPanelTransport? DimmableTransport(string panelDeviceId)
    {
        lock (_lock)
        {
            foreach (var ds in _bySerial.Values)
            {
                if (!ds.Session.Closed && string.Equals(ds.Session.PanelDeviceId, panelDeviceId, StringComparison.Ordinal))
                {
                    return ds.Transport as IBrightnessPanelTransport;
                }
            }
        }
        return null;
    }

    private LcdFade? ActiveFade(string panelDeviceId)
    {
        lock (_fadeGate)
        {
            return _fades.TryGetValue(panelDeviceId, out var fade) && !fade.IsDoneAt(_nowMs()) ? fade : null;
        }
    }

    private void RemoveFade(string panelDeviceId)
    {
        lock (_fadeGate) { _fades.Remove(panelDeviceId); }
    }

    private void BeginFade(string panelDeviceId, int from, int to, TimeSpan duration)
    {
        if (from == to)
        {
            RemoveFade(panelDeviceId);
            return;
        }
        lock (_fadeGate) { _fades[panelDeviceId] = new LcdFade(from, to, duration, _nowMs()); }
        FadeRunner.Kick();
    }

    /// <summary>True while the lock holds this panel dark and its fade down has finished: rendering stops only then.</summary>
    public bool IsLockDark(string panelDeviceId) =>
        IsLockSleeping(panelDeviceId) && ActiveFade(panelDeviceId) is not { Down: true };

    /// <summary>One sample of every fade: applies each panel's stepped level, finishes the ones that landed.</summary>
    internal bool StepFades()
    {
        var now = _nowMs();
        List<(string Id, bool Up)> finished = new();
        List<string> ids;
        lock (_fadeGate)
        {
            ids = _fades.Keys.ToList();
            foreach (var id in ids)
            {
                if (_fades[id].IsDoneAt(now))
                {
                    finished.Add((id, !_fades[id].Down));
                    _fades.Remove(id);
                }
            }
        }
        foreach (var id in ids)
        {
            try
            {
                var applied = ApplyBrightness(id);
                foreach (var (doneId, up) in finished)
                {
                    // Cleared only once the panel confirmed the level it was returning to.
                    if (doneId == id && up && applied && !IsLockSleeping(id))
                    {
                        SetMarker(id, null);
                    }
                }
            }
            catch (Exception ex)
            {
                // One panel's failure drops only its own ramp.
                ServiceLog.Warn($"[lcd-lock] fade step failed for panel {id}: {ex.GetType().Name}: {ex.Message}");
                FaultPanelFade(id);
            }
        }
        if (finished.Any(f => !f.Up))
        {
            _notifyOverlay?.Invoke();
        }
        lock (_fadeGate) { return _fades.Count > 0; }
    }

    // The ramp is dropped and the panel gets its settled level (dark while the lock holds it, else the
    // level it was returning to), so a failed step does not leave it lit mid-ramp.
    private void FaultPanelFade(string panelDeviceId)
    {
        RemoveFade(panelDeviceId);
        try
        {
            if (ApplyBrightness(panelDeviceId) && !IsLockSleeping(panelDeviceId))
            {
                SetMarker(panelDeviceId, null);
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[lcd-lock] settling panel {panelDeviceId} after a failed fade: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void FaultAllFades()
    {
        List<string> ids;
        lock (_fadeGate) { ids = _fades.Keys.ToList(); }
        foreach (var id in ids)
        {
            FaultPanelFade(id);
        }
    }

    // A panel with no stored backlight has no value to return to after the lock, so the
    // level it had before is recorded on its record: restored on unlock, and still there
    // if the service restarts while the glass is dark.
    private readonly object _markerGate = new();

    /// <summary>Test seam: runs between the lock path's dark apply and its marker write.</summary>
    internal Action? TestHookBeforeMarkerWrite { get; set; }

    // Read BEFORE the dark apply, from what the transport last put on the glass. Previous is
    // only a fallback for a decision a frame thread already acted on, and a zero never
    // becomes a restore level.
    private int? CaptureRestoreLevel(string panelDeviceId)
    {
        var record = _registry.Get(panelDeviceId);
        if (record is null || record.LcdBrightness is not null || record.LockRestoreBrightness is not null)
        {
            return null;
        }
        var transport = DimmableTransport(panelDeviceId);
        if (transport?.CurrentBrightness is > 0 and var current)
        {
            return current;
        }
        if (transport?.PreviousBrightness is > 0 and var previous)
        {
            return previous;
        }
        return UnlockDefaultBrightness;
    }

    private void SetMarker(string panelDeviceId, int? level)
    {
        lock (_markerGate)
        {
            _registry.SetLockRestoreBrightness(panelDeviceId, level);
        }
    }

    // The lock path writes only while the lock it started under is still the current one.
    private void WriteMarkerForLock(string panelDeviceId, int? level, int epoch)
    {
        if (level is not > 0)
        {
            return;
        }
        TestHookBeforeMarkerWrite?.Invoke();
        lock (_markerGate)
        {
            if (_lockState is { IsLocked: true } state && state.Epoch == epoch)
            {
                _registry.SetLockRestoreBrightness(panelDeviceId, level);
            }
        }
    }

    internal void OnLockChanged(bool locked)
    {
        List<string> panels;
        lock (_lock)
        {
            panels = _bySerial.Values
                .Where(ds => !ds.Session.Closed && ds.Info.Profile.SupportsBrightness)
                .Select(ds => ds.Session.PanelDeviceId)
                .ToList();
        }
        if (locked)
        {
            foreach (var id in panels)
            {
                if (IsLockSleeping(id))
                {
                    var epoch = _lockState?.Epoch ?? 0;
                    var level = CaptureRestoreLevel(id);
                    // The level to come back to is fixed here, before the first step: an up-fade
                    // caught mid-way reverses from where it is, never from an intermediate level.
                    var record = _registry.Get(id);
                    var remembered = record?.LcdBrightness ?? record?.LockRestoreBrightness ?? level ?? UnlockDefaultBrightness;
                    var from = ActiveFade(id) is { Down: false } rising ? rising.LevelAt(_nowMs()) : remembered;
                    WriteMarkerForLock(id, level, epoch);
                    if (_lockState is { IsLocked: true } current && current.Epoch == epoch)
                    {
                        BeginFade(id, from, 0, SleepBlackoutCoordinator.LockFadeDuration);
                    }
                    ApplyBrightness(id);
                }
            }
        }
        else
        {
            var woken = _lockSleep?.Drain() ?? new List<string>();
            if (woken.Count > 0)
            {
                ServiceLog.Info($"[lcd-lock] unlocked; waking {woken.Count} streamed panel(s)");
            }
            foreach (var id in woken)
            {
                // A panel that attached mid-lock has no marker yet.
                if (CaptureRestoreLevel(id) is int level)
                {
                    SetMarker(id, level);
                }
            }
            // Only panels this lock slept come back; one it left alone is not touched.
            foreach (var id in woken)
            {
                var record = _registry.Get(id);
                var target = record?.LcdBrightness ?? record?.LockRestoreBrightness;
                var current = DimmableTransport(id)?.CurrentBrightness;
                // Up from 0, or from wherever a fade down was caught; a panel the lock never dimmed
                // stays where it is.
                var from = ActiveFade(id) is { Down: true } falling ? falling.LevelAt(_nowMs()) : current ?? 0;
                if (target is int wanted && wanted > 0 && from != wanted)
                {
                    BeginFade(id, from, wanted, SleepBlackoutCoordinator.UnlockFadeDuration);
                }
                else
                {
                    RemoveFade(id);
                }
                // Cleared only after the panel confirmed the write; a failed one keeps the marker
                // for the next apply or connect to retry. A fade up clears it when it lands.
                if (ActiveFade(id) is null && ApplyBrightness(id) && !IsLockSleeping(id))
                {
                    SetMarker(id, null);
                }
                else if (ActiveFade(id) is not null)
                {
                    ApplyBrightness(id);
                }
            }
        }
        _notifyOverlay?.Invoke();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_discoveries.Count == 0) return;

        try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                TickOnce();
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[streamed-panel] tick failed: {ex.GetType().Name}: {ex.Message}");
            }

            try { await _wake.WaitAsync(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        CloseAll("service stopping");
    }

    /// <summary>Runs the next discovery pass now instead of at the end of the poll interval.</summary>
    public void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    internal void TickOnce()
    {
        var changed = false;
        foreach (var discovery in _discoveries)
        {
            var enabled = _gate.IsEnabled(discovery.HandlerId);
            var withheld = enabled && discovery.Withheld;
            TrackHeld(discovery, withheld);
            IReadOnlyList<StreamedPanelDeviceInfo> devices;
            if (!enabled || withheld)
            {
                devices = Array.Empty<StreamedPanelDeviceInfo>();
            }
            else
            {
                try
                {
                    devices = discovery.Discover();
                }
                catch (Exception ex)
                {
                    ServiceLog.Error($"[streamed-panel] discover failed ({discovery.HandlerId}): {ex.GetType().Name}: {ex.Message}");
                    continue;
                }
            }

            var now = _nowMs();
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var info in devices) present.Add(info.Serial);

            foreach (var found in devices)
            {
                DeviceSession? existing;
                lock (_lock) _bySerial.TryGetValue(found.Serial, out existing);
                var info = existing is null ? found : WithRenderScale(found, existing.Session.PanelDeviceId);
                string? remintedPanelId = null;
                if (existing is not null && !ProfilesEqual(existing.Info.Profile, info.Profile))
                {
                    // Config changes re-mint the session (fresh sessionId) so
                    // the overlay's reconcile is a pure spawn/close diff. Clients
                    // hear about it once the new session is live: a panel list read
                    // between the two drops the panel and remounts its device page.
                    CloseSession(existing, "profile changed", notifyClients: false);
                    remintedPanelId = existing.Session.PanelDeviceId;
                    existing = null;
                    changed = true;
                }
                if (existing is not null)
                {
                    existing.LastPresentAtMs = now;
                    bool needsReopen;
                    lock (_lock)
                    {
                        var transport = existing.Transport;
                        needsReopen = transport is null || !transport.IsOpen;
                    }
                    if (needsReopen)
                        TryReopenTransport(existing);
                }
                else if (StartSession(discovery, found, now))
                {
                    changed = true;
                }
                else if (remintedPanelId is not null)
                {
                    _notifyPanelChanged?.Invoke(remintedPanelId);
                }
            }

            List<DeviceSession> absent;
            lock (_lock)
            {
                absent = _bySerial.Values
                    .Where(ds => ReferenceEquals(ds.Discovery, discovery) && !present.Contains(ds.Info.Serial))
                    .ToList();
            }
            foreach (var ds in absent)
            {
                if (!enabled)
                {
                    CloseSession(ds, "control gate off");
                    changed = true;
                }
                else if (withheld)
                {
                    CloseSession(ds, "screen handed to the device");
                    changed = true;
                }
                else if (now - ds.LastPresentAtMs > DetachLinger.TotalMilliseconds)
                {
                    CloseSession(ds, "detached past linger");
                    changed = true;
                }
            }
        }

        if (changed)
            _notifyOverlay?.Invoke();
    }

    /// <summary>
    /// Panel record ids currently owned by a live stream session, or whose glass their device
    /// holds (<see cref="IStreamedPanelDiscovery.ListedWhileWithheld"/>). GET /panel/devices
    /// stamps these so the dashboard can list a streamed panel: it is backed by neither a
    /// curated device nor a display, so nothing else marks it as present and editable.
    /// </summary>
    public HashSet<string> LivePanelDeviceIds()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        lock (_lock)
        {
            foreach (var ds in _bySerial.Values)
            {
                if (!ds.Session.Closed)
                    ids.Add(ds.Session.PanelDeviceId);
            }
            ids.UnionWith(_heldByDevice.Values);
        }
        return ids;
    }

    /// <summary>Applies a persisted panel backlight change without waiting for a frame.</summary>
    public bool ApplyBrightness(string panelDeviceId)
    {
        lock (_lock)
        {
            foreach (var ds in _bySerial.Values)
            {
                if (ds.Session.Closed || !string.Equals(ds.Session.PanelDeviceId, panelDeviceId, StringComparison.Ordinal))
                    continue;
                if (ds.Transport is not IBrightnessPanelTransport dimmable)
                    return false;
                return dimmable.ApplyBrightness();
            }
        }
        return false;
    }

    /// <summary>
    /// Starts or stops the Windows monitor after a settings edit, then re-publishes the
    /// assignments: a panel showing the desktop gets no render host. Stopping joins the feed's
    /// threads, so it runs off the caller's thread.
    /// </summary>
    public void ApplySecondaryMonitor(string panelDeviceId)
    {
        ISecondaryMonitorTransport? transport = null;
        lock (_lock)
        {
            foreach (var ds in _bySerial.Values)
            {
                if (!ds.Session.Closed && string.Equals(ds.Session.PanelDeviceId, panelDeviceId, StringComparison.Ordinal))
                {
                    transport = ds.Transport as ISecondaryMonitorTransport;
                    break;
                }
            }
        }
        _notifyOverlay?.Invoke();
        if (transport is not null)
        {
            _ = Task.Run(transport.ApplySecondaryMonitor);
        }
    }

    /// <summary>Monitor state per panel record id, for the records whose monitor is on.</summary>
    public Dictionary<string, string> SecondaryMonitorStates()
    {
        var states = new Dictionary<string, string>(StringComparer.Ordinal);
        lock (_lock)
        {
            foreach (var ds in _bySerial.Values)
            {
                if (!ds.Session.Closed && ds.Transport is ISecondaryMonitorTransport { SecondaryMonitorState: { } state })
                    states[ds.Session.PanelDeviceId] = state;
            }
        }
        return states;
    }

    private bool ShowsSecondaryMonitor(DeviceSession ds) =>
        ds.Info.Profile.SupportsSecondaryMonitor
        && _registry.Get(ds.Session.PanelDeviceId)?.SecondaryMonitor == true;

    /// <summary>Suppresses assignments while a focus mode asks for rendering to stop; the overlay closes its render hosts on the empty list and rebuilds them when it returns.</summary>
    public void SetRenderingPaused(bool paused)
    {
        lock (_lock)
        {
            if (_renderingPaused == paused) return;
            _renderingPaused = paused;
        }
        _notifyOverlay?.Invoke();
    }

    private bool _renderingPaused;

    public StreamAssignmentsResponse GetAssignments()
    {
        var response = new StreamAssignmentsResponse();
        lock (_lock)
        {
            if (_renderingPaused) return response;

            foreach (var ds in _bySerial.Values)
            {
                if (ds.Session.Closed || ShowsSecondaryMonitor(ds)) continue;
                if (IsLockDark(ds.Session.PanelDeviceId)) continue;
                var p = ds.Info.Profile;
                response.Assignments.Add(new StreamAssignmentDto
                {
                    SessionId = ds.Session.SessionId,
                    PanelDeviceId = ds.Session.PanelDeviceId,
                    CssWidth = p.CssWidth,
                    CssHeight = p.CssHeight,
                    Dpr = p.Dpr * p.RenderScale,
                    Fps = p.Fps,
                    BitrateKbps = p.BitrateKbps,
                    Codec = p.Codec == StreamCodec.RawBgra ? "rawBgra" : "h264",
                });
            }
        }
        return response;
    }

    /// <summary>
    /// Binds an ingest connection to its session. A second connection for a
    /// live session supersedes the first (overlay hard-kill leaves a half-open
    /// socket the new connection must displace). Returns null for unknown or
    /// closed sessions; the overlay treats 404 as "close host and re-reconcile".
    /// </summary>
    public StreamSession? TryBindIngest(string sessionId, HttpContext ctx)
    {
        HttpContext? superseded = null;
        StreamSession? session = null;
        lock (_lock)
        {
            if (_bySessionId.TryGetValue(sessionId, out var ds) && !ds.Session.Closed)
            {
                superseded = ds.IngestContext;
                ds.IngestContext = ctx;
                ds.Session.ResetForNewIngest();
                session = ds.Session;
            }
        }
        if (superseded is not null)
        {
            try { superseded.Abort(); } catch { }
        }
        if (session is not null)
            ServiceLog.Info($"[streamed-panel] ingest bound session={sessionId}{(superseded is not null ? " (superseded previous)" : "")}");
        return session;
    }

    public void OnIngestClosed(string sessionId, HttpContext ctx)
    {
        lock (_lock)
        {
            if (!_bySessionId.TryGetValue(sessionId, out var ds)) return;
            if (!ReferenceEquals(ds.IngestContext, ctx)) return;
            ds.IngestContext = null;
            ds.Session.SetIngestBound(false);
        }
        ServiceLog.Info($"[streamed-panel] ingest closed session={sessionId}");
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        CloseAll("service stopping");
        return base.StopAsync(cancellationToken);
    }

    private bool StartSession(IStreamedPanelDiscovery discovery, StreamedPanelDeviceInfo info, long now)
    {
        string panelDeviceId;
        try
        {
            panelDeviceId = ResolvePanelDeviceId(info);
        }
        catch (Exception ex)
        {
            ServiceLog.Error($"[streamed-panel] record allocation failed serial={info.Serial}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        info = WithRenderScale(info, panelDeviceId);

        var session = new StreamSession(NewSessionId(), info, panelDeviceId);
        var ds = new DeviceSession
        {
            Session = session,
            Writer = new PacedStreamWriter(session, (faulted, ex) => HandleTransportFault(info.Serial, faulted, ex)),
            Discovery = discovery,
            Info = info,
            LastPresentAtMs = now,
        };
        lock (_lock)
        {
            _bySerial[info.Serial] = ds;
            _bySessionId[session.SessionId] = ds;
        }
        // The full effective profile, so a stale persisted override (an old
        // fps/bitrate hand-tune in streamed-panels.json) is visible at a
        // glance when a device misbehaves on supposedly-fixed defaults.
        ServiceLog.Info($"[streamed-panel] session started serial={info.Serial} session={session.SessionId} "
            + $"panel={panelDeviceId} profile={info.Profile.Kind} {info.Profile.CssWidth}x{info.Profile.CssHeight}@{info.Profile.Fps} {info.Profile.BitrateKbps}kbps render={info.Profile.RenderScale}");
        // A record's `streamed` flag follows the session, so clients re-read the panel list.
        _notifyPanelChanged?.Invoke(panelDeviceId);

        TryReopenTransport(ds);
        return true;
    }

    /// <summary>
    /// Keeps a withheld panel listed when its discovery asks, whether the device took the glass
    /// from a live session or before one ever opened (a restart or replug in that mode).
    /// </summary>
    private void TrackHeld(IStreamedPanelDiscovery discovery, bool withheld)
    {
        string? held;
        lock (_lock) _heldByDevice.TryGetValue(discovery, out held);
        string? now = null;
        if (withheld && discovery.ListedWhileWithheld)
        {
            now = held;
            if (now is null && discovery.WithheldSerial is { } serial
                && _store.Load().TryGetValue(serial, out var rec)
                && !string.IsNullOrEmpty(rec.PanelDeviceId)
                && _registry.Get(rec.PanelDeviceId) is not null)
            {
                now = rec.PanelDeviceId;
            }
        }
        if (now == held)
        {
            return;
        }
        lock (_lock)
        {
            if (now is null) _heldByDevice.Remove(discovery);
            else _heldByDevice[discovery] = now;
        }
        _notifyPanelChanged?.Invoke(now ?? held!);
    }

    // The per-serial store keeps the panel record identity stable across
    // restarts/re-attaches so layout and theme survive; a record the user
    // deleted via the API is re-allocated fresh.
    private string ResolvePanelDeviceId(StreamedPanelDeviceInfo info)
    {
        var records = _store.Load();
        if (info.AdoptsFamilyRecords)
        {
            AdoptFamilyRecords(records, info);
        }
        if (records.TryGetValue(info.Serial, out var rec)
            && !string.IsNullOrEmpty(rec.PanelDeviceId)
            && _registry.Get(rec.PanelDeviceId) is not null
            && !(info.Profile.MultiInstance && HeldByAnotherSession(rec.PanelDeviceId, info.Serial)))
        {
            // Re-stamp: the driver can report a different surface than it did when the
            // record was minted (a Thermalright splits square from wide by model), and a
            // reused record would otherwise keep the old one for the life of the install.
            // A strip with no stored layout (a record older than the strip seed) gets it now.
            var reused = _registry.Patch(rec.PanelDeviceId, new PanelDevicePatch
            {
                Capabilities = info.Profile.BuildCapabilities(),
            });
            if (reused is { Layout: null } && PanelLayoutDefaults.StripSeedFor(reused) is { } seed)
            {
                _registry.Patch(rec.PanelDeviceId, new PanelDevicePatch { Layout = seed });
            }
            if (info.Profile.MultiInstance && !rec.MultiInstance)
            {
                rec.MultiInstance = true;
                _store.Save(records);
            }
            return rec.PanelDeviceId;
        }

        // A multi-screen device's screens never share a record, nor lend one to another panel.
        var multiInstanceIds = records.Values.Where(r => r.MultiInstance).Select(r => r.PanelDeviceId).ToHashSet(StringComparer.Ordinal);
        var record = _registry.Allocate(
            info.Profile.DisplayName,
            info.Profile.BuildCapabilities(),
            info.Profile.MultiInstance
                ? _ => false
                : candidate => !multiInstanceIds.Contains(candidate.Id)
                    && string.Equals(PanelDeviceRegistry.ReuseFamily(candidate.Capabilities), info.Profile.Family, StringComparison.Ordinal));
        if (IsStrip(info.Profile) && _registry.Get(record.Id)?.Layout is null)
        {
            _registry.Patch(record.Id, new PanelDevicePatch { Layout = PanelLayoutDefaults.ForStrip(info.Profile.Surface) });
        }
        rec ??= new StreamedPanelRecord();
        rec.PanelDeviceId = record.Id;
        rec.MultiInstance = info.Profile.MultiInstance;
        records[info.Serial] = rec;
        _store.Save(records);
        return record.Id;
    }

    /// <summary>
    /// Gives the device's family one record under the fixed key: the most recently seen
    /// customized one, else the most recently seen, whatever serial or key it was stored under.
    /// Records that are not customized are removed through the same cleanup a delete does; a
    /// customized record is never removed, so two of them both stay (the other under its own key).
    /// Idempotent, so it also tidies a mix left by an earlier build.
    /// </summary>
    private void AdoptFamilyRecords(Dictionary<string, StreamedPanelRecord> records, StreamedPanelDeviceInfo info)
    {
        var family = info.Profile.Family;
        var members = records
            .Select(pair => (Serial: pair.Key, Record: pair.Value, Panel: string.IsNullOrEmpty(pair.Value.PanelDeviceId) ? null : _registry.Get(pair.Value.PanelDeviceId)))
            .Where(m => m.Panel is not null && string.Equals(m.Panel.Capabilities?.Family, family, StringComparison.Ordinal))
            .OrderByDescending(m => m.Panel!.LastSeenAt)
            .ToList();
        if (members.Count == 0 || (members.Count == 1 && members[0].Serial == info.Serial))
        {
            return;
        }
        var customized = members.Where(m => IsCustomized(m.Panel!.Id)).Select(m => m.Panel!.Id).ToHashSet(StringComparer.Ordinal);
        var keep = members.FirstOrDefault(m => customized.Contains(m.Panel!.Id));
        if (keep.Panel is null)
        {
            keep = members[0];
        }
        var changed = false;
        foreach (var m in members)
        {
            if (m.Record.PanelDeviceId == keep.Record.PanelDeviceId)
            {
                changed |= m.Serial != info.Serial && records.Remove(m.Serial);
            }
            else if (!customized.Contains(m.Panel!.Id))
            {
                records.Remove(m.Serial);
                _removePanel(m.Panel.Id);
                changed = true;
            }
            else if (m.Serial == info.Serial)
            {
                // The fixed key goes to the survivor; this customized record keeps a key of its own.
                records.Remove(m.Serial);
                records[$"{info.Serial}~{m.Panel.Id}"] = m.Record;
                changed = true;
            }
        }
        if (!records.TryGetValue(info.Serial, out var current) || current.PanelDeviceId != keep.Record.PanelDeviceId)
        {
            records[info.Serial] = keep.Record;
            changed = true;
        }
        if (changed)
        {
            _store.Save(records);
        }
    }

    /// <summary>Unknown is treated as customized: a check that fails must never lead to a delete, nor escape the session start.</summary>
    private bool IsCustomized(string panelId)
    {
        try
        {
            return _isCustomized(panelId);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[streamed-panel] customized check failed for '{panelId}': {ex.GetType().Name}: {ex.Message}");
            return true;
        }
    }

    /// <summary>
    /// A multi-instance screen whose stored record another live session renders gets a fresh
    /// one: a build that shared one record across a device's screens left every serial on it.
    /// </summary>
    private bool HeldByAnotherSession(string panelDeviceId, string serial)
    {
        lock (_lock)
        {
            return _bySerial.Values.Any(ds =>
                ds.Session.PanelDeviceId == panelDeviceId && !string.Equals(ds.Info.Serial, serial, StringComparison.Ordinal));
        }
    }

    private void TryReopenTransport(DeviceSession ds)
    {
        IStreamedPanelTransport? old;
        lock (_lock)
        {
            old = ds.Transport;
            ds.Transport = null;
        }
        if (old is not null)
        {
            try { old.Dispose(); } catch { }
        }
        try
        {
            var transport = ds.Discovery.CreateTransport(ds.Info);
            // Mount orientation is applied to the bytes, not the render, so the editor and
            // the preview stay upright. Pushed-frame cooler LCDs only.
            var panelId = ds.Session.PanelDeviceId;
            if (transport is IOrientablePanelTransport orientable)
            {
                orientable.BindOrientation(() =>
                {
                    var rec = _registry.Get(panelId);
                    return (rec?.Flip180 ?? false, rec?.Mirror ?? false);
                });
            }
            // The backlight is a device command, so the transport writes it rather than
            // filtering frames with it. Null is passed through: a record that carries no
            // setting leaves the panel on whatever it powered up with.
            if (transport is IBrightnessPanelTransport dimmable)
            {
                dimmable.BindBrightness(() => EffectiveBrightness(panelId));
            }
            if (transport is ISecondaryMonitorTransport monitorable)
            {
                monitorable.BindSecondaryMonitor(() => _registry.Get(panelId)?.SecondaryMonitor == true);
                monitorable.SecondaryMonitorStateChanged += () => _notifyPanelChanged?.Invoke(panelId);
            }
            transport.Open();
            transport.StartPlayer();
            var accepted = false;
            lock (_lock)
            {
                // Open() is slow; a session closed meanwhile must not get a
                // resurrected transport.
                if (_bySessionId.ContainsKey(ds.Session.SessionId))
                {
                    ds.Transport = transport;
                    accepted = true;
                }
            }
            if (!accepted)
            {
                try { transport.Dispose(); } catch { }
                return;
            }
            ds.Session.SetTransportUp(true);
            ds.Writer.SetTransport(transport);
            // No frames flow to a panel held dark, so nothing else would carry the dark value to a
            // glass that attached or reconnected mid-lock.
            if (transport is IBrightnessPanelTransport lockDimmable)
            {
                var sleeping = IsLockSleeping(panelId);
                if (sleeping)
                {
                    // Attached mid-lock: dark at once, no ramp.
                    RemoveFade(panelId);
                    var epoch = _lockState?.Epoch ?? 0;
                    var level = CaptureRestoreLevel(panelId);
                    lockDimmable.ApplyBrightness();
                    WriteMarkerForLock(panelId, level, epoch);
                }
                else if (_registry.Get(panelId)?.LockRestoreBrightness is not null)
                {
                    // Left over from a lock that ended while this panel was away, or from a restart.
                    // While a fade up is still running the getter answers with its current step, which
                    // is not the level to keep: the fade clears the marker itself when it lands.
                    if (lockDimmable.ApplyBrightness() && ActiveFade(panelId) is null)
                    {
                        SetMarker(panelId, null);
                    }
                }
            }
            ServiceLog.Info($"[streamed-panel] transport open serial={ds.Info.Serial}");
        }
        catch (Exception ex)
        {
            ds.Session.SetTransportUp(false);
            ServiceLog.Error($"[streamed-panel] transport open failed serial={ds.Info.Serial}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // The faulted instance travels with the callback: a fault landing after a
    // reopen already swapped in a fresh transport must dispose only its own.
    private void HandleTransportFault(string serial, IStreamedPanelTransport faulted, Exception ex)
    {
        ServiceLog.Error($"[streamed-panel] transport fault serial={serial}: {ex.GetType().Name}: {ex.Message}");
        lock (_lock)
        {
            // The flag flips inside the lock so a stale fault can never
            // overwrite a concurrent reopen's transport-up.
            if (_bySerial.TryGetValue(serial, out var ds) && ReferenceEquals(ds.Transport, faulted))
            {
                ds.Transport = null;
                ds.Session.SetTransportUp(false);
            }
        }
        try { faulted.Dispose(); } catch { }
    }

    private void CloseSession(DeviceSession ds, string reason, bool notifyClients = true)
    {
        IStreamedPanelTransport? transport;
        lock (_lock)
        {
            _bySerial.Remove(ds.Info.Serial);
            _bySessionId.Remove(ds.Session.SessionId);
            transport = ds.Transport;
            ds.Transport = null;
        }
        ds.Session.Close();
        ds.Writer.Dispose();
        if (transport is not null)
        {
            try { transport.Dispose(); } catch { }
        }
        var ingest = ds.IngestContext;
        ds.IngestContext = null;
        if (ingest is not null)
        {
            try { ingest.Abort(); } catch { }
        }
        ServiceLog.Info($"[streamed-panel] session closed serial={ds.Info.Serial} session={ds.Session.SessionId} ({reason})");
        if (notifyClients)
        {
            _notifyPanelChanged?.Invoke(ds.Session.PanelDeviceId);
        }
    }

    private void CloseAll(string reason)
    {
        List<DeviceSession> all;
        lock (_lock) all = _bySerial.Values.ToList();
        foreach (var ds in all) CloseSession(ds, reason);
    }

    internal static bool IsStrip(StreamedPanelProfile p) => PanelLayoutDefaults.IsStrip(p.Surface, p.CssWidth, p.CssHeight);

    // Toggling the record's HighResolution changes the profile, so the session re-mints at the new size.
    private StreamedPanelDeviceInfo WithRenderScale(StreamedPanelDeviceInfo info, string panelDeviceId)
    {
        var profile = info.Profile;
        if (!profile.SupportsRenderScale)
        {
            return info;
        }
        var scale = _registry.Get(panelDeviceId)?.HighResolution == true ? 1.0 : profile.PerformanceRenderScale;
        return scale.Equals(profile.RenderScale)
            ? info
            : new StreamedPanelDeviceInfo { Serial = info.Serial, Profile = profile with { RenderScale = scale }, AdoptsFamilyRecords = info.AdoptsFamilyRecords };
    }

    internal static bool ProfilesEqual(StreamedPanelProfile a, StreamedPanelProfile b)
        => string.Equals(a.Kind, b.Kind, StringComparison.Ordinal)
           && string.Equals(a.Surface, b.Surface, StringComparison.Ordinal)
           && a.CssWidth == b.CssWidth
           && a.CssHeight == b.CssHeight
           && a.Dpr.Equals(b.Dpr)
           && a.RenderScale.Equals(b.RenderScale)
           && a.Fps == b.Fps
           && a.BitrateKbps == b.BitrateKbps
           && a.WriteBatchFrames == b.WriteBatchFrames;

    private static string NewSessionId()
    {
        var bytes = RandomNumberGenerator.GetBytes(9);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
