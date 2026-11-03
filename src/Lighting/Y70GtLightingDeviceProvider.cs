using System;
using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.Hyte.Y70Display;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Surfaces the HYTE Y70 GT backlight as one standalone matrix card. The
/// panel's serial controller is owned by <see cref="Y70DisplayHub"/> (OpenRGB
/// does not drive it), so this provider hands the engine a 5x7 frame whose
/// per-LED UVs follow the wire order, and <see cref="Y70GtLightingFrameWriter"/>
/// streams it. Every other Y70 variant yields no card.
/// </summary>
public sealed class Y70GtLightingDeviceProvider :
    ILightingDeviceProvider, ILightingFrameContributor, IDeviceStructureSource
{
    public const string BacklightName = "Y70 GT Backlight";

    private readonly Y70DisplayHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private string _lastSignature = "";

    public Y70GtLightingDeviceProvider(Y70DisplayHub hub, IConfigStore store, Np50IdentifyTracker identify)
    {
        _hub = hub;
        _store = store;
        _identify = identify;
    }

    public bool IsConnected => Present;

    public event Action? DevicesChanged;

    private bool Present => _hub.HasBacklight && !string.IsNullOrEmpty(_hub.DeviceId);

    /// <summary>The hub id is shared with the Y70 device handler; only the GT variant owns a lighting card under it.</summary>
    public static bool IsY70GtId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith("y70:", StringComparison.Ordinal);

    /// <summary>Called by the Y70 heartbeat after each tick so the bridge rebuilds its frame map on (re)connect; a signature compare suppresses repeats.</summary>
    public void OnHubStateUpdated()
    {
        var sig = Present ? _hub.DeviceId : "disconnected";
        if (sig == _lastSignature) return;
        _lastSignature = sig;
        try { DevicesChanged?.Invoke(); } catch { /* swallow subscriber failures */ }
    }

    private static string DeviceKey() =>
        DeviceKeyComputer.ForFirstParty(Y70DisplayProtocol.VendorId, Y70DisplayProtocol.Y70GtProductId);

    public GetLightingDevicesResponse GetAll()
    {
        var resp = new GetLightingDevicesResponse { IsInit = true };
        if (!Present) return resp;
        var id = _hub.DeviceId;
        var settings = _store.Load();
        var isOn = !settings.Devices.DisabledLightingDevices.Contains(id);
        var brightness = 100;
        var hue = 0f;
        var saturation = 1f;
        if (settings.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
        {
            brightness = pref.Brightness; hue = pref.Hue; saturation = pref.Saturation;
        }
        var (defX, defY, defW, defH) = DefaultLayout();
        settings.Lighting.DeviceLayouts.TryGetValue(id, out var layout);
        resp.Devices.Add(new LightingDevice
        {
            Id = id, DeviceKey = DeviceKey(), Name = BacklightName, Type = "ledstrip", IconType = "case",
            LedsOn = isOn, Brightness = brightness, Hue = hue, Saturation = saturation,
            LedCount = Y70DisplayProtocol.BacklightLedCount,
            CanvasX = layout?.X ?? defX, CanvasY = layout?.Y ?? defY,
            CanvasW = layout?.W ?? defW, CanvasH = layout?.H ?? defH,
            CanvasRotation = ((((layout?.Rotation ?? 0) % 360) + 360) % 360),
            ZoneType = "matrix", ZoneResizable = false,
        });
        return resp;
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

    // The LED count is fixed by the 5x7 grid.
    public void SetZoneLedCount(string id, int count)
    {
        _ = id; _ = count;
    }

    public void Identify(string id, int durationMs) => _identify.Schedule(id, durationMs);

    // ── IDeviceStructureSource ──

    public IReadOnlyList<DeviceStructure> GetStructures()
    {
        if (!Present) return Array.Empty<DeviceStructure>();
        var id = _hub.DeviceId;
        var key = DeviceKey();
        var (u, v) = BuildLedUv();
        var structure = new DeviceStructure { DeviceId = id, Name = BacklightName, DeviceKey = key, Partitionable = false };
        structure.Segments.Add(new StructureSegment
        {
            Index = 0,
            Name = BacklightName,
            LedCount = Y70DisplayProtocol.BacklightLedCount,
            FrameLedCount = Y70DisplayProtocol.BacklightLedCount,
            Resizable = false,
            ZoneType = "matrix",
            DefaultU = u,
            DefaultV = v,
        });
        structure.DefaultZones.Add(new DefaultZoneDef
        {
            Id = id,
            Name = BacklightName,
            RawName = BacklightName,
            DeviceKey = key,
            LegacyZoneIndex = -1,
            Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = Y70DisplayProtocol.BacklightLedCount } },
        });
        return new[] { structure };
    }

    // Held across RgbBridge rebuilds so the per-LED buffer isn't re-zeroed for one tick.
    private DeviceFrame? _frame;

    public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex)
    {
        if (!Present) { _frame = null; return Array.Empty<DeviceFrame>(); }
        var id = _hub.DeviceId;
        var layouts = _store.Load().Lighting.DeviceLayouts;
        var ledCount = Y70DisplayProtocol.BacklightLedCount;
        var (defX, defY, defW, defH) = DefaultLayout();
        layouts.TryGetValue(id, out var layout);
        var rot = ((((layout?.Rotation ?? 0) % 360) + 360) % 360);

        if (_frame is not null && _frame.Index == startingIndex && _frame.Id == id && _frame.LedCount == ledCount)
        {
            _frame.X = layout?.X ?? defX;
            _frame.Y = layout?.Y ?? defY;
            _frame.W = layout?.W ?? defW;
            _frame.H = layout?.H ?? defH;
            _frame.Rotation = rot;
        }
        else
        {
            _frame = new DeviceFrame(
                index: startingIndex, id: id, ledCount: ledCount,
                x: layout?.X ?? defX, y: layout?.Y ?? defY,
                w: layout?.W ?? defW, h: layout?.H ?? defH, rotation: rot);
            var (u, v) = BuildLedUv();
            _frame.LedU = u;
            _frame.LedV = v;
        }
        return new[] { _frame };
    }

    /// <summary>Per-LED canvas position for the card's wire order, so effects land on the physical cell of the 5x7 grid rather than along a linear index.</summary>
    internal static (float[] u, float[] v) BuildLedUv()
    {
        var order = Y70DisplayProtocol.BacklightWireOrder;
        var u = new float[order.Length];
        var v = new float[order.Length];
        for (var i = 0; i < order.Length; i++)
        {
            var (col, row) = order[i];
            u[i] = (col + 0.5f) / Y70DisplayProtocol.BacklightColumns;
            v[i] = (row + 0.5f) / Y70DisplayProtocol.BacklightRows;
        }
        return (u, v);
    }

    private static (float x, float y, float w, float h) DefaultLayout() => (40f, 950f, 100f, 140f);
}
