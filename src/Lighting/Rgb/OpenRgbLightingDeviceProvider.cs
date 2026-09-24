using System;
using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Models.Devices;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.Rgb;

/// <summary>
/// Real <see cref="ILightingDeviceProvider"/> backed by the OpenRGB SDK via the
/// <see cref="RgbBridge"/>. Returns whatever devices the bundled OpenRGB-headless
/// subprocess has detected.
///
/// Per-device brightness/hue/saturation persistence still goes to the config store
/// - same shape as the stub - so the UI can store user preferences even when no
/// effect is currently running.
///
/// Card emission derives from each device's zone partition (see
/// <see cref="OpenRgbZoneSupport"/>): with no partition persisted this is
/// exactly the legacy behavior - motherboards with multiple ARGB headers are
/// split into one card per zone ("openrgb-N-Z"), everything else is one
/// whole-device card. ARGB is a one-way protocol so the LED count per header
/// cannot be auto-detected - users set it via the zone card and we persist +
/// re-apply it via OpenRGB's RESIZEZONE opcode.
/// </summary>
public sealed class OpenRgbLightingDeviceProvider : ILightingDeviceProvider, IDeviceStructureSource
{
    private readonly RgbBridge _bridge;
    private readonly IConfigStore _store;

    public OpenRgbLightingDeviceProvider(RgbBridge bridge, IConfigStore store)
    {
        _bridge = bridge;
        _store = store;
    }

    public bool IsConnected => _bridge.IsConnected;

    public GetLightingDevicesResponse GetAll()
        => OpenRgbZoneSupport.BuildCards(_bridge.Devices, _store.Load(), _bridge.IsConnected, _bridge.DrivableIds);

    // ── IDeviceStructureSource ──

    public IReadOnlyList<DeviceStructure> GetStructures()
    {
        var devices = _bridge.Devices;
        if (devices.Count == 0)
            return Array.Empty<DeviceStructure>();
        var settings = _store.Load();
        if (SplitMotherboardDeviceMigration.NeedsApply(settings, devices))
        {
            _store.Update(s => SplitMotherboardDeviceMigration.Apply(s, devices));
            settings = _store.Load();
        }
        var structures = new List<DeviceStructure>(devices.Count);
        foreach (var d in devices)
        {
            structures.AddRange(OpenRgbZoneSupport.BuildStructures(d, settings));
        }
        return structures;
    }

    public void SetDisabled(IReadOnlyList<string> ids) => _store.Update(s =>
    {
        s.Devices.DisabledLightingDevices = new List<string>(ids);
    });

