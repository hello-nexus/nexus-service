using System;
using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.Nollie;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// One card per port of every attached Nollie controller, mirroring
/// <see cref="SmartHubLightingDeviceProvider"/>. A port is one ARGB channel,
/// or the six channels behind a Strimer connector (<see cref="NolliePort"/>).
/// The protocol has no read command, so a port's LED count is always the
/// user's declaration, capped at <see cref="NolliePort.MaxLedCount"/>. Ids
/// are namespaced per controller (<c>{deviceId}:{slug}</c>) because several
/// commonly share a machine.
/// </summary>
public sealed class NollieLightingDeviceProvider :
    ILightingDeviceProvider, ILightingFrameContributor, IDeviceStructureSource
{
    private readonly NollieHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private string _lastSignature = "";

    public NollieLightingDeviceProvider(NollieHub hub, IConfigStore store, Np50IdentifyTracker identify)
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

    /// <summary>Covers declared counts, not just the attached set: a resize must fire DevicesChanged so the bridge rebuilds frames at the new length.</summary>
    private string BuildSignature()
    {
        var controllers = _hub.Controllers;
        if (controllers.Count == 0) return "disconnected";
        var counts = _store.Load().Devices.ZoneLedCounts;
        var sb = new System.Text.StringBuilder();
        foreach (var c in controllers)
        {
            sb.Append(c.DeviceId).Append(':').Append(c.Spec.Channels).Append('=');
            foreach (var port in c.Spec.Ports)
            {
                sb.Append(DeclaredLedCount(counts, PortId(c.DeviceId, port), port)).Append(',');
            }
            sb.Append('|');
        }
        return sb.ToString();
    }

    /// <summary>Card id for one port of one controller.</summary>
    public static string PortId(string deviceId, NolliePort port) => $"{deviceId}:{port.Slug}";

    /// <summary>Persisted user declaration clamped to the port's ceiling; 0 until set, as the firmware reports no count.</summary>
    public static int DeclaredLedCount(IReadOnlyDictionary<string, int> counts, string id, NolliePort port)
        => counts.TryGetValue(id, out var persisted)
            ? Math.Clamp(persisted, 0, port.MaxLedCount)
            : 0;

    public GetLightingDevicesResponse GetAll()
    {
        var resp = new GetLightingDevicesResponse { IsInit = true };
        var controllers = _hub.Controllers;
        if (controllers.Count == 0) return resp;

        var settings = _store.Load();
        var disabled = settings.Devices.DisabledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var layouts = settings.Lighting.DeviceLayouts;
        var counts = settings.Devices.ZoneLedCounts;
        var slot = 0;

        foreach (var controller in controllers)
        {
            foreach (var port in controller.Spec.Ports)
            {
                var structure = BuildPortStructure(controller, port, counts);
                foreach (var zone in ZoneResolution.Resolve(structure, settings))
                {
                    resp.Devices.Add(BuildZoneCard(structure, zone, slot++, controller.DeviceId, disabled, prefs, layouts, settings));
                }
            }
        }
        return resp;
    }

    /// <summary>Card for one resolved zone of a port - the whole port when unchained, one per product once a chain owns it.</summary>
    private static LightingDevice BuildZoneCard(
        DeviceStructure structure, ResolvedZone zone, int slot, string parentDeviceId,
        IReadOnlyList<string> disabled,
        IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        IReadOnlyDictionary<string, DeviceLayout> layouts,
        NexusSettings settings)
    {
        var id = zone.Id;
        var isOn = true;
        for (var i = 0; i < disabled.Count; i++) if (disabled[i] == id) { isOn = false; break; }
        var brightness = 100;
        var hue = 0f;
        var saturation = 1f;
        if (prefs.TryGetValue(id, out var pref))
        {
            brightness = pref.Brightness; hue = pref.Hue; saturation = pref.Saturation;
        }
        var (defX, defY, defW, defH) = DefaultNollieLayout(slot);
        layouts.TryGetValue(id, out var layout);
        return new LightingDevice
        {
            Id = id,
            DeviceKey = zone.DeviceKey,
            Name = zone.Name,
            Type = "ledstrip",
            IconType = "strip",
            LedsOn = isOn,
            Brightness = brightness,
            Hue = hue,
            Saturation = saturation,
            LedCount = zone.LedCount,
            EnabledLedCount = ZoneResolution.CountEnabled(structure, zone, id, zone.LedCount, zoneHint: 0, settings),
            CanvasX = layout?.X ?? defX,
            CanvasY = layout?.Y ?? defY,
            CanvasW = layout?.W ?? defW,
            CanvasH = layout?.H ?? defH,
            CanvasRotation = NormalizeRotation(layout?.Rotation ?? 0),
            ParentDeviceId = parentDeviceId,
            ZoneIndex = zone.Ordinal,
            ZoneType = "linear",
            // Only a zone that owns the whole port may resize it; a chain
            // link is sized by its product.
            ZoneResizable = ZoneResolution.WholeResizableSegment(structure, zone, settings) >= 0,
            DeviceId = structure.DeviceId,
            ZoneCustomizable = true,
        };
    }

    private static string PortKey(NollieController controller, NolliePort port)
        => DeviceKeyComputer.ForFirstParty(controller.Spec.VendorId, controller.Spec.ProductId, port.Slug);

    // ── IDeviceStructureSource ──

    public IReadOnlyList<DeviceStructure> GetStructures()
    {
        var controllers = _hub.Controllers;
        if (controllers.Count == 0) return Array.Empty<DeviceStructure>();

        var counts = _store.Load().Devices.ZoneLedCounts;
        var structures = new List<DeviceStructure>();
        foreach (var controller in controllers)
        {
            foreach (var port in controller.Spec.Ports)
            {
                structures.Add(BuildPortStructure(controller, port, counts));
            }
        }
        return structures;
    }

    /// <summary>
    /// One port's structure. Shared with the frame writer so the card
    /// list, the engine frames, and the bytes on the wire all resolve the
    /// same chain - a port split several ways in one of them and not the
    /// others would light the wrong LEDs rather than fail visibly.
    /// </summary>
    internal static DeviceStructure BuildPortStructure(NollieController controller, NolliePort port, IReadOnlyDictionary<string, int> counts)
    {
        var id = PortId(controller.DeviceId, port);
        var ledCount = DeclaredLedCount(counts, id, port);
        var name = $"{controller.Spec.Name} - {port.Name}";
        var rawName = port.Name;
        var key = PortKey(controller, port);
        var structure = new DeviceStructure { DeviceId = id, Name = name, DeviceKey = key };
        structure.Segments.Add(new StructureSegment
        {
            Index = 0,
            Name = rawName,
            LedCount = ledCount,
            FrameLedCount = ledCount,
            Resizable = true,
            // Without the ceiling a chain past it is accepted, then clamped on
            // the way to the firmware, so the partition no longer tiles the
            // segment and the port falls back to one zone - orphaning the
            // chain record and its per-zone mappings.
            MaxLedCount = port.MaxLedCount,
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

    /// <summary>The zones one port currently resolves to, in chain order.</summary>
    internal static IReadOnlyList<ResolvedZone> ResolvePortZones(NollieController controller, NolliePort port, NexusSettings settings)
        => ZoneResolution.Resolve(BuildPortStructure(controller, port, settings.Devices.ZoneLedCounts), settings);

    // ── ILightingDeviceProvider ──

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

    /// <summary>Clamps to the port's ceiling; an unknown id is ignored so a stale card cannot write a phantom entry.</summary>
    public void SetZoneLedCount(string id, int count)
    {
        if (count < 0) return;
        if (!TryResolve(id, out var controller, out var port)) return;
        var clamped = Math.Min(count, port.MaxLedCount);
        _store.Update(s =>
        {
            s.Devices.ZoneLedCounts[id] = clamped;
            ZoneResolution.DropChainForCount(s, id);
        });
        PushLedCountHandshake(controller);
        NollieStandalone.Refresh(controller, _store.Load());
        OnHubStateUpdated();
    }

    /// <summary>Re-declares every channel count to the one controller whose firmware takes the handshake.</summary>
    private void PushLedCountHandshake(NollieController controller)
    {
        if (!controller.Spec.WantsLedCountHandshake) return;
        var counts = _store.Load().Devices.ZoneLedCounts;
        var perChannel = new int[controller.Spec.Channels];
        foreach (var port in controller.Spec.Ports)
        {
            var total = DeclaredLedCount(counts, PortId(controller.DeviceId, port), port);
            for (var lane = 0; lane < port.Lanes; lane++)
            {
                perChannel[port.FirstChannel + lane] = port.LaneLeds(total, lane);
            }
        }
        controller.SendLedCounts(perChannel);
    }

    /// <summary>
    /// Re-sends what follows from a port count, for a caller that wrote
    /// ZoneLedCounts directly (the chain POST) instead of through
    /// SetZoneLedCount: the LED-count handshake on the one controller whose
    /// firmware wants it, and the standalone settings, whose MOS bit follows
    /// the GPU port. A no-op for an unknown id.
    /// </summary>
    public void PushLedCountHandshakeFor(string id)
    {
        if (!TryResolve(id, out var controller, out _)) return;
        PushLedCountHandshake(controller);
        NollieStandalone.Refresh(controller, _store.Load());
    }

    public void Identify(string id, int durationMs) => _identify.Schedule(id, durationMs);

    /// <summary>Resolves a port id back to its controller and port. Controller ids carry no ':', so the last one splits the slug off.</summary>
    public bool TryResolve(string id, out NollieController controller, out NolliePort port)
    {
        controller = null!;
        port = null!;
        if (string.IsNullOrEmpty(id)) return false;
        var sep = id.LastIndexOf(':');
        if (sep <= 0 || sep == id.Length - 1) return false;
        var found = _hub.Find(id[..sep]);
        if (found is null) return false;
        var slug = id.AsSpan(sep + 1);
        foreach (var p in found.Spec.Ports)
        {
            if (slug.SequenceEqual(p.Slug))
            {
                controller = found;
                port = p;
                return true;
            }
        }
        return false;
    }

    // ── ILightingFrameContributor ──

    // Reuse DeviceFrame instances across a refresh so the writer never sees a
    // fresh zero-filled frame for one tick and blanks a channel (same rationale
    // as the MiniHub / NP50 / Smart Hub providers).
    private readonly Dictionary<string, DeviceFrame> _frameCache = new(StringComparer.Ordinal);

    public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex)
    {
        var controllers = _hub.Controllers;
        if (controllers.Count == 0) return Array.Empty<DeviceFrame>();

        var settings = _store.Load();
        var layouts = settings.Lighting.DeviceLayouts;
        var counts = settings.Devices.ZoneLedCounts;
        var frames = new List<DeviceFrame>();
        var idx = startingIndex;
        var slot = 0;

        foreach (var controller in controllers)
        {
            foreach (var port in controller.Spec.Ports)
            {
                var structure = BuildPortStructure(controller, port, counts);
                foreach (var zone in ZoneResolution.Resolve(structure, settings))
                {
                    var id = zone.Id;
                    var (defX, defY, defW, defH) = DefaultNollieLayout(slot++);
                    layouts.TryGetValue(id, out var layout);
                    var rot = NormalizeRotation(layout?.Rotation ?? 0);
                    var thisIdx = idx++;

                    if (_frameCache.TryGetValue(id, out var existing)
                        && existing.Index == thisIdx
                        && existing.LedCount == zone.LedCount)
                    {
                        existing.X = layout?.X ?? defX;
                        existing.Y = layout?.Y ?? defY;
                        existing.W = layout?.W ?? defW;
                        existing.H = layout?.H ?? defH;
                        existing.Rotation = rot;
                        frames.Add(existing);
                        continue;
                    }

                    var frame = new DeviceFrame(
                        index: thisIdx, id: id, ledCount: zone.LedCount,
                        x: layout?.X ?? defX, y: layout?.Y ?? defY,
                        w: layout?.W ?? defW, h: layout?.H ?? defH, rotation: rot);
                    _frameCache[id] = frame;
                    frames.Add(frame);
                }
            }
        }

        if (_frameCache.Count > frames.Count)
        {
            var live = new HashSet<string>(frames.Count, StringComparer.Ordinal);
            foreach (var f in frames) live.Add(f.Id);
            var stale = new List<string>();
            foreach (var k in _frameCache.Keys) if (!live.Contains(k)) stale.Add(k);
            foreach (var k in stale) _frameCache.Remove(k);
        }
        return frames;
    }

    private static int NormalizeRotation(int rotation) => (((rotation % 360) + 360) % 360);

    /// <summary>Grid of strip cards; wraps into rows since a many-channel board contributes a card per port.</summary>
    internal static (float x, float y, float w, float h) DefaultNollieLayout(int slot)
    {
        const float W = 120f;
        const float H = 105f;
        const float GapX = 140f;
        const float GapY = 125f;
        const float BaseX = 40f;
        const float BaseY = 40f;
        const int Cols = 4;
        // No row wrap: cards keep descending so a second controller's channels
        // never land exactly on the first's. The canvas scrolls.
        var s = slot < 0 ? 0 : slot;
        return (BaseX + (s % Cols) * GapX, BaseY + (s / Cols) * GapY, W, H);
    }
}
