using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Devices;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Models.Devices;
using Nexus.Service.Models.SmartLights;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Lighting.Smart;

/// <summary>
/// Brand-neutral provider for every network ("smart") light. Implements the
/// engine-facing <see cref="ILightingDeviceProvider"/> (control) and
/// <see cref="ILightingFrameContributor"/> (canvas frames) once for all brands,
/// dispatching to the owning <see cref="ILightDriver"/> by id prefix. Control +
/// effect streaming both flow through <see cref="NetworkSendThrottle"/> so a
/// device is never sent faster than its rate ceiling and a slow bridge never
/// stalls the engine. Mirrors the CNVS/NP50 provider pattern; the difference is
/// the transport is the LAN, not a serial/HID hub.
/// </summary>
public sealed class SmartLightProvider : ILightingDeviceProvider, ILightingFrameContributor, IDeviceStructureSource
{
    private const string IconType = "bulb";
    // Reachability probe cadence while a lighting view is open. Govee is UDP, so
    // its frames never error on a dead device; an active devStatus probe at this
    // interval is the only signal that drops/restores the card.
    private const int ReachabilityPollSeconds = 5;

    private readonly Dictionary<string, ILightDriver> _drivers;
    private readonly IConfigStore _store;
    private readonly NetworkSendThrottle _throttle;

    // id -> reachability. Owned by the active probe (ProbeReachabilityAsync);
    // the send path must NOT write it - a fire-and-forget UDP send (Govee)
    // never errors on a dead device, so a "successful" send is not liveness.
    private readonly ConcurrentDictionary<string, bool> _online = new();
    // Serializes the SetOnline read-modify; the probe loop and the
    // /smart-lights/all route both write _online.
    private readonly object _onlineLock = new();
    // id -> live snapshot, refreshed each BuildFrames so the 33 Hz writer's
    // SubmitFrame lookups don't re-parse settings per device per tick.
    private readonly ConcurrentDictionary<string, SmartLight> _cache = new();
    // id -> frame plan captured in BuildFrames so SubmitEffectFrame knows
    // whether the device takes per-zone colors without re-asking the driver.
    private readonly ConcurrentDictionary<string, LightFramePlan> _plans = new();
    // id -> reused DeviceFrame so the per-LED buffer survives RgbBridge rebuilds.
    private readonly Dictionary<string, DeviceFrame> _frames = new();
    // Smart lights whose realtime mode drops a manual color without a continuous
    // stream (plan.StaticNeedsStreaming, e.g. Govee razer/DreamView). The writer
    // re-pushes these while no effect runs. Membership is set in PushStaticFrom.
    private readonly ConcurrentDictionary<string, byte> _streamedStatic = new();

    // Reachability poll lifecycle, started/stopped by lighting-topic subscribers.
    private readonly object _pollLock = new();
    private CancellationTokenSource? _pollCts;

    public event Action? DevicesChanged;
    // Raised only when a device's visible online/offline state flips, so the
    // lighting list can refetch and drop/restore the card. Distinct from
    // DevicesChanged (which forces a full topology rebuild).
    public event Action? OnlineChanged;

