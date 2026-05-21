using System;
using System.Collections.Generic;
using Qos.Service.Devices;
using Qos.Service.Lighting.Engine;
using Qos.Service.Models.Devices;
using Qos.Service.Peripherals.Hyte.MiniHub;
using Qos.Service.Persistence;

namespace Qos.Service.Lighting;

/// <summary>
/// Exposes the MiniHub's 2 LED-capable ports (port 3 and port 4) as
/// drivable <see cref="LightingDevice"/>s grouped under a "HYTE MiniHub"
/// header, mirroring <see cref="Np50LightingDeviceProvider"/>. Same
/// engine→writer pipeline: BuildFrames contributes the zones to the
/// engine; <see cref="MiniHubLightingFrameWriter"/> pushes the rendered
/// LED bytes to the hub each tick.
/// </summary>
public sealed class MiniHubLightingDeviceProvider : ILightingDeviceProvider, ILightingFrameContributor
{
    private readonly MiniHubHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private string _lastSignature = "";

    public MiniHubLightingDeviceProvider(MiniHubHub hub, IConfigStore store, Np50IdentifyTracker identify)
    {
        _hub = hub;
        _store = store;
        _identify = identify;
    }

    public bool IsConnected => _hub.IsConnected;

    public event Action? DevicesChanged;

    public void OnHubStateUpdated()
    {
        var sig = BuildSignature();
        if (sig == _lastSignature) return;
        _lastSignature = sig;
        try { DevicesChanged?.Invoke(); } catch { /* swallow subscriber failures */ }
    }

    private string BuildSignature()
    {
        if (!_hub.IsConnected) return "disconnected";
        return $"{_hub.DeviceId}|p3:{_hub.State.Port3.LedCount}|p4:{_hub.State.Port4.LedCount}";
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
        var counts = settings.Devices.ZoneLedCounts;
        var slot = 0;

        // All four physical ports can carry LEDs. The MiniHub firmware
        // doesn't enumerate them — the official HYTE tool keeps the per-
        // port LED counts in a user-edited config (MiniHubLayoutConfig).
        // We mirror that: emit all four LED zones unconditionally with
        // sensible defaults (matching the spec table and the typical
        // 1-fan + 3-fan layout), let the user resize via the settings
        // modal (ZoneResizable=true), and persist the user's value into
        // ZoneLedCounts which BuildZone honours below.
        AddZone($"{hubId}:port1", $"{MiniHubHub.ProductName} - Port 1 (1× RGB Fan)", _hub.State.Port1.LedCount);
        AddZone($"{hubId}:port2", $"{MiniHubHub.ProductName} - Port 2 (3× RGB Fans)", _hub.State.Port2.LedCount);
        AddZone($"{hubId}:port3", $"{MiniHubHub.ProductName} - Port 3 (LED Strip)", _hub.State.Port3.LedCount);
        AddZone($"{hubId}:port4", $"{MiniHubHub.ProductName} - Port 4 (LED Strip)", _hub.State.Port4.LedCount);
        return resp;

        void AddZone(string id, string name, int firmwareLedCount)
        {
            resp.Devices.Add(BuildZone(
                id: id, name: name, firmwareLedCount: firmwareLedCount,
                zoneIndex: slot++, parentDeviceId: hubId,
                disabled, prefs, layouts, counts));
        }
    }

    private static LightingDevice BuildZone(
        string id, string name, int firmwareLedCount, int zoneIndex, string parentDeviceId,
        IReadOnlyList<string> disabled,
        IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        IReadOnlyDictionary<string, DeviceLayout> layouts,
        IReadOnlyDictionary<string, int> counts)
    {
        var isOn = true;
        for (var i = 0; i < disabled.Count; i++) if (disabled[i] == id) { isOn = false; break; }
        var brightness = 100;
        var hue = 0f;
        var saturation = 1f;
        if (prefs.TryGetValue(id, out var pref))
        {
            brightness = pref.Brightness; hue = pref.Hue; saturation = pref.Saturation;
        }
        // LED count override (user can shorten / expand within firmware-reported max)
        var effectiveLedCount = firmwareLedCount;
        if (counts.TryGetValue(id, out var persisted))
            effectiveLedCount = Math.Max(0, persisted);
        var (defX, defY, defW, defH) = DefaultMiniHubLayout(zoneIndex);
        layouts.TryGetValue(id, out var layout);
        return new LightingDevice
        {
            Id = id, Name = name, Type = "ledstrip", IconType = "strip",
            LedsOn = isOn, Brightness = brightness, Hue = hue, Saturation = saturation,
            LedCount = effectiveLedCount,
            CanvasX = layout?.X ?? defX, CanvasY = layout?.Y ?? defY,
            CanvasW = layout?.W ?? defW, CanvasH = layout?.H ?? defH,
            CanvasRotation = ((((layout?.Rotation ?? 0) % 360) + 360) % 360),
            ParentDeviceId = parentDeviceId, ZoneIndex = zoneIndex,
            ZoneType = "linear", ZoneResizable = true,
        };
    }

