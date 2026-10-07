using System;
using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.LianLi;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Exposes every attached Lian Li Uni Hub's fans on the lighting page. A hub's
/// physical channels compose into a configurable device set (per-port vs mirror,
/// rings combined vs split) via <see cref="LianLiZoneSupport"/>; each device is
/// a 2-segment partitionable structure the user can re-zone freely. Frames are
/// per-resolved-zone, like the keeb, so custom partitions render; the writer
/// scatters them back to the inner/outer channels each port drives.
/// </summary>
public sealed class LianLiLightingDeviceProvider :
    ILightingDeviceProvider, ILightingFrameContributor, IDeviceStructureSource, IComposableHubSource, IOpenRgbDeviceOwner
{
    private readonly LianLiHubSet _hubs;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private string _lastSignature = "";

    private readonly Dictionary<string, DeviceFrame> _frameCache = new();

    public LianLiLightingDeviceProvider(LianLiHubSet hubs, IConfigStore store, Np50IdentifyTracker identify)
    {
        _hubs = hubs;
        _store = store;
        _identify = identify;
    }

    public bool IsConnected => _hubs.AnyConnected;

    /// <summary>
    /// The Lian Li Uni Hub exposes both USB HID (used here) and USB CDC (used by
    /// OpenRGB). Both interfaces reach the same physical LEDs, so when OpenRGB
    /// enumerates the hub we must prevent the bridge from seeding engine frames for
    /// it - otherwise OpenRGB's push overwrites our first-party black/brightness frames.
    /// </summary>
    public bool OwnsOpenRgbDevice(RgbDevice device) =>
        IsConnected && device.Name.Contains("Lian Li Uni Hub", StringComparison.OrdinalIgnoreCase);

    public event Action? DevicesChanged;

    /// <summary>
    /// Called by the connection worker each poll tick and by the fan-count /
    /// composition routes. Fires <see cref="DevicesChanged"/> only when the
    /// device topology changes (connect/disconnect, fan count, or composition),
    /// so the bridge rebuilds frame mappings without churning on every RPM tick.
    /// </summary>
    public void OnHubStateUpdated()
    {
        var sig = BuildSignature();
        if (sig == _lastSignature) return;
        _lastSignature = sig;
        try { DevicesChanged?.Invoke(); } catch { /* swallow subscriber failures */ }
    }

    private string BuildSignature()
    {
        var settings = _store.Load();
        var sb = new System.Text.StringBuilder();
        foreach (var hub in _hubs.Hubs)
        {
            sb.Append(hub.DeviceId);
            if (!hub.IsConnected)
            {
                sb.Append(":off;");
                continue;
            }
            var comp = LianLiZoneSupport.ReadComposition(settings, hub.DeviceId);
            sb.Append(":on|p=").Append(hub.Profile.ProductId);
            sb.Append("|a=").Append(PlaysArgbInput(hub, settings) ? '1' : '0');
            sb.Append("|m=").Append(comp.Mirror ? '1' : '0');
            sb.Append("|c=").Append(comp.CombineRings ? '1' : '0');
            var fans = LianLiHubSet.FansOf(settings.Devices, hub.DeviceId);
            for (var p = 0; p < LianLiProtocol.PortCount; p++)
            {
                sb.Append('|').Append(LianLiZoneSupport.ClampFans(fans.GetFans(p)));
            }
            sb.Append(';');
        }
        return sb.ToString();
    }

    private static bool PlaysArgbInput(LianLiHub hub, NexusSettings settings) =>
        hub.Profile.PlaysArgbInput(LianLiHubSet.LightingOf(settings.Devices, hub.DeviceId).ArgbSync);

    public GetLightingDevicesResponse GetAll()
    {
        var resp = new GetLightingDevicesResponse { IsInit = true };
        var settings = _store.Load();
        var slot = 0;
        foreach (var hub in _hubs.Hubs)
        {
            // Under ARGB sync the fans play the source header's card, so the ports have none of their own.
            if (!hub.IsConnected || PlaysArgbInput(hub, settings)) continue;
            var cards = BuildCards(hub.DeviceId, hub.Profile, settings, slot);
            slot += cards.Count;
            resp.Devices.AddRange(cards);
        }
        return resp;
    }

    /// <summary>Pure card emission for the current composition + partition; static so tests cover it without a live hub.</summary>
    internal static List<LightingDevice> BuildCards(string hubId, in LianLiFanProfile profile, NexusSettings settings, int firstSlot = 0)
    {
        var comp = LianLiZoneSupport.ReadComposition(settings, hubId);
        var composed = LianLiZoneSupport.Compose(hubId, profile, comp, LianLiHubSet.FansOf(settings.Devices, hubId));
        var cards = new List<LightingDevice>();
        var slot = firstSlot;
        foreach (var device in composed)
        {
            var zones = ZoneResolution.Resolve(device.Structure, settings);
            foreach (var zone in zones)
            {
                cards.Add(BuildCard(hubId, device.Structure, zone, slot++, settings));
            }
        }
        return cards;
    }

    private static LightingDevice BuildCard(
        string hubId, DeviceStructure structure, ResolvedZone zone, int slot, NexusSettings settings)
    {
        var disabled = settings.Devices.DisabledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var layouts = settings.Lighting.DeviceLayouts;

        var isOn = true;
        for (var i = 0; i < disabled.Count; i++)
        {
            if (disabled[i] == zone.Id) { isOn = false; break; }
        }
        prefs.TryGetValue(zone.Id, out var pref);
        var (defX, defY, defW, defH) = DefaultLayout(slot);
        layouts.TryGetValue(zone.Id, out var layout);
        return new LightingDevice
        {
            Id = zone.Id,
            DeviceKey = zone.DeviceKey,
            Name = zone.Name,
            Type = "ledstrip",
            IconType = "fan",
            LedsOn = isOn,
            Brightness = pref?.Brightness ?? 100,
            Hue = pref?.Hue ?? 0f,
            Saturation = pref?.Saturation ?? 1f,
            LedCount = zone.LedCount,
            EnabledLedCount = ZoneResolution.CountEnabled(structure, zone, zone.Id, zone.LedCount, zoneHint: 0, settings),
            CanvasX = layout?.X ?? defX,
            CanvasY = layout?.Y ?? defY,
            CanvasW = layout?.W ?? defW,
            CanvasH = layout?.H ?? defH,
            CanvasRotation = ((((layout?.Rotation ?? 0) % 360) + 360) % 360),
            ParentDeviceId = hubId,
            ZoneIndex = zone.Ordinal,
            ZoneType = "linear",
            ZoneResizable = false,
            DeviceId = structure.DeviceId,
            ZoneCustomizable = true,
        };
    }

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
            foreach (var x in current)
            {
                if (x != id) next.Add(x);
            }
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

    // Ring LED counts are fan-derived, not separately editable.
    public void SetZoneLedCount(string id, int count) { }

    public void Identify(string id, int durationMs) => _identify.Schedule(id, durationMs);

    // ── IComposableHubSource ──

    public HubCompositionInfo? DescribeComposition(string deviceId)
    {
        var hub = _hubs.Owner(deviceId);
        if (hub is null || !hub.IsConnected) return null;
        var hubId = hub.DeviceId;

        var settings = _store.Load();
        var comp = LianLiZoneSupport.ReadComposition(settings, hubId);
        var lianLi = LianLiHubSet.FansOf(settings.Devices, hubId);
        var active = new bool[LianLiProtocol.PortCount];
        for (var p = 0; p < LianLiProtocol.PortCount; p++)
        {
            active[p] = LianLiZoneSupport.ClampFans(lianLi.GetFans(p)) > 0;
        }
        return new HubCompositionInfo
        {
            HubId = hubId,
            HubKind = "lianli",
            PortCount = LianLiProtocol.PortCount,
            HasRingsAxis = hub.Profile.ChannelsPerPort == 2,
            HasPortToggle = false,
            HasMirror = false,
            Mirror = false,
            CombineRings = comp.CombineRings,
            ActivePorts = active,
        };
    }

    // ── IDeviceStructureSource ──

    public IReadOnlyList<DeviceStructure> GetStructures()
    {
        var settings = _store.Load();
        var structures = new List<DeviceStructure>();
        foreach (var device in ComposeAll(settings))
        {
            structures.Add(device.Structure);
        }
        return structures;
    }

    // Every attached hub's devices, hub by hub; a hub under ARGB sync has none.
    private List<ComposedDevice> ComposeAll(NexusSettings settings)
    {
        var all = new List<ComposedDevice>();
        foreach (var hub in _hubs.Hubs)
        {
            if (!hub.IsConnected || PlaysArgbInput(hub, settings)) continue;
            var comp = LianLiZoneSupport.ReadComposition(settings, hub.DeviceId);
            all.AddRange(LianLiZoneSupport.Compose(hub.DeviceId, hub.Profile, comp, LianLiHubSet.FansOf(settings.Devices, hub.DeviceId)));
        }
        return all;
    }

    // ── ILightingFrameContributor ──

    public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex)
    {
        var settings = _store.Load();
        var layouts = settings.Lighting.DeviceLayouts;
        var composed = ComposeAll(settings);

        var frames = new List<DeviceFrame>();
        var idx = startingIndex;
        var slot = 0;
        foreach (var device in composed)
        {
            foreach (var zone in ZoneResolution.Resolve(device.Structure, settings))
            {
                frames.Add(BuildOrReuseFrame(zone.Id, zone.FrameLedCount, slot++, layouts, ref idx));
            }
        }

        if (_frameCache.Count > frames.Count)
        {
            var live = new HashSet<string>(frames.Count);
            foreach (var f in frames)
            {
                live.Add(f.Id);
            }
            var stale = new List<string>();
            foreach (var k in _frameCache.Keys)
            {
                if (!live.Contains(k)) stale.Add(k);
            }
            foreach (var k in stale)
            {
                _frameCache.Remove(k);
            }
        }
        return frames;
    }

    private DeviceFrame BuildOrReuseFrame(
        string id, int firmwareLedCount, int zoneIndex,
        IReadOnlyDictionary<string, DeviceLayout> layouts,
        ref int idx)
    {
        var (defX, defY, defW, defH) = DefaultLayout(zoneIndex);
        layouts.TryGetValue(id, out var layout);
        var rot = ((((layout?.Rotation ?? 0) % 360) + 360) % 360);
        var thisIdx = idx++;

        if (_frameCache.TryGetValue(id, out var existing)
            && existing.Index == thisIdx
            && existing.LedCount == firmwareLedCount)
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
            ledCount: firmwareLedCount,
            x: layout?.X ?? defX,
            y: layout?.Y ?? defY,
            w: layout?.W ?? defW,
            h: layout?.H ?? defH,
            rotation: rot);
        _frameCache[id] = frame;
        return frame;
    }

    /// <summary>4-col, 2-row grid, same shape as the MiniHub/NP50 defaults,
    /// so extra zones wrap onto existing cards instead of drifting off
    /// canvas.</summary>
    internal static (float x, float y, float w, float h) DefaultLayout(int slot)
    {
        const float Y = 370f;
        const float W = 120f;
        const float H = 105f;
        const float Gap = 140f;
        const float BaseX = 40f;
        const int Cols = 4;
        const int Rows = 2; // 370 + 105 + 105 = 580 ≤ canvas bottom
        const float RowGap = 105f;
        var s = ((slot % (Cols * Rows)) + Cols * Rows) % (Cols * Rows);
        var col = s % Cols;
        var row = s / Cols;
        return (BaseX + col * Gap, Y + row * RowGap, W, H);
    }
}
