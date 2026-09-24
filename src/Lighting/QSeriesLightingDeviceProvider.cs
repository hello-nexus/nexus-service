using System;
using System.Collections.Generic;
using Nexus.Service.Devices;            // ILightingDeviceProvider
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.Hyte.QSeriesCooler;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Surfaces the HYTE Q-series cooler (Q60 / Q80) as lighting cards: a standalone
/// Panel + Logo card (mirroring <see cref="CnvsLightingDeviceProvider"/>), plus one
/// card per Nexus Link device (LS10 / LN70 / lit FT12) chained on channel 1 or 2,
/// each grouped under the Panel + Logo card via <see cref="LightingDevice.ParentDeviceId"/>.
///
/// OpenRGB doesn't drive 1st-party HYTE devices, so <see cref="QSeriesCoolerHub"/>
/// owns the cooler's serial port and this provider exposes its LEDs to the engine
/// -&gt; <see cref="QSeriesLightingFrameWriter"/> pipeline. The Panel + Logo card streams
/// to ports 3/4 via <see cref="QSeriesCoolerHub.WriteLighting"/>; the Nexus Link zones
/// stream to ports 1/2 via <see cref="QSeriesCoolerHub.WriteLinkLighting"/>.
/// </summary>
public sealed class QSeriesLightingDeviceProvider :
    ILightingDeviceProvider, ILightingFrameContributor, IDeviceStructureSource, IOpenRgbDeviceOwner
{
    private readonly QSeriesCoolerHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private string _lastSignature = "";

    public QSeriesLightingDeviceProvider(QSeriesCoolerHub hub, IConfigStore store, Np50IdentifyTracker identify)
    {
        _hub = hub;
        _store = store;
        _identify = identify;
    }

    public bool IsConnected => _hub.IsConnected;

    /// <summary>
    /// The id OpenRGB assigns to the same cooler when it enumerates the COM port we hold
    /// (RgbDevice.StableId = "openrgb-l-{location}", and location is the COM port for these
    /// serial devices, e.g. "openrgb-l-COM4"). The composite strips this inert OpenRGB zombie
    /// by id - unaffected by OpenRGB's device name ("HYTE THICC Q60"), which silently broke a
    /// substring match. Null when disconnected. COM port names are alphanumeric, so no
    /// RgbDevice.Sanitize transform is needed to reconstruct the id.
    /// </summary>
    public string? OwnedOpenRgbDeviceId =>
        _hub.IsConnected && !string.IsNullOrEmpty(_hub.PortName) ? $"openrgb-l-{_hub.PortName}" : null;

    public bool OwnsOpenRgbDevice(RgbDevice device) =>
        OwnedOpenRgbDeviceId is { } id && string.Equals(device.StableId, id, StringComparison.OrdinalIgnoreCase);

    public event Action? DevicesChanged;

    /// <summary>
    /// Called by the Q-series heartbeat worker after a successful (re)open so the
    /// RgbBridge rebuilds its frame map. Cheap signature compare suppresses repeat
    /// invocations when nothing changed - including a Nexus Link device plugged or
    /// unplugged on channel 1 or 2, not just a connect/disconnect transition.
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
        if (!_hub.IsConnected) return "disconnected";
        var sb = new System.Text.StringBuilder(_hub.DeviceId);
        sb.Append('|');
        AppendChannelSignature(sb, _hub.State.Channel1Devices);
        sb.Append(';');
        AppendChannelSignature(sb, _hub.State.Channel2Devices);
        return sb.ToString();
    }

    private static void AppendChannelSignature(System.Text.StringBuilder sb, IReadOnlyList<QSeriesLinkDevice> devices)
    {
        foreach (var d in devices) sb.Append(d.Model).Append(d.LedCount).Append(',');
    }

    private static string CardName(string variant) => variant switch
    {
        QSeriesCoolerProtocol.VariantQ80 => "HYTE Q80",
        _ => "HYTE Q60",
    };

    public GetLightingDevicesResponse GetAll()
    {
        var resp = new GetLightingDevicesResponse { IsInit = true };
        if (!_hub.IsConnected) return resp;
        var id = _hub.DeviceId;
        var settings = _store.Load();
        resp.Devices.Add(BuildCard(
            id: id, name: CardName(_hub.Variant),
            deviceKey: DeviceKeyOf(_hub.Variant),
            firmwareLedCount: QSeriesCoolerHub.LedCount,
            settings.Devices.DisabledLightingDevices,
            settings.Devices.LightingDevicePrefs,
            settings.Lighting.DeviceLayouts));

        var zoneLedCounts = settings.Devices.ZoneLedCounts;
        var slot = 0;
        foreach (var (channel, devices) in ChannelPairs())
        {
            foreach (var dev in devices)
            {
                if (dev.LedCount <= 0) continue;
                resp.Devices.Add(BuildLinkZone(
                    id: LinkDeviceId(id, channel, dev.Slot),
                    name: $"{CardName(_hub.Variant)} - {dev.Model} (Port {channel} #{dev.Slot})",
                    rawName: $"{dev.Model} (Port {channel} #{dev.Slot})",
                    iconType: dev.FanCount > 0 ? "fan" : "strip",
                    firmwareLedCount: dev.LedCount,
                    zoneIndex: slot++,
                    parentDeviceId: id,
                    deviceKey: DeviceKeyComputer.ForFirstParty(QSeriesCoolerProtocol.VendorId, QSeriesCoolerProtocol.ProductIdForVariant(_hub.Variant), dev.Model),
                    settings.Devices.DisabledLightingDevices,
                    settings.Devices.LightingDevicePrefs,
                    settings.Lighting.DeviceLayouts,
                    zoneLedCounts));
            }
        }
        return resp;
    }

    /// <summary>Both Nexus Link channels paired with their channel number, in stream order (1 then 2).</summary>
    private IEnumerable<(int Channel, IReadOnlyList<QSeriesLinkDevice> Devices)> ChannelPairs()
    {
        yield return (QSeriesCoolerProtocol.LinkChannel1, _hub.State.Channel1Devices);
        yield return (QSeriesCoolerProtocol.FanChannel, _hub.State.Channel2Devices);
    }

    private static string LinkDeviceId(string hubId, int channel, int slot) => $"{hubId}:p{channel}:{slot}";

    private static LightingDevice BuildLinkZone(
        string id, string name, string rawName, string iconType,
        int firmwareLedCount, int zoneIndex, string parentDeviceId, string deviceKey,
        IReadOnlyList<string> disabled,
        IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        IReadOnlyDictionary<string, DeviceLayout> layouts,
        IReadOnlyDictionary<string, int> zoneLedCounts)
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
        var effectiveLedCount = firmwareLedCount;
        if (zoneLedCounts.TryGetValue(id, out var persisted))
        {
            effectiveLedCount = Math.Clamp(persisted, 0, firmwareLedCount);
        }
        var (defX, defY, defW, defH) = DefaultQSeriesLinkLayout(zoneIndex);
        layouts.TryGetValue(id, out var layout);
        return new LightingDevice
        {
            Id = id,
            DeviceKey = deviceKey,
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

    /// <summary>Visible-canvas default layout for the per-Nexus-Link-device
    /// zones, in a 4-col, 2-row grid to the right of the Panel + Logo card's
    /// column.</summary>
    private static (float x, float y, float w, float h) DefaultQSeriesLinkLayout(int slot)
    {
        const float Y = 370f;
        const float W = 120f;
        const float H = 105f;
        const float Gap = 140f;
        const float BaseX = 260f;
        const int Cols = 4;
        const int Rows = 2; // 370 + 105 + 105 = 580 ≤ canvas bottom
        const float RowGap = 105f;
        var s = ((slot % (Cols * Rows)) + Cols * Rows) % (Cols * Rows);
        var col = s % Cols;
        var row = s / Cols;
        return (BaseX + col * Gap, Y + row * RowGap, W, H);
    }

    private static LightingDevice BuildCard(
        string id, string name, string deviceKey, int firmwareLedCount,
        IReadOnlyList<string> disabled,
        IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        IReadOnlyDictionary<string, DeviceLayout> layouts)
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
        var (defX, defY, defW, defH) = DefaultQSeriesLayout();
        layouts.TryGetValue(id, out var layout);
        return new LightingDevice
        {
            Id = id, DeviceKey = deviceKey, Name = name, Type = "ledstrip", IconType = "cooler",
            LedsOn = isOn, Brightness = brightness, Hue = hue, Saturation = saturation,
            LedCount = firmwareLedCount,
            CanvasX = layout?.X ?? defX, CanvasY = layout?.Y ?? defY,
            CanvasW = layout?.W ?? defW, CanvasH = layout?.H ?? defH,
            CanvasRotation = ((((layout?.Rotation ?? 0) % 360) + 360) % 360),
            // Standalone top-level card (no ParentDeviceId / ZoneIndex). LED count is
            // fixed by the panel + logo geometry, so ZoneResizable=false hides the
            // led-count editor.
            ZoneType = "matrix", ZoneResizable = false,
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
        // The Panel + Logo card's LED count is fixed by its geometry
        // (ZoneResizable=false hides the editor for it); only the per-Nexus-Link-device
        // zones honour a trimmed count.
        if (count < 0 || id == _hub.DeviceId) return;
        _store.Update(s => s.Devices.ZoneLedCounts[id] = count);
    }

    public void Identify(string id, int durationMs) => _identify.Schedule(id, durationMs);

    private static string DeviceKeyOf(string variant) => DeviceKeyComputer.ForFirstParty(
        QSeriesCoolerProtocol.VendorId, QSeriesCoolerProtocol.ProductIdForVariant(variant));

    // ── IDeviceStructureSource ──

    /// <summary>One structure for the Panel + Logo card plus one per Nexus Link zone, so the LED map editor resolves every card's LED space.</summary>
    public IReadOnlyList<DeviceStructure> GetStructures()
    {
        if (!_hub.IsConnected || string.IsNullOrEmpty(_hub.DeviceId))
        {
            return Array.Empty<DeviceStructure>();
        }
        var id = _hub.DeviceId;
        var counts = _store.Load().Devices.ZoneLedCounts;
        var structures = new List<DeviceStructure> { BuildStructure() };
        foreach (var (channel, devices) in ChannelPairs())
        {
            foreach (var dev in devices)
            {
                if (dev.LedCount <= 0) continue;
                var rawName = $"{dev.Model} (Port {channel} #{dev.Slot})";
                structures.Add(BuildLinkStructure(
                    LinkDeviceId(id, channel, dev.Slot), $"{CardName(_hub.Variant)} - {rawName}", rawName,
                    dev.LedCount, dev.Model, counts));
            }
        }
        return structures;
    }

    private DeviceStructure BuildLinkStructure(
        string id, string name, string rawName, int firmwareLedCount, string keySlug,
        IReadOnlyDictionary<string, int> counts)
    {
        var ledCount = counts.TryGetValue(id, out var persisted)
            ? Math.Clamp(persisted, 0, firmwareLedCount)
            : firmwareLedCount;
        var key = DeviceKeyComputer.ForFirstParty(QSeriesCoolerProtocol.VendorId, QSeriesCoolerProtocol.ProductIdForVariant(_hub.Variant), keySlug);
        var structure = new DeviceStructure { DeviceId = id, Name = name, DeviceKey = key, Partitionable = false };
        structure.Segments.Add(new StructureSegment
        {
            Index = 0,
            Name = rawName,
            LedCount = ledCount,
            FrameLedCount = ledCount,
            Resizable = true,
            ZoneType = "linear",
        });
        structure.DefaultZones.Add(new DefaultZoneDef
        {
            Id = id,
            Name = name,
            RawName = rawName,
            DeviceKey = key,
            LegacyZoneIndex = -1,
            Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = ledCount } },
        });
        return structure;
    }

    private DeviceStructure BuildStructure()
    {
        var id = _hub.DeviceId;
        var name = CardName(_hub.Variant);
        var key = DeviceKeyOf(_hub.Variant);
        var (u, v) = BuildLedUv();

        var structure = new DeviceStructure { DeviceId = id, Name = name, DeviceKey = key, Partitionable = false };
        structure.Segments.Add(new StructureSegment
        {
            Index = 0,
            Name = "Panel + Logo",
            LedCount = QSeriesCoolerHub.LedCount,
            FrameLedCount = QSeriesCoolerHub.LedCount,
            Resizable = false,
            ZoneType = "matrix",
            DefaultU = u,
            DefaultV = v,
        });
        structure.DefaultZones.Add(new DefaultZoneDef
        {
            Id = id,
            Name = name,
            RawName = "Panel + Logo",
            DeviceKey = key,
            LegacyZoneIndex = -1,
            Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = QSeriesCoolerHub.LedCount } },
        });
        return structure;
    }

    // Hold the DeviceFrame across RgbBridge rebuilds so the per-LED buffer isn't
    // re-zeroed for one tick (same reuse pattern as CNVS / NP50 / MiniHub).
    private DeviceFrame? _frame;
    private readonly Dictionary<string, DeviceFrame> _linkFrameCache = new();

    public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex)
    {
        if (!_hub.IsConnected) { _frame = null; return Array.Empty<DeviceFrame>(); }
        var id = _hub.DeviceId;
        var settings = _store.Load();
        var layouts = settings.Lighting.DeviceLayouts;
        var ledCount = QSeriesCoolerHub.LedCount;

        var (defX, defY, defW, defH) = DefaultQSeriesLayout();
        layouts.TryGetValue(id, out var layout);
        var rot = ((((layout?.Rotation ?? 0) % 360) + 360) % 360);

        if (_frame is not null
            && _frame.Index == startingIndex
            && _frame.Id == id
            && _frame.LedCount == ledCount)
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

        var frames = new List<DeviceFrame> { _frame };
        var idx = startingIndex + 1;
        var zoneLedCounts = settings.Devices.ZoneLedCounts;
        var slot = 0;
        foreach (var (channel, devices) in ChannelPairs())
        {
            foreach (var dev in devices)
            {
                if (dev.LedCount <= 0) continue;
                frames.Add(BuildOrReuseLinkFrame(
                    LinkDeviceId(id, channel, dev.Slot), dev.LedCount, slot++, layouts, zoneLedCounts, ref idx));
            }
        }

        if (_linkFrameCache.Count > frames.Count - 1)
        {
            var live = new HashSet<string>(frames.Count);
            foreach (var f in frames) live.Add(f.Id);
            var stale = new List<string>();
            foreach (var k in _linkFrameCache.Keys) if (!live.Contains(k)) stale.Add(k);
            foreach (var k in stale) _linkFrameCache.Remove(k);
        }
        return frames;
    }

    private DeviceFrame BuildOrReuseLinkFrame(
        string id, int firmwareLedCount, int zoneIndex,
        IReadOnlyDictionary<string, DeviceLayout> layouts,
        IReadOnlyDictionary<string, int> zoneLedCounts,
        ref int idx)
    {
        var effectiveLedCount = firmwareLedCount;
        if (zoneLedCounts.TryGetValue(id, out var persisted))
        {
            effectiveLedCount = Math.Clamp(persisted, 0, firmwareLedCount);
        }
        var (defX, defY, defW, defH) = DefaultQSeriesLinkLayout(zoneIndex);
        layouts.TryGetValue(id, out var layout);
        var rot = ((((layout?.Rotation ?? 0) % 360) + 360) % 360);
        var thisIdx = idx++;

        if (_linkFrameCache.TryGetValue(id, out var existing)
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
        _linkFrameCache[id] = frame;
        return frame;
    }

    /// <summary>
    /// Per-LED canvas position for the card's wire order. The panel's 42 LEDs
    /// occupy the upper 3/4 of the card on their 5x9 grid and the logo's 4 sit
    /// in a diamond below it, so a canvas-sampled effect lands on the physical
    /// cell rather than on a linear index the hardware does not have.
    /// </summary>
    private static (float[] u, float[] v) BuildLedUv()
    {
        var total = QSeriesCoolerHub.LedCount;
        var u = new float[total];
        var v = new float[total];
        const float panelBottom = 0.74f;

        var panel = QSeriesCoolerProtocol.BacklightWireOrder;
        for (var i = 0; i < panel.Length; i++)
        {
            var (col, row) = panel[i];
            u[i] = (col + 0.5f) / QSeriesCoolerProtocol.BacklightColumns;
            v[i] = (row + 0.5f) / QSeriesCoolerProtocol.BacklightRows * panelBottom;
        }

        var logo = QSeriesCoolerProtocol.LogoWireOrder;
        for (var i = 0; i < logo.Length; i++)
        {
            var (col, row) = logo[i];
            u[panel.Length + i] = (col + 0.5f) / 3f;
            v[panel.Length + i] = panelBottom + (row + 0.5f) / 3f * (1f - panelBottom);
        }
        return (u, v);
    }

    /// <summary>Default canvas placement for the Q-series cooler card. User can drag and persist;
    /// the composite re-grids anything without a saved layout anyway.</summary>
    private static (float x, float y, float w, float h) DefaultQSeriesLayout()
    {
        return (40f, 730f, 200f, 200f);
    }
}
