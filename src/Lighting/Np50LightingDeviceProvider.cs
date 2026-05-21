using System;
using System.Collections.Generic;
using Qos.Service.Devices;
using Qos.Service.Lighting.Engine;
using Qos.Service.Models.Devices;
using Qos.Service.Peripherals.Hyte.Np50;
using Qos.Service.Persistence;

namespace Qos.Service.Lighting;

/// <summary>
/// Exposes the NP50 hub and its attached Nexus Link modules (LS10 / LS30 /
/// FP12) as <see cref="LightingDevice"/>s for the lighting page. The hub
/// itself surfaces as a parent device; each attached module surfaces as a
/// child with <see cref="LightingDevice.ParentDeviceId"/> pointing at the
/// hub, so the existing DevicePanel grouping logic (built for motherboard
/// zones) renders them as a collapsible group "for free".
///
/// v1 is read-only — the GetAll snapshot lets the UI render the tree, but
/// the per-zone Set* operations (power, brightness, hue, saturation) are
/// no-ops. Phase-3.5 will wire them into <see cref="Np50Hub.WriteLighting"/>
/// once the per-zone color picker shape settles.
/// </summary>
public sealed class Np50LightingDeviceProvider : ILightingDeviceProvider, ILightingFrameContributor
{
    /// <summary>Number of LEDs on the NP50 hub logo strip (the firmware-controlled prefix on streaming channel 1).</summary>
    public const int LogoLedCount = 6;

    /// <summary>
    /// Cached fan-list signature used to debounce <see cref="DevicesChanged"/>:
    /// we only fire when the actual lit-device topology changes (a strip is
    /// plugged/unplugged), not on every fan-temp/RPM tick.
    /// </summary>
    private string _lastSignature = "";

    private readonly Np50Hub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;

    public Np50LightingDeviceProvider(Np50Hub hub, IConfigStore store, Np50IdentifyTracker identify)
    {
        _hub = hub;
        _store = store;
        _identify = identify;
    }

    public bool IsConnected => _hub.IsConnected;

    public event Action? DevicesChanged;

    /// <summary>
    /// Called by the heartbeat worker (which polls the hub) so the bridge
    /// can rebuild frame mappings when the lit-device topology changes.
    /// Cheap signature compare keeps non-topology ticks silent.
    /// </summary>
    public void OnHubStateUpdated()
    {
        var sig = BuildSignature();
        if (sig == _lastSignature) return;
        _lastSignature = sig;
        try { DevicesChanged?.Invoke(); } catch { /* subscriber failures shouldn't bubble */ }
    }

    private string BuildSignature()
    {
        if (!_hub.IsConnected) return "disconnected";
        var sb = new System.Text.StringBuilder(_hub.DeviceId);
        sb.Append('|');
        foreach (var port in _hub.State.Ports)
        {
            sb.Append("p").Append(port.Index).Append(':');
            foreach (var dev in port.Devices)
            {
                sb.Append(dev.Model).Append(dev.LedCount).Append(',');
            }
            sb.Append(';');
        }
        return sb.ToString();
    }