    public SmartLightProvider(IEnumerable<ILightDriver> drivers, IConfigStore store, NetworkSendThrottle throttle)
    {
        _drivers = new Dictionary<string, ILightDriver>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in drivers) _drivers[d.Brand] = d;
        _store = store;
        _throttle = throttle;
    }

    public bool IsConnected => _store.Load().SmartLights.Devices.Count > 0;

    /// <summary>True when the id belongs to a paired smart light (brand prefix
    /// matches a registered driver and the device is configured).</summary>
    public bool Owns(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        var brand = BrandOf(id);
        return brand is not null && _drivers.ContainsKey(brand);
    }

    private static string? BrandOf(string id)
    {
        var c = id.IndexOf(':');
        return c > 0 ? id.Substring(0, c) : null;
    }

    private ILightDriver? DriverForId(string id)
    {
        var brand = BrandOf(id);
        return brand is not null && _drivers.TryGetValue(brand, out var d) ? d : null;
    }

    // A brand absent from the map (or false) is OFF: not scanned, not probed, and
    // its lights stay off the lighting canvas.
    private static bool BrandOn(IReadOnlyDictionary<string, bool> brandEnabled, string brand)
        => brandEnabled.TryGetValue(brand, out var v) && v;

    // ── ILightingDeviceProvider ──────────────────────────────────────────────

    public GetLightingDevicesResponse GetAll()
    {
        var resp = new GetLightingDevicesResponse();
        var settings = _store.Load();
        var devices = settings.SmartLights.Devices;
        if (devices.Count == 0) return resp;
        resp.IsInit = true;

        var disabled = settings.Devices.DisabledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var layouts = settings.Lighting.DeviceLayouts;
        var brandEnabled = settings.SmartLights.BrandEnabled;

        for (var i = 0; i < devices.Count; i++)
        {
            var cfg = devices[i];
            if (!cfg.Enabled || !BrandOn(brandEnabled, cfg.Brand)) continue;
            // Hide a known-offline light's card; absent/true keeps it shown
            // (optimistic). Reachability is owned by the probe poll
            // (ProbeReachabilityAsync); reconnect is detected there, not from the
            // send path (a UDP send can't tell a live device from a dead one).
            if (_online.TryGetValue(cfg.Id, out var online) && !online) continue;

            var isOn = !disabled.Contains(cfg.Id);
            var brightness = 100;
            var hue = 0f;
            var saturation = 1f;
            if (prefs.TryGetValue(cfg.Id, out var pref))
            { brightness = pref.Brightness; hue = pref.Hue; saturation = pref.Saturation; }

            var (defX, defY, defW, defH) = DefaultLayout(i);
            layouts.TryGetValue(cfg.Id, out var layout);

            resp.Devices.Add(new LightingDevice
            {
                Id = cfg.Id,
                Name = cfg.Name,
                Type = "ledstrip",
                IconType = IconType,
                LedsOn = isOn,
                Brightness = brightness,
                Hue = hue,
                Saturation = saturation,
                LedCount = LedCountFor(cfg),
                CanvasX = layout?.X ?? defX,
                CanvasY = layout?.Y ?? defY,
                CanvasW = layout?.W ?? defW,
                CanvasH = layout?.H ?? defH,
                CanvasRotation = ((((layout?.Rotation ?? 0) % 360) + 360) % 360),
                ZoneType = "linear",
                ZoneResizable = false,
            });
        }
        return resp;
    }

    public void SetDisabled(IReadOnlyList<string> ids) => _store.Update(s =>
    {
        // ids = the complete set of MY devices that should be disabled. Replace
        // only my entries in the shared list; leave other providers' ids alone.
        var set = new HashSet<string>(s.Devices.DisabledLightingDevices, StringComparer.Ordinal);
        foreach (var cfg in s.SmartLights.Devices) set.Remove(cfg.Id);
        foreach (var id in ids) if (Owns(id)) set.Add(id);
        s.Devices.DisabledLightingDevices = new List<string>(set);
    });

    public void SetPower(string id, bool on)
    {
        _store.Update(s =>
        {
            var current = s.Devices.DisabledLightingDevices;
            if (on)
            {
                if (!current.Contains(id)) return;
                var next = new List<string>(current.Count);
                foreach (var x in current) if (x != id) next.Add(x);
                s.Devices.DisabledLightingDevices = next;
            }
            else
            {
                if (current.Contains(id)) return;
                var next = new List<string>(current.Count + 1) ;
                next.AddRange(current); next.Add(id);
                s.Devices.DisabledLightingDevices = next;
            }
        });
        PushStatic(id);
    }

    public void SetBrightness(string id, int brightness)
    {
        _store.Update(s =>
        {
            if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
            { pref = new LightingDevicePreference(); s.Devices.LightingDevicePrefs[id] = pref; }
            pref.Brightness = Math.Clamp(brightness, 0, 100);
        });
        PushStatic(id);
    }

    public void SetHue(string id, float hue)
    {
        _store.Update(s =>
        {
            if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
            { pref = new LightingDevicePreference(); s.Devices.LightingDevicePrefs[id] = pref; }
            pref.Hue = hue;
        });
        PushStatic(id);
    }

    public void SetSaturation(string id, float saturation)
    {
        _store.Update(s =>
        {
            if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
            { pref = new LightingDevicePreference(); s.Devices.LightingDevicePrefs[id] = pref; }
            pref.Saturation = saturation;
        });
        PushStatic(id);
    }

    public void SetZoneLedCount(string id, int count) { _ = id; _ = count; }

    public void Identify(string id, int durationMs)
    {
        var dev = Resolve(id);
        var driver = DriverForId(id);
        if (dev is null || driver is null) return;
        _ = driver.IdentifyAsync(dev, CancellationToken.None);
    }

    // ── ILightingFrameContributor ────────────────────────────────────────────

    public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex)
    {
        var settings = _store.Load();
        var devices = settings.SmartLights.Devices;
        var layouts = settings.Lighting.DeviceLayouts;
        var brandEnabled = settings.SmartLights.BrandEnabled;

        // Refresh the per-tick lookup cache from the persisted config.
        _cache.Clear();

        if (devices.Count == 0) { _frames.Clear(); return Array.Empty<DeviceFrame>(); }

        var result = new List<DeviceFrame>(devices.Count);
        var live = new HashSet<string>(StringComparer.Ordinal);
        var idx = startingIndex;
        for (var i = 0; i < devices.Count; i++)
        {
            var cfg = devices[i];
            if (!cfg.Enabled || !BrandOn(brandEnabled, cfg.Brand)) continue;
            _cache[cfg.Id] = ToSmartLight(cfg);
            live.Add(cfg.Id);

            var driver = DriverForId(cfg.Id);
            var plan = driver?.PlanFrames(_cache[cfg.Id]) ?? new LightFramePlan(16, AverageToSingle: true);
            _plans[cfg.Id] = plan;
            var ledCount = plan.LedCount;

            var (defX, defY, defW, defH) = DefaultLayout(i);
            layouts.TryGetValue(cfg.Id, out var layout);
            var rot = ((((layout?.Rotation ?? 0) % 360) + 360) % 360);

            if (!_frames.TryGetValue(cfg.Id, out var frame)
                || frame.Index != idx || frame.LedCount != ledCount)
            {
                frame = new DeviceFrame(idx, cfg.Id, ledCount,
                    layout?.X ?? defX, layout?.Y ?? defY, layout?.W ?? defW, layout?.H ?? defH, rot);
                ApplySampleMap(frame, plan);
                _frames[cfg.Id] = frame;
            }
            else
            {
                frame.X = layout?.X ?? defX; frame.Y = layout?.Y ?? defY;
                frame.W = layout?.W ?? defW; frame.H = layout?.H ?? defH; frame.Rotation = rot;
                // Re-paired devices can change their zone geometry without
                // changing the count - re-apply the sample map both ways
                // (fresh UVs, or back to the grid when UVs disappeared).
                ApplySampleMap(frame, plan);
            }
            result.Add(frame);
            idx++;
        }

        // Drop frames for unpaired devices.
        if (_frames.Count != live.Count)
        {
            var stale = new List<string>();
            foreach (var k in _frames.Keys) if (!live.Contains(k)) stale.Add(k);
            foreach (var k in stale) _frames.Remove(k);
        }
        return result;
    }

    /// <summary>Map engine sample points for the device: real per-zone canvas
    /// positions when the driver provides them (Nanoleaf panel layout),
    /// otherwise an even grid across the rect (region averaging).</summary>
    private static void ApplySampleMap(DeviceFrame frame, LightFramePlan plan)
    {
        if (plan.LedU is { } u && plan.LedV is { } v
            && u.Length == frame.LedCount && v.Length == frame.LedCount)
        {
            frame.LedU = u;
            frame.LedV = v;
            return;
        }
        BuildSampleGrid(frame);
    }

    /// <summary>Spread N sample points across the device's canvas rect so a
    /// single-color lamp averages a region (good screen-mirror behavior).</summary>
    private static void BuildSampleGrid(DeviceFrame frame)
    {
        var n = frame.LedCount;
        if (n <= 1) return;
        var side = (int)Math.Ceiling(Math.Sqrt(n));
        var u = new float[n];
        var v = new float[n];
        for (var i = 0; i < n; i++)
        {
            var col = i % side;
            var row = i / side;
            u[i] = side > 1 ? (col + 0.5f) / side : 0.5f;
            v[i] = side > 1 ? (row + 0.5f) / side : 0.5f;
        }
        frame.LedU = u;
        frame.LedV = v;
    }

    // ── IDeviceStructureSource ────────────────────────────────────────────────

    /// <summary>Expose zone-addressable strips (Govee razer segments) as a
    /// single linear structure so the LED-map editor can position each segment
    /// and the resolver folds the user's overrides into the engine sample
    /// points. Single-color lamps have nothing to arrange and are omitted.</summary>
    public IReadOnlyList<DeviceStructure> GetStructures()
    {
        var devices = _store.Load().SmartLights.Devices;
        if (devices.Count == 0) return Array.Empty<DeviceStructure>();
        List<DeviceStructure>? structures = null;
        foreach (var cfg in devices)
        {
            if (!cfg.Enabled) continue;
            var plan = _plans.TryGetValue(cfg.Id, out var cached) ? cached : DriverForId(cfg.Id)?.PlanFrames(ToSmartLight(cfg));
            if (plan is not { AverageToSingle: false }) continue;
            var n = Math.Max(1, plan.LedCount);
            if (n <= 1) continue;

            // The driver's stock sample sweep seeds the editor's default
            // positions; null falls back to the resolver's linear default,
            // which equals the Govee razer sweep (left→right at mid-height).
            float[]? du = null, dv = null;
            if (plan.LedU is { } pu && plan.LedV is { } pv && pu.Length == n && pv.Length == n)
            { du = pu; dv = pv; }

            var structure = new DeviceStructure { DeviceId = cfg.Id, Name = cfg.Name };
            structure.Segments.Add(new StructureSegment
            {
                Index = 0,
                Name = cfg.Name,
                LedCount = n,
                FrameLedCount = n,
                Resizable = false,
                ZoneType = "linear",
                DefaultU = du,
                DefaultV = dv,
            });
            // Zone id MUST equal the card id (cfg.Id): the editor opens with the
            // card id as its zone, and RgbBridge matches the contributed frame
            // to this zone by id to apply the override context.
            structure.DefaultZones.Add(new DefaultZoneDef
            {
                Id = cfg.Id,
                Name = cfg.Name,
                RawName = cfg.Name,
                LegacyZoneIndex = -1,
                Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = n } },
            });
            (structures ??= new()).Add(structure);
        }
        return (IReadOnlyList<DeviceStructure>?)structures ?? Array.Empty<DeviceStructure>();
    }

    // ── Streaming + static control (called by the writer + control methods) ───

    /// <summary>Submit one engine effect frame for a device: averaged to a
    /// single color, plus the per-zone RGB triplets when the device's plan
    /// requests them. Called by the frame writer each tick.</summary>
    public void SubmitEffectFrame(string id, ReadOnlySpan<byte> ledBytes, int ledCount, float brightness01)
    {
        var (r, g, b) = AverageRgb(ledBytes, ledCount);
        byte[]? zones = null;
        if (_plans.TryGetValue(id, out var plan) && !plan.AverageToSingle)
        {
            var len = Math.Min(ledCount * 3, ledBytes.Length);
            if (len >= 3)
            {
                zones = new byte[len];
                ledBytes.Slice(0, len).CopyTo(zones);
            }
        }
        AccumulateOrSubmit(id, new LightFrame(On: true, r, g, b, brightness01, zones));
    }

    internal static (byte r, byte g, byte b) AverageRgb(ReadOnlySpan<byte> leds, int ledCount)
    {
        if (ledCount <= 0 || leds.Length < 3) return (0, 0, 0);
        long sr = 0, sg = 0, sb = 0;
        var n = Math.Min(ledCount, leds.Length / 3);
        for (var i = 0; i < n; i++)
        {
            var off = i * 3;
            sr += leds[off]; sg += leds[off + 1]; sb += leds[off + 2];
        }
        if (n == 0) return (0, 0, 0);
        return ((byte)(sr / n), (byte)(sg / n), (byte)(sb / n));
    }

    /// <summary>Submit the latest desired frame for a device (effect streaming).
    /// No-op if the device or its driver isn't resolvable.</summary>
    public void SubmitFrame(string id, LightFrame frame)
    {
        if (!_cache.TryGetValue(id, out var dev)) return;
        var driver = DriverForId(id);
        if (driver is null) return;
        var minInterval = driver.MinIntervalMs(dev);
        // Reachability is NOT inferred from sends: a Govee UDP send always
        // "succeeds" to a dead device, and a transient HTTP failure shouldn't
        // strand a light. The probe poll owns _online.
        _throttle.Submit(id, frame, minInterval,
            (f, c) => driver.SendAsync(dev, f, c),
            hostKey: driver.RateLimitKey(dev), hostIntervalMs: minInterval);
    }

    /// <summary>Route an effect frame: session-streaming drivers (Hue
    /// Entertainment) accumulate into a per-controller batch; others go through
    /// the per-light throttle/REST path. Brightness is pre-applied for the
    /// session path (its packet carries raw RGB); off = black.</summary>
    public void AccumulateOrSubmit(string id, LightFrame frame)
    {
        var driver = DriverForId(id);
        if (driver is ISessionStreamer ss && _cache.TryGetValue(id, out var dev))
        {
            byte r = 0, g = 0, b = 0;
            if (frame.On)
            {
                var k = Math.Clamp(frame.Brightness01, 0f, 1f);
                r = (byte)(frame.R * k); g = (byte)(frame.G * k); b = (byte)(frame.B * k);
            }
            ss.Accumulate(dev, r, g, b);
        }
        else
        {
            SubmitFrame(id, frame);
        }
    }

    /// <summary>Flush this tick's batched frames to any active session streamers.</summary>
    public void FlushStreaming()
    {
        foreach (var d in _drivers.Values) if (d is ISessionStreamer ss) ss.Flush();
    }

    /// <summary>End all streaming sessions (effect stopped).</summary>
    public void StopStreamingSessions()
    {
        foreach (var d in _drivers.Values) if (d is ISessionStreamer ss) ss.StopAll();
    }

    /// <summary>Push each device's static (manual) color - used when an effect
    /// stops so lamps return to their configured color rather than freezing on
    /// the last effect frame.</summary>
    public void RestoreStatic()
    {
        var settings = _store.Load();
        foreach (var cfg in settings.SmartLights.Devices)
        {
            if (!cfg.Enabled) continue;
            PushStaticFrom(cfg.Id, settings);
        }
    }

    /// <summary>Push one device's static (manual) color. Used when a light is
    /// re-enabled from uncontrolled: while no effect runs, nothing else re-pushes
    /// its configured color on its own, so this closes the gap explicitly.
    /// No-op for a device this provider doesn't own or that isn't enabled.</summary>
    public void RestoreStatic(string id)
    {
        if (!Owns(id)) return;
        var settings = _store.Load();
        foreach (var cfg in settings.SmartLights.Devices)
        {
            if (cfg.Id != id) continue;
            if (cfg.Enabled)
            {
                PushStaticFrom(id, settings);
            }
            return;
        }
    }

    /// <summary>Re-push the static color for lights whose realtime mode lapses
    /// without a continuous stream (plan.StaticNeedsStreaming, e.g. Govee
    /// razer/DreamView reverts ~60s without frames). Called by the frame writer on
    /// a keep-alive cadence while no effect runs; no-op when none are controlled.</summary>
    public void MaintainStreamedStatic()
    {
        if (_streamedStatic.IsEmpty) return;
        var s = _store.Load();
        foreach (var id in _streamedStatic.Keys)
            PushStaticFrom(id, s);
    }

    private void PushStatic(string id) => PushStaticFrom(id, _store.Load());

    private void PushStaticFrom(string id, NexusSettings s)
    {
        // Uncontrolled: submit nothing at all, not even On=false - the light is
        // meant to keep whatever state its own app/scene left it in.
        if (s.Devices.UncontrolledLightingDevices.Contains(id))
        {
            _streamedStatic.TryRemove(id, out _);
            return;
        }

        // Control can run before BuildFrames populated the cache (e.g. right
        // after pairing) - resolve a snapshot so SubmitFrame has a device.
        if (!_cache.TryGetValue(id, out var dev))
        {
            dev = Resolve(id);
            if (dev is null) return;
            _cache[id] = dev;
        }
        var on = !s.Devices.DisabledLightingDevices.Contains(id);
        float hue = 0f, sat = 1f; int bri = 100;
        if (s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
        { hue = pref.Hue; sat = pref.Saturation; bri = pref.Brightness; }
        var global = Math.Clamp(s.Lighting.GlobalBrightness, 0f, 1f);
        var (r, g, b) = ColorMath.HsvToRgb(hue, sat, 1f);
        var b01 = Math.Min(Math.Clamp(bri, 0, 100) / 100f, global);

        // A device whose realtime mode lapses without a stream
        // (plan.StaticNeedsStreaming, e.g. Govee) can't hold a single-color
        // command while a built-in scene runs - only a per-segment frame takes
        // over. Send a solid zoned frame and mark it for the writer's keep-alive.
        // Devices that hold a manual color (bulbs, Nanoleaf) get the single path.
        byte[]? zones = null;
        // Prefer the cached plan (BuildFrames) - PlanFrames allocates a sample
        // map, and this runs every writer tick for a streamed-static device.
        var plan = _plans.TryGetValue(id, out var cachedPlan) ? cachedPlan : DriverForId(id)?.PlanFrames(dev);
        if (on && plan is { StaticNeedsStreaming: true, LedCount: > 1 })
        {
            var n = plan.LedCount;
            zones = new byte[n * 3];
            for (var i = 0; i < n; i++) { zones[i * 3] = r; zones[i * 3 + 1] = g; zones[i * 3 + 2] = b; }
            _streamedStatic[id] = 0;
        }
        else
        {
            _streamedStatic.TryRemove(id, out _);
        }
        SubmitFrame(id, new LightFrame(On: on, r, g, b, b01, zones));
    }

    // ── Routes surface (discover / pair / list / remove) ─────────────────────

    public async Task<GetSmartLightsResponse> GetSmartLightDtosAsync(CancellationToken ct)
    {
        var resp = new GetSmartLightsResponse();
        var smart = _store.Load().SmartLights;
        resp.BrandEnabled = new Dictionary<string, bool>(smart.BrandEnabled);
        var devices = smart.Devices;
        if (devices.Count == 0) return resp;

        var hostOnline = await ProbeReachabilityAsync(devices, ct).ConfigureAwait(false);
        foreach (var cfg in devices)
        {
            resp.Devices.Add(new SmartLightDto
            {
                Id = cfg.Id,
                Brand = cfg.Brand,
                Name = cfg.Name,
                Host = cfg.Host,
                Online = hostOnline.TryGetValue((cfg.Brand, cfg.Host), out var on) && on,
                Enabled = cfg.Enabled,
                LedCount = LedCountFor(cfg),
            });
        }
        return resp;
    }

    // Probe reachability ONCE per (brand, host) - a transient streaming 429
    // doesn't strand a whole bridge's lights - then sync _online and broadcast
    // once on any transition. Brand-neutral: each driver's PingAsync is the
    // active liveness check its transport allows (Govee UDP devStatus, Hue/
    // Nanoleaf HTTP). Hue lights share their bridge's host, so the probe is
    // bridge-level for Hue and per-device for Govee.
    private async Task<Dictionary<(string brand, string host), bool>> ProbeReachabilityAsync(
        IReadOnlyList<SmartLightConfig> devices, CancellationToken ct)
    {
        var brandEnabled = _store.Load().SmartLights.BrandEnabled;
        var hostOnline = new Dictionary<(string brand, string host), bool>();
        foreach (var cfg in devices)
        {
            if (!BrandOn(brandEnabled, cfg.Brand)) continue; // off brands are never probed
            var key = (cfg.Brand, cfg.Host);
            if (hostOnline.ContainsKey(key)) continue;
            var driver = DriverForId(cfg.Id);
            var online = false;
            if (driver is not null)
            {
                try { online = await driver.PingAsync(ToSmartLight(cfg), ct).ConfigureAwait(false); }
                catch { online = false; }
            }
            hostOnline[key] = online;
        }
        var changed = false;
        foreach (var cfg in devices)
        {
            if (!BrandOn(brandEnabled, cfg.Brand)) continue;
            changed |= SetOnline(cfg.Id, hostOnline.TryGetValue((cfg.Brand, cfg.Host), out var on) && on);
        }
        if (changed) RaiseOnlineChanged();
        return hostOnline;
    }

    /// <summary>Begin probing reachability on an interval; idempotent. Wired to
    /// the lighting topic's first subscriber, so the poll runs only while a
    /// lighting view is open and stops when the last one leaves.</summary>
    public void StartReachabilityPolling()
    {
        lock (_pollLock)
        {
            if (_pollCts is not null) return;
            _pollCts = new CancellationTokenSource();
            _ = ReachabilityLoopAsync(_pollCts.Token);
        }
    }

    public void StopReachabilityPolling()
    {
        // An in-flight probe may finish after this returns; its writes go
        // through SetOnline, so a brief overlap with a restarted loop is safe.
        lock (_pollLock)
        {
            _pollCts?.Cancel();
            _pollCts?.Dispose();
            _pollCts = null;
        }
    }

    private async Task ReachabilityLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(ReachabilityPollSeconds));
            // Probe immediately, then on each tick, so opening a lighting view
            // reflects current reachability without waiting a full interval.
            do
            {
                try
                {
                    var devices = _store.Load().SmartLights.Devices;
                    if (devices.Count > 0)
                        await ProbeReachabilityAsync(devices, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch { /* transient probe failure; keep polling */ }
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { /* stopped */ }
    }

    public async Task<DiscoverSmartLightsResponse> DiscoverAsync(string brand, CancellationToken ct)
    {
        var resp = new DiscoverSmartLightsResponse();
        if (!_drivers.TryGetValue(brand, out var driver))
        { resp.Error = "unknown-brand"; return resp; }

        IReadOnlyList<DiscoveredLight> found;
        try { found = await driver.DiscoverAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { resp.Error = ex.Message; return resp; }

        var pairedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cfg in _store.Load().SmartLights.Devices)
            if (!string.IsNullOrEmpty(cfg.StableKey)) pairedKeys.Add(cfg.StableKey);

        foreach (var d in found)
        {
            resp.Devices.Add(new DiscoveredSmartLightDto
            {
                Brand = d.Brand,
                Host = d.Host,
                Name = d.Name,
                StableKey = d.StableKey,
                AlreadyPaired = pairedKeys.Contains(d.StableKey),
            });
        }
        resp.Ok = true;
        return resp;
    }

    public async Task<PairSmartLightResponse> PairAsync(PairSmartLightBody body, CancellationToken ct)
    {
        if (!_drivers.TryGetValue(body.Brand, out var driver))
            return new PairSmartLightResponse { Ok = false, Error = "unknown-brand", Message = "Unknown brand." };

        var target = new DiscoveredLight(body.Brand, body.Host,
            string.IsNullOrEmpty(body.Name) ? body.Brand : body.Name, body.StableKey ?? "");

        PairResult result;
        try { result = await driver.PairAsync(target, ct).ConfigureAwait(false); }
        catch (Exception ex) { return new PairSmartLightResponse { Ok = false, Error = "exception", Message = ex.Message }; }

        if (!result.Ok)
        {
            var msg = result.Error == "link-button"
                ? "Press the button on the bridge, then try again."
                : result.Error;
            return new PairSmartLightResponse { Ok = false, Error = result.Error, Message = msg };
        }

        var added = 0;
        _store.Update(s =>
        {
            // Rebuild the list (replace reference, never structurally mutate the
            // live list a reader on the writer thread may be enumerating). Re-pair
            // replaces existing entries in place of order; new lights append.
            var repl = new Dictionary<string, SmartLightConfig>(StringComparer.Ordinal);
            foreach (var dev in result.Devices) repl[dev.Id] = dev;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var next = new List<SmartLightConfig>(s.SmartLights.Devices.Count + result.Devices.Count);
            foreach (var c in s.SmartLights.Devices)
            {
                if (repl.TryGetValue(c.Id, out var updated)) { next.Add(updated); seen.Add(c.Id); }
                else { next.Add(c); }
            }
            foreach (var dev in result.Devices)
                if (seen.Add(dev.Id)) { next.Add(dev); added++; }
            s.SmartLights.Devices = next;
        });

        // Drop throttle loops for these ids so the next send rebuilds with the
        // refreshed host/token/clientkey (a re-pair can change them).
        foreach (var dev in result.Devices) _throttle.Remove(dev.Id);

        ServiceLog.Info($"[smart-lights] paired {body.Brand}: {result.Devices.Count} light(s), {added} new");
        FireChanged();
        return new PairSmartLightResponse { Ok = true, Added = added, Message = $"Added {result.Devices.Count} light(s)." };
    }

    public void Remove(string id)
    {
        _store.Update(s =>
        {
            // Replace the list reference (not RemoveAt) so a reader enumerating
            // the old reference on the writer thread isn't structurally mutated.
            var next = new List<SmartLightConfig>(s.SmartLights.Devices.Count);
            foreach (var c in s.SmartLights.Devices) if (c.Id != id) next.Add(c);
            s.SmartLights.Devices = next;
        });
        _throttle.Remove(id);
        _online.TryRemove(id, out _);
        _cache.TryRemove(id, out _);
        _plans.TryRemove(id, out _);
        _streamedStatic.TryRemove(id, out _);
        FireChanged();
    }

    /// <summary>Enable/disable a paired light WITHOUT unpairing it. Disabled
    /// lights stay listed on the Smart Lights page but drop off the lighting
    /// canvas/effects (GetAll + BuildFrames skip <c>!Enabled</c>).</summary>
    public void SetEnabled(string id, bool enabled)
    {
        _store.Update(s =>
        {
            foreach (var cfg in s.SmartLights.Devices)
                if (cfg.Id == id) { cfg.Enabled = enabled; break; }
        });
        if (!enabled) { _throttle.Remove(id); _streamedStatic.TryRemove(id, out _); } // stop streaming a disabled light
        FireChanged();
    }

    /// <summary>Turn a whole brand on or off. OFF brands are not scanned, not
    /// probed, and their lights leave the lighting canvas (GetAll/BuildFrames and
    /// the probe gate on BrandOn). Paired lights and their LED mappings are kept.</summary>
    public void SetBrandEnabled(string brand, bool enabled)
    {
        _store.Update(s => s.SmartLights.BrandEnabled[brand] = enabled);
        if (!enabled)
        {
            foreach (var cfg in _store.Load().SmartLights.Devices)
                if (cfg.Brand == brand) { _throttle.Remove(cfg.Id); _streamedStatic.TryRemove(cfg.Id, out _); }
        }
        FireChanged();
    }

    /// <summary>Scan a brand and reconcile its paired list: prune that brand's
    /// lights that are unreachable now (the way to drop a light you removed), then
    /// run discovery to surface new candidates for the pair UI. Pruning is by an
    /// active reachability probe, NOT discovery membership: discovery is unreliable
    /// for some brands (Govee multicast over WiFi is why Govee is paired by IP), so
    /// a present-but-undiscovered light must not be dropped. Probed once per host
    /// (Hue lights share their bridge). Pruned lights keep their DeviceLedOverrides,
    /// so re-adding restores the mapping. Returns discovery candidates.</summary>
    public async Task<DiscoverSmartLightsResponse> ScanBrandAsync(string brand, CancellationToken ct)
    {
        var brandLights = new List<SmartLightConfig>();
        foreach (var c in _store.Load().SmartLights.Devices)
            if (c.Brand == brand) brandLights.Add(c);

        var hostReachable = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var cfg in brandLights)
        {
            if (hostReachable.ContainsKey(cfg.Host)) continue;
            var driver = DriverForId(cfg.Id);
            var reachable = false;
            if (driver is not null)
            {
                try { reachable = await driver.PingAsync(ToSmartLight(cfg), ct).ConfigureAwait(false); }
                catch { reachable = false; }
            }
            hostReachable[cfg.Host] = reachable;
        }

        var pruned = new List<string>();
        _store.Update(s =>
        {
            var next = new List<SmartLightConfig>(s.SmartLights.Devices.Count);
            foreach (var c in s.SmartLights.Devices)
            {
                // Prune only a probed-and-unreachable light of this brand; if we
                // couldn't probe its host, keep it.
                if (c.Brand == brand && hostReachable.TryGetValue(c.Host, out var ok) && !ok)
                { pruned.Add(c.Id); continue; }
                next.Add(c);
            }
            s.SmartLights.Devices = next;
        });
        foreach (var id in pruned)
        {
            _throttle.Remove(id);
            _online.TryRemove(id, out _);
            _cache.TryRemove(id, out _);
            _plans.TryRemove(id, out _);
            _streamedStatic.TryRemove(id, out _);
        }
        if (pruned.Count > 0)
        {
            ServiceLog.Info($"[smart-lights] scan {brand}: pruned {pruned.Count} unreachable light(s)");
            FireChanged();
        }
        return await DiscoverAsync(brand, ct).ConfigureAwait(false);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private SmartLight? Resolve(string id)
    {
        foreach (var cfg in _store.Load().SmartLights.Devices)
            if (cfg.Id == id) return ToSmartLight(cfg);
        return null;
    }

    private SmartLight ToSmartLight(SmartLightConfig cfg) => new()
    {
        Id = cfg.Id,
        Brand = cfg.Brand,
        Name = cfg.Name,
        Host = cfg.Host,
        StableKey = cfg.StableKey,
        Token = cfg.Token,
        Extra = cfg.Extra,
        Enabled = cfg.Enabled,
        Online = !_online.TryGetValue(cfg.Id, out var on) || on,
    };

    // Per-zone count the UI shows: zone-addressable strips (Govee razer segments)
    // report their segment count; single-color lamps report 1.
    private int LedCountFor(SmartLightConfig cfg)
    {
        var plan = _plans.TryGetValue(cfg.Id, out var cachedPlan) ? cachedPlan : DriverForId(cfg.Id)?.PlanFrames(ToSmartLight(cfg));
        return plan is { AverageToSingle: false } ? Math.Max(1, plan.LedCount) : 1;
    }

    private void FireChanged()
    {
        try { DevicesChanged?.Invoke(); } catch { /* swallow subscriber failures */ }
    }

    private void RaiseOnlineChanged()
    {
        try { OnlineChanged?.Invoke(); } catch { /* swallow subscriber failures */ }
    }

    // Records reachability and returns true when it flips the device's visible
    // state. The optimistic baseline (absent or true = online) means a device
    // shows until the probe first reports it unreachable. Serialized so the poll
    // loop and the /smart-lights/all route can't interleave the read-modify and
    // drop or duplicate the transition broadcast.
    private bool SetOnline(string id, bool online)
    {
        lock (_onlineLock)
        {
            var wasOnline = !_online.TryGetValue(id, out var prev) || prev;
            _online[id] = online;
            return wasOnline != online;
        }
    }

    private static (float x, float y, float w, float h) DefaultLayout(int index)
        => (40f + (index % 6) * 130f, 760f + (index / 6) * 80f, 120f, 60f);
}