    public void SetDisabled(IReadOnlyList<string> ids) => _store.Update(s =>
        s.Devices.DisabledLightingDevices = new List<string>(ids));

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
            next.AddRange(current); next.Add(id);
            s.Devices.DisabledLightingDevices = next;
        }
    });

    public void SetBrightness(string id, int brightness) => _store.Update(s =>
    {
        if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
        { pref = new LightingDevicePreference(); s.Devices.LightingDevicePrefs[id] = pref; }
        pref.Brightness = Math.Clamp(brightness, 0, 100);
    });

    public void SetHue(string id, float hue) => _store.Update(s =>
    {
        if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
        { pref = new LightingDevicePreference(); s.Devices.LightingDevicePrefs[id] = pref; }
        pref.Hue = hue;
    });

    public void SetSaturation(string id, float saturation) => _store.Update(s =>
    {
        if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
        { pref = new LightingDevicePreference(); s.Devices.LightingDevicePrefs[id] = pref; }
        pref.Saturation = saturation;
    });

    public void SetZoneLedCount(string id, int count)
    {
        if (count < 0) return;
        _store.Update(s => s.Devices.ZoneLedCounts[id] = count);
    }

    public void Identify(string id, int durationMs) => _identify.Schedule(id, durationMs);

    // Same rationale as Np50LightingDeviceProvider._frameCache: reuse the
    // DeviceFrame instance across RgbBridge's 3 s refresh so the writer
    // doesn't see a fresh zero-filled frame for one tick and blank the hub.
    private readonly Dictionary<string, DeviceFrame> _frameCache = new();

    public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex)
    {
        if (!_hub.IsConnected) return Array.Empty<DeviceFrame>();
        var frames = new List<DeviceFrame>();
        var hubId = _hub.DeviceId;
        var idx = startingIndex;
        var settings = _store.Load();
        var layouts = settings.Lighting.DeviceLayouts;
        var counts = settings.Devices.ZoneLedCounts;
        var slot = 0;

        // One DeviceFrame per channel. Channels 1+2 cover the RGB-fan rings
        // on the Nexus-Link fan ports; 3+4 are the standalone LED outputs.
        // Always emitted regardless of declared LED count so the writer
        // always pushes a blank frame to every channel — keeps the hub
        // from falling back to firmware animation on un-addressed channels.
        frames.Add(BuildOrReuseFrame($"{hubId}:port1", _hub.State.Port1.LedCount, slot++, layouts, counts, ref idx));
        frames.Add(BuildOrReuseFrame($"{hubId}:port2", _hub.State.Port2.LedCount, slot++, layouts, counts, ref idx));
        frames.Add(BuildOrReuseFrame($"{hubId}:port3", _hub.State.Port3.LedCount, slot++, layouts, counts, ref idx));
        frames.Add(BuildOrReuseFrame($"{hubId}:port4", _hub.State.Port4.LedCount, slot++, layouts, counts, ref idx));

        // Prune cache entries no longer in the live set (e.g. hub serial
        // changed). For MiniHub the live set is fixed at 4 ports so this
        // mostly only fires across reconnects to a different physical hub.
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
        IReadOnlyDictionary<string, DeviceLayout> layouts,
        IReadOnlyDictionary<string, int> counts,
        ref int idx)
    {
        var effectiveLedCount = firmwareLedCount;
        if (counts.TryGetValue(id, out var persisted))
            effectiveLedCount = Math.Max(0, persisted);
        var (defX, defY, defW, defH) = DefaultMiniHubLayout(zoneIndex);
        layouts.TryGetValue(id, out var layout);
        var rot = ((((layout?.Rotation ?? 0) % 360) + 360) % 360);
        var thisIdx = idx++;

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
            index: thisIdx, id: id, ledCount: effectiveLedCount,
            x: layout?.X ?? defX, y: layout?.Y ?? defY,
            w: layout?.W ?? defW, h: layout?.H ?? defH, rotation: rot);
        _frameCache[id] = frame;
        return frame;
    }

    /// <summary>Default canvas slots for MiniHub zones. Placed just below the NP50 row so they coexist on a typical canvas without overlap.</summary>
    private static (float x, float y, float w, float h) DefaultMiniHubLayout(int slot)
    {
        const float Y = 560f;
        const float W = 240f;
        const float H = 60f;
        const float Gap = 280f;
        const float BaseX = 40f;
        return (BaseX + slot * Gap, Y, W, H);
    }
}