    public GetLightingDevicesResponse GetAll()
    {
        var resp = new GetLightingDevicesResponse { IsInit = true };
        if (!_hub.IsConnected) return resp;

        var hubId = _hub.DeviceId;
        var settings = _store.Load();
        var disabled = settings.Devices.DisabledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var layouts = settings.Lighting.DeviceLayouts;
        var zoneLedCounts = settings.Devices.ZoneLedCounts;

        // Each NP50-driven zone is exposed as a top-level drivable card that
        // groups under a "HYTE NP50" header via the same parentDeviceId
        // mechanism the motherboard ARGB-strip split uses. The hub itself
        // is never emitted (no LEDs of its own → would render as "detected
        // but not drivable" clutter); the header just needs a non-empty
        // parentDeviceId shared by all NP50 zones. Names follow the
        // "{ParentPrefix} - {ZoneName}" convention DevicePanel splits on.
        var slot = 0;

        resp.Devices.Add(BuildZone(
            id: $"{hubId}:logo",
            name: $"{Np50Hub.ProductName} - Logo Strip",
            iconType: "strip",
            firmwareLedCount: LogoLedCount,
            zoneIndex: slot++,
            parentDeviceId: hubId,
            disabled, prefs, layouts, zoneLedCounts));

        foreach (var port in _hub.State.Ports)
        {
            foreach (var dev in port.Devices)
            {
                if (dev.LedCount <= 0) continue;
                resp.Devices.Add(BuildZone(
                    id: $"{hubId}:port{port.Index}:dev{dev.Index}",
                    name: $"{Np50Hub.ProductName} - {dev.Model} (Port {port.Index} #{dev.Index})",
                    iconType: dev.Model == "FP12" ? "fan" : "strip",
                    firmwareLedCount: dev.LedCount,
                    zoneIndex: slot++,
                    parentDeviceId: hubId,
                    disabled, prefs, layouts, zoneLedCounts));
            }
        }
        return resp;
    }

    private static LightingDevice BuildZone(
        string id, string name, string iconType,
        int firmwareLedCount, int zoneIndex, string parentDeviceId,
        IReadOnlyList<string> disabled,
        IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        IReadOnlyDictionary<string, Persistence.DeviceLayout> layouts,
        IReadOnlyDictionary<string, int> zoneLedCounts)
    {
        // Honour persisted state when reporting the card so the UI's toggle,
        // brightness slider, position, and LED count all reflect what the
        // writer is actually doing.
        var isOn = true;
        for (var i = 0; i < disabled.Count; i++)
        { if (disabled[i] == id) { isOn = false; break; } }
        var brightness = 100;
        var hue = 0f;
        var saturation = 1f;
        if (prefs.TryGetValue(id, out var pref))
        {
            brightness = pref.Brightness;
            hue = pref.Hue;
            saturation = pref.Saturation;
        }
        // LED count: persisted override wins so the user can shorten a strip
        // they've only partially wired. Capped at the firmware-reported max
        // so we never claim more LEDs than the hub can actually drive.
        var effectiveLedCount = firmwareLedCount;
        if (zoneLedCounts.TryGetValue(id, out var persisted))
        {
            effectiveLedCount = Math.Clamp(persisted, 0, firmwareLedCount);
        }
        // Position: persisted layout wins. Defaults place NP50 zones in
        // their own visible band on the canvas (below OpenRGB strips at
        // y≈380, well within the engine's 600-px canvas height — the old
        // "+64 slot" math put them at y=2620, off-canvas, which read as
        // "no boundary visible / can't drag" in the panel.
        var (defX, defY, defW, defH) = DefaultNp50Layout(zoneIndex);
        layouts.TryGetValue(id, out var layout);
        return new LightingDevice
        {
            Id = id,
            Name = name,
            Type = "ledstrip",
            IconType = iconType,
            LedsOn = isOn,
            Brightness = brightness,
            Hue = hue,
            Saturation = saturation,
            LedCount = effectiveLedCount,
            CanvasX = layout?.X ?? defX,
            CanvasY = layout?.Y ?? defY,
            CanvasW = layout?.W ?? defW,
            CanvasH = layout?.H ?? defH,
            CanvasRotation = ((((layout?.Rotation ?? 0) % 360) + 360) % 360),
            ParentDeviceId = parentDeviceId,
            ZoneIndex = zoneIndex,
            ZoneType = "linear",
            ZoneResizable = true,
        };
    }

    // Setters persist to the same shared settings store OpenRGB uses, so the
    // engine→writer pipeline picks up the new state on the next frame.
    // Mirrors OpenRgbLightingDeviceProvider's atomic list-replacement
    // strategy so the 30fps frame reader never sees a torn DisabledList.

    public void SetDisabled(IReadOnlyList<string> ids) => _store.Update(s =>
    {
        s.Devices.DisabledLightingDevices = new List<string>(ids);
    });

