using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
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
    private readonly IReadOnlyList<IStreamedPanelDiscovery> _discoveries;
    private readonly StreamedPanelStore _store;
    private readonly PanelDeviceRegistry _registry;
    private readonly DeviceControlGate _gate;
    private readonly Action? _notifyOverlay;
    private readonly Func<long> _nowMs;

    public StreamedPanelCoordinator(
        IEnumerable<IStreamedPanelDiscovery> discoveries,
        StreamedPanelStore store,
        PanelDeviceRegistry registry,
        DeviceControlGate gate,
        Action? notifyOverlay = null,
        Func<long>? nowMs = null)
    {
        _discoveries = discoveries.ToList();
        _store = store;
        _registry = registry;
        _gate = gate;
        _notifyOverlay = notifyOverlay;
        _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
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

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }

        CloseAll("service stopping");
    }

    internal void TickOnce()
    {
        var changed = false;
        foreach (var discovery in _discoveries)
        {
            var enabled = _gate.IsEnabled(discovery.HandlerId);
            IReadOnlyList<StreamedPanelDeviceInfo> devices;
            if (!enabled)
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

            foreach (var info in devices)
            {
                DeviceSession? existing;
                lock (_lock) _bySerial.TryGetValue(info.Serial, out existing);
                if (existing is not null && !ProfilesEqual(existing.Info.Profile, info.Profile))
                {
                    // Config changes re-mint the session (fresh sessionId) so
                    // the overlay's reconcile is a pure spawn/close diff.
                    CloseSession(existing, "profile changed");
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
                else if (StartSession(discovery, info, now))
                {
                    changed = true;
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
    /// Panel record ids currently owned by a live stream session. GET /panel/devices
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
        }
        return ids;
    }

    /// <summary>Applies a persisted panel backlight change without waiting for a frame.</summary>
    public void ApplyBrightness(string panelDeviceId)
    {
        lock (_lock)
        {
            foreach (var ds in _bySerial.Values)
            {
                if (ds.Session.Closed || !string.Equals(ds.Session.PanelDeviceId, panelDeviceId, StringComparison.Ordinal))
                    continue;
                (ds.Transport as IBrightnessPanelTransport)?.ApplyBrightness();
                return;
            }
        }
    }

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
                if (ds.Session.Closed) continue;
                var p = ds.Info.Profile;
                response.Assignments.Add(new StreamAssignmentDto
                {
                    SessionId = ds.Session.SessionId,
                    PanelDeviceId = ds.Session.PanelDeviceId,
                    CssWidth = p.CssWidth,
                    CssHeight = p.CssHeight,
                    Dpr = p.Dpr,
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
            + $"panel={panelDeviceId} profile={info.Profile.Kind} {info.Profile.CssWidth}x{info.Profile.CssHeight}@{info.Profile.Fps} {info.Profile.BitrateKbps}kbps");

        TryReopenTransport(ds);
        return true;
    }

    // The per-serial store keeps the panel record identity stable across
    // restarts/re-attaches so layout and theme survive; a record the user
    // deleted via the API is re-allocated fresh.
    private string ResolvePanelDeviceId(StreamedPanelDeviceInfo info)
    {
        var records = _store.Load();
        if (records.TryGetValue(info.Serial, out var rec)
            && !string.IsNullOrEmpty(rec.PanelDeviceId)
            && _registry.Get(rec.PanelDeviceId) is not null)
        {
            // Re-stamp: the driver can report a different surface than it did when the
            // record was minted (a Thermalright splits square from wide by model), and a
            // reused record would otherwise keep the old one for the life of the install.
            _registry.Patch(rec.PanelDeviceId, new PanelDevicePatch
            {
                Capabilities = info.Profile.BuildCapabilities(),
            });
            return rec.PanelDeviceId;
        }

        var record = _registry.Allocate(info.Profile.DisplayName, info.Profile.BuildCapabilities());
        rec ??= new StreamedPanelRecord();
        rec.PanelDeviceId = record.Id;
        records[info.Serial] = rec;
        _store.Save(records);
        return record.Id;
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
                dimmable.BindBrightness(() => _registry.Get(panelId)?.LcdBrightness);
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

    private void CloseSession(DeviceSession ds, string reason)
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
    }

    private void CloseAll(string reason)
    {
        List<DeviceSession> all;
        lock (_lock) all = _bySerial.Values.ToList();
        foreach (var ds in all) CloseSession(ds, reason);
    }

    internal static bool ProfilesEqual(StreamedPanelProfile a, StreamedPanelProfile b)
        => string.Equals(a.Kind, b.Kind, StringComparison.Ordinal)
           && string.Equals(a.Surface, b.Surface, StringComparison.Ordinal)
           && a.CssWidth == b.CssWidth
           && a.CssHeight == b.CssHeight
           && a.Dpr.Equals(b.Dpr)
           && a.Fps == b.Fps
           && a.BitrateKbps == b.BitrateKbps
           && a.WriteBatchFrames == b.WriteBatchFrames;

    private static string NewSessionId()
    {
        var bytes = RandomNumberGenerator.GetBytes(9);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