    // Replaces the list reference rather than mutating in place so the 30fps
    // RgbBridge.OnFrame reader never observes a torn state.
    public void SetPower(string id, bool on) => _store.Update(s =>
    {
        var current = s.Devices.DisabledLightingDevices;
        if (on)
        {
            if (!current.Contains(id))
                return;
            var next = new List<string>(current.Count);
            foreach (var x in current)
            { if (x != id) next.Add(x); }
            s.Devices.DisabledLightingDevices = next;
        }
        else
        {
            if (current.Contains(id))
                return;
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
        pref.Brightness = brightness;
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

    public void SetZoneLedCount(string id, int count)
    {
        if (count < 0 || count > 1024)
            return;
        if (!TryResolveResizableZone(id, out var physIdx, out var zoneIdx, out var deviceId))
            return;

        // Counts persist under the hardware segment key (the legacy zone-card
        // id) so the wiring choice survives any re-partition, and overrides
        // beyond the new count are pruned in the segment-local store. That
        // store is keyed on the PORT at segment 0 since each header became its
        // own device; pruning the board's key matched nothing.
        var segmentKey = $"{deviceId}-{zoneIdx}";
        _store.Update(s =>
        {
            s.Devices.ZoneLedCounts[segmentKey] = count;
            if (s.Devices.DeviceLedOverrides.TryGetValue(segmentKey, out var list))
            {
                var pruned = new List<SegmentLedOverride>(list.Count);
                foreach (var o in list)
                {
                    if (o.LedIndex < count)
                        pruned.Add(o);
                }
                if (pruned.Count != list.Count)
                    s.Devices.DeviceLedOverrides[segmentKey] = pruned;
            }
            ZoneResolution.DropChainForCount(s, segmentKey);
        });

        _bridge.RequestZoneResize(physIdx, zoneIdx, count);
    }

    public void Identify(string id, int durationMs)
    {
        _bridge.BeginIdentify(id, durationMs);
    }

    /// <summary>
    /// Default on-canvas rectangle for a full device card, arranged in a
    /// 3-column grid so multiple devices don't overlap. Roughly square so a
    /// grid or ring LED map doesn't render letterboxed. Canvas coords are
    /// 1000x600 internal units; the UI rescales. Row count is capped so no
    /// card lands off-canvas; once the grid is full the slot wraps to
    /// position 0 (the top-left), stacking new cards on existing defaults
    /// that the user can drag apart.
    /// </summary>
    internal static (float x, float y, float w, float h) DefaultCardLayout(int slot)
    {
        const float W = 140f;
        const float H = 120f;
        const int Cols = 3;
        const int Rows = 4; // 40 + 3*140 + 120 = 580 ≤ canvas bottom (588 with PAD)
        const float ColGap = 220f;
        const float RowGap = 140f;
        var s = ((slot % (Cols * Rows)) + Cols * Rows) % (Cols * Rows);
        var col = s % Cols;
        var row = s / Cols;
        return (30f + col * ColGap, 40f + row * RowGap, W, H);
    }

    /// <summary>
    /// Default on-canvas rectangle for a motherboard ARGB strip zone, roughly
    /// square like <see cref="DefaultCardLayout"/> so a grid or ring LED map
    /// doesn't render letterboxed. A 2-column grid anchored below the
    /// device-card area so strips don't pile on top of each other or
    /// overlap the cards. Same wrap rule as <see cref="DefaultCardLayout"/>:
    /// slots past the visible grid loop back to the first column/row.
    /// </summary>
    internal static (float x, float y, float w, float h) DefaultStripLayout(int slot)
    {
        const float W = 140f;
        const float H = 120f;
        const int Cols = 2;
        const int Rows = 3; // 190 + 2*130 + 120 = 570 ≤ canvas bottom
        const float ColGap = 200f;
        const float RowGap = 130f;
        // Card row 0 bottom is 40 + 120 = 160; strips start below that so
        // strips never collide with the first card row.
        const float BaseY = 190f;
        var s = ((slot % (Cols * Rows)) + Cols * Rows) % (Cols * Rows);
        var col = s % Cols;
        var row = s / Cols;
        return (40f + col * ColGap, BaseY + row * RowGap, W, H);
    }

    /// <summary>
    /// Resolve a card id that supports LED-count editing to its
    /// (physicalIndex, zoneIndex, deviceId). Handles the legacy zone-card
    /// shape "{stableId}-{zoneIndex}" and custom-partition ids whose zone is
    /// exactly one whole resizable segment (the only shape rule 2 allows on a
    /// header).
    /// </summary>
    private bool TryResolveResizableZone(string id, out int physicalIndex, out int zoneIndex, out string deviceId)
    {
        physicalIndex = -1;
        zoneIndex = -1;
        deviceId = "";
        if (string.IsNullOrEmpty(id))
            return false;

        var devices = _bridge.Devices;
        for (int i = 0; i < devices.Count; i++)
        {
            var stableId = devices[i].StableId;
            if (id.Length > stableId.Length + 1
                && id.StartsWith(stableId, StringComparison.Ordinal)
                && id[stableId.Length] == '-'
                && int.TryParse(id.AsSpan(stableId.Length + 1), out zoneIndex)
                && zoneIndex >= 0)
            {
                physicalIndex = devices[i].Index;
                deviceId = stableId;
                return true;
            }
        }

        // Custom-partition card: find the owning device, resolve its zones,
        // and accept only a whole-resizable-segment zone.
        var settings = _store.Load();
        foreach (var d in devices)
        {
            if (!id.StartsWith(d.StableId, StringComparison.Ordinal))
                continue;
            foreach (var structure in OpenRgbZoneSupport.BuildStructures(d, settings))
            {
                foreach (var zone in ZoneResolution.Resolve(structure, settings))
                {
                    if (zone.Id != id)
                        continue;
                    if (ZoneResolution.WholeResizableSegment(structure, zone, settings) < 0)
                        return false;
                    // A port structure holds one segment, so its own index is
                    // always 0; the OpenRGB zone to resize is the one the port
                    // was split from, which its default zone still records.
                    var physicalZone = structure.DefaultZones.Count > 0
                        ? structure.DefaultZones[0].LegacyZoneIndex
                        : -1;
                    if (physicalZone < 0)
                        return false;
                    physicalIndex = d.Index;
                    zoneIndex = physicalZone;
                    deviceId = d.StableId;
                    return true;
                }
            }
        }
        return false;
    }
}