    public void SetPower(string id, bool on) => _store.Update(s =>
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
            var next = new List<string>(current.Count + 1);
            next.AddRange(current);
            next.Add(id);
            s.Devices.DisabledLightingDevices = next;
        }
    });

    public void SetBrightness(string id, int brightness) => _store.Update(s =>
    {
        if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
        {
            pref = new LightingDevicePreference();
            s.Devices.LightingDevicePrefs[id] = pref;
        }
        pref.Brightness = Math.Clamp(brightness, 0, 100);
    });

    public void SetHue(string id, float hue) => _store.Update(s =>
    {
        if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
        {
            pref = new LightingDevicePreference();
            s.Devices.LightingDevicePrefs[id] = pref;
        }
        pref.Hue = hue;
    });

    public void SetSaturation(string id, float saturation) => _store.Update(s =>
    {
        if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
        {
            pref = new LightingDevicePreference();
            s.Devices.LightingDevicePrefs[id] = pref;
        }
        pref.Saturation = saturation;
    });

    // NP50 module LED counts are fixed by the firmware (LS10=20, LS30=62),
    // but the user can persist a SMALLER logical count if a strip is only
    // partially wired or they want to trim the canvas-sample area. Stored
    // alongside OpenRGB zone-led-count overrides so the engine sees a
    // single source of truth. Values above the firmware max are clamped on
    // read in GetAll.
    public void SetZoneLedCount(string id, int count)
    {
        if (count < 0) return;
        _store.Update(s => s.Devices.ZoneLedCounts[id] = count);
    }

    public void Identify(string id, int durationMs) => _identify.Schedule(id, durationMs);

    // ── ILightingFrameContributor ──

    // Frames built on previous BuildFrames calls, keyed by id. Reused when
    // the topology hasn't changed so the existing DeviceFrame instance (and
    // its LED buffer holding the last rendered colors) survives the 3 s
    // RgbBridge.RefreshDevicesAsync rebuild. Without this every refresh
    // hands the writer a fresh, zero-filled frame for one tick and the hub
    // sees a black-out — the OpenRGB path avoids this via
    // RgbBridge.BuildOrReuseFrame; contributors need the same protection.
    private readonly Dictionary<string, DeviceFrame> _frameCache = new();

    public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex)
    {
        if (!_hub.IsConnected) return Array.Empty<DeviceFrame>();
        var frames = new List<DeviceFrame>();
        var hubId = _hub.DeviceId;
        var idx = startingIndex;
        var settings = _store.Load();
        var layouts = settings.Lighting.DeviceLayouts;
        var zoneLedCounts = settings.Devices.ZoneLedCounts;

        // IDs + layout positions stay in lockstep with what GetAll returns —
        // the engine's canvas-sample step uses the per-frame X/Y/W/H, and
        // the lighting-page card editor writes to the same DeviceLayouts
        // store, so dragging a card moves where the engine samples colors
        // from. EffectiveLedCount honours the user's "trim a strip" override
        // bounded by the firmware-reported max.
        var slot = 0;

        frames.Add(BuildOrReuseFrame(
            id: $"{hubId}:logo",
            firmwareLedCount: LogoLedCount,
            zoneIndex: slot++,
            layouts, zoneLedCounts,
            idx: ref idx));

        foreach (var port in _hub.State.Ports)
        {
            foreach (var dev in port.Devices)
            {
                if (dev.LedCount <= 0) continue;
                frames.Add(BuildOrReuseFrame(
                    id: $"{hubId}:port{port.Index}:dev{dev.Index}",
                    firmwareLedCount: dev.LedCount,
                    zoneIndex: slot++,
                    layouts, zoneLedCounts,
                    idx: ref idx));
            }
        }

        // Prune cache entries no longer in the live set so a removed strip
        // doesn't keep its DeviceFrame pinned forever.
        if (_frameCache.Count > frames.Count)
        {
            var live = new HashSet<string>(frames.Count);
            foreach (var f in frames) live.Add(f.Id);
            var stale = new List<string>();
            foreach (var k in _frameCache.Keys) if (!live.Contains(k)) stale.Add(k);
            foreach (var k in stale) _frameCache.Remove(k);
        }
        return frames;
    }

    private DeviceFrame BuildOrReuseFrame(
        string id, int firmwareLedCount, int zoneIndex,
        IReadOnlyDictionary<string, Persistence.DeviceLayout> layouts,
        IReadOnlyDictionary<string, int> zoneLedCounts,
        ref int idx)
    {
        var effectiveLedCount = firmwareLedCount;
        if (zoneLedCounts.TryGetValue(id, out var persisted))
        {
            effectiveLedCount = Math.Clamp(persisted, 0, firmwareLedCount);
        }
        var (defX, defY, defW, defH) = DefaultNp50Layout(zoneIndex);
        layouts.TryGetValue(id, out var layout);
        var rot = ((((layout?.Rotation ?? 0) % 360) + 360) % 360);
        var thisIdx = idx++;

        // Reuse if id + Index + LedCount all match — same criteria as
        // RgbBridge.BuildOrReuseFrame. X/Y/W/H/Rotation are {get; set;} on
        // DeviceFrame so we can update layout on the existing instance
        // without forcing a fresh allocation (and the zero-LED buffer that
        // comes with it).
        if (_frameCache.TryGetValue(id, out var existing)
            && existing.Index == thisIdx
            && existing.LedCount == effectiveLedCount)
        {
            existing.X = layout?.X ?? defX;
            existing.Y = layout?.Y ?? defY;
            existing.W = layout?.W ?? defW;
            existing.H = layout?.H ?? defH;
            existing.Rotation = rot;
            return existing;
        }

        var frame = new DeviceFrame(
            index: thisIdx,
            id: id,
            ledCount: effectiveLedCount,
            x: layout?.X ?? defX,
            y: layout?.Y ?? defY,
            w: layout?.W ?? defW,
            h: layout?.H ?? defH,
            rotation: rot);
        _frameCache[id] = frame;
        return frame;
    }

    /// <summary>
    /// Visible-canvas default layout for NP50 zones. Canvas runs 0..1000 ×
    /// 0..600; OpenRGB strips live around y=380, so we tuck NP50 zones into
    /// y=490 in a horizontal row. The user can drag them anywhere afterward
    /// and the layout persists to settings.Lighting.DeviceLayouts.
    /// </summary>
    private static (float x, float y, float w, float h) DefaultNp50Layout(int slot)
    {
        const float Y = 490f;
        const float W = 200f;
        const float H = 60f;
        const float Gap = 220f;
        const float BaseX = 40f;
        const int Cols = 4;
        var col = slot % Cols;
        var row = slot / Cols;
        return (BaseX + col * Gap, Y + row * 70f, W, H);
    }
}

/// <summary>
/// Singleton tracker for identify-flash requests against NP50-driven LEDs.
/// Lets <see cref="Np50LightingDeviceProvider.Identify"/> schedule a flash
/// and <see cref="Np50LightingFrameWriter"/> read it without a direct
/// dependency between the two (which would otherwise form a cycle through
/// DI on the lighting engine path).
/// </summary>
public sealed class Np50IdentifyTracker
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (long startTicks, long expirationTicks)> _entries = new();

    public void Schedule(string id, int durationMs)
    {
        if (string.IsNullOrEmpty(id)) return;
        var now = DateTime.UtcNow.Ticks;
        var dur = Math.Max(1, durationMs);
        lock (_lock) _entries[id] = (now, now + TimeSpan.FromMilliseconds(dur).Ticks);
    }

    public bool TryGetActive(string id, long nowTicks, out long startTicks)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(id, out var entry) && nowTicks < entry.expirationTicks)
            {
                startTicks = entry.startTicks;
                return true;
            }
            if (entry.expirationTicks != 0) _entries.Remove(id);
        }
        startTicks = 0;
        return false;
    }
}
