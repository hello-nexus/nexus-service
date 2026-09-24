using System;
using System.Collections.Generic;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.Zones;

/// <summary>
/// Central card-to-layout resolution over the partition model. Aggregates
/// every provider's <see cref="IDeviceStructureSource"/> and resolves any
/// lighting card id - default-partition zone, custom zone, or legacy
/// non-partitionable card - to its layout, mapping user overrides through
/// the device's segment slices. Also owns the tracker-discipline rule for
/// pushing resolved layouts back into live engine frames (OpenRGB frames are
/// untracked; contributor frames must go through
/// <see cref="ContributorFrameLayouts"/>).
/// </summary>
public sealed class ZoneTopology
{
    private readonly IEnumerable<IDeviceStructureSource> _sources;
    private readonly IConfigStore _store;
    private readonly LightingEngine _engine;
    private readonly ContributorFrameLayouts _contributorLayouts;
    private readonly RgbBridge? _bridge;

    public ZoneTopology(
        IEnumerable<IDeviceStructureSource> sources,
        IConfigStore store,
        LightingEngine engine,
        ContributorFrameLayouts contributorLayouts,
        RgbBridge? bridge = null)
    {
        _sources = sources;
        _store = store;
        _engine = engine;
        _contributorLayouts = contributorLayouts;
        _bridge = bridge;
    }

    public IEnumerable<DeviceStructure> AllStructures()
    {
        foreach (var source in _sources)
        {
            foreach (var structure in source.GetStructures())
                yield return structure;
        }
    }

    public DeviceStructure? FindStructure(string deviceId)
    {
        foreach (var source in _sources)
        {
            foreach (var structure in source.GetStructures())
            {
                if (structure.DeviceId == deviceId)
                    return structure;
            }
        }
        return null;
    }

    public IReadOnlyList<ResolvedZone> ZonesFor(DeviceStructure structure, NexusSettings? settings = null)
        => ZoneResolution.Resolve(structure, settings ?? _store.Load());

    /// <summary>Locate the (structure, zone) pair a card id belongs to; null for non-partitionable cards.</summary>
    public (DeviceStructure Structure, ResolvedZone Zone)? FindZone(string cardId, NexusSettings settings)
    {
        foreach (var source in _sources)
        {
            foreach (var structure in source.GetStructures())
            {
                // Cheap pre-filter: every zone id starts with the device id
                // except legacy keeb suffixes which also share the prefix.
                if (!cardId.StartsWith(structure.DeviceId, StringComparison.Ordinal))
                    continue;
                foreach (var zone in ZoneResolution.Resolve(structure, settings))
                {
                    if (zone.Id == cardId)
                        return (structure, zone);
                }
            }
        }
        return null;
    }

    /// <summary>Override context for a card: its zone slices when partition-backed, else the identity (single-segment) context.</summary>
    public ZoneOverrideContext ContextFor(string cardId, NexusSettings settings)
    {
        var hit = FindZone(cardId, settings);
        return hit is { } z
            ? ZoneResolution.ContextOf(z.Structure, z.Zone)
            : ZoneOverrideContext.Identity(cardId);
    }

    public sealed class CardResolution
    {
        public required ResolvedLedLayout Layout { get; init; }
        /// <summary>Owning OpenRGB device when the card is bridge-backed (LED names, untracked frame application); null for contributor cards.</summary>
        public RgbDevice? Device { get; init; }
    }

    /// <summary>
    /// Resolve any lighting card id to its full layout stack. Partition-backed
    /// cards resolve through their zone; everything else falls back to the
    /// legacy paths (OpenRGB resolver, then engine frame with provider seeds).
    /// </summary>
    public CardResolution? ResolveCard(string cardId, NexusSettings settings)
    {
        var hit = FindZone(cardId, settings);
        if (hit is { } z)
        {
            return ResolveZone(z.Structure, z.Zone, settings);
        }

        if (_bridge is not null)
        {
            var (device, zoneIdx) = OpenRgbResolver.Resolve(cardId, _bridge.Devices);
            if (device is not null)
            {
                return new CardResolution
                {
                    Layout = LedLayoutResolver.ResolveOpenRgb(device, zoneIdx, cardId, settings),
                    Device = device,
                };
            }
        }

        foreach (var frame in _engine.Devices)
        {
            if (frame.Id == cardId)
            {
                var (defU, defV) = _contributorLayouts.GetDefaults(cardId);
                return new CardResolution
                {
                    Layout = LedLayoutResolver.ResolveSeeded(cardId, frame.LedCount, defU, defV, settings),
                };
            }
        }
        return null;
    }

    /// <summary>
    /// Resolve a zone already known to belong to a structure, skipping the
    /// cardId lookup <see cref="FindZone"/> performs. A caller holding a
    /// structure/zone pair that a registered source would not currently
    /// reproduce (a chain preview's detached copy) resolves against exactly
    /// that pair instead.
    /// </summary>
    public CardResolution ResolveZone(DeviceStructure structure, ResolvedZone zone, NexusSettings settings)
    {
        var rgbDevice = FindRgbDevice(structure.OwningDeviceId);
        if (rgbDevice is not null)
        {
            var layout = zone.IsDefault
                ? LedLayoutResolver.ResolveOpenRgb(rgbDevice, zone.LegacyZoneIndex, zone.Id, settings,
                    ZoneResolution.ContextOf(structure, zone))
                : LedLayoutResolver.ResolveZoneOpenRgb(rgbDevice, structure, zone, settings);
            return new CardResolution { Layout = layout, Device = rgbDevice };
        }
        // Structure-authored segment defaults win (they follow the zone's
        // slices, so any partition shape keeps its true sub-shape); the
        // tracker snapshot covers providers that only author per-frame UVs.
        var (defU, defV) = ZoneResolution.DefaultUv(structure, zone);
        if (defU is null || defV is null)
        {
            (defU, defV) = _contributorLayouts.GetDefaults(zone.Id);
        }
        var seeded = LedLayoutResolver.ResolveSeeded(zone.Id, zone.FrameLedCount, defU, defV, settings,
            ZoneResolution.ContextOf(structure, zone));
        return new CardResolution { Layout = seeded };
    }

    /// <summary>Push a resolution into the matching live engine frame, honoring the contributor-tracker discipline.</summary>
    public void ApplyToEngine(string cardId, CardResolution resolution)
    {
        foreach (var frame in _engine.Devices)
        {
            if (frame.Id != cardId)
                continue;
            if (resolution.Device is not null)
            {
                LedLayoutResolver.ApplyToFrame(frame, resolution.Layout);
            }
            else
            {
                _contributorLayouts.Apply(frame, resolution.Layout);
            }
            break;
        }
    }

    /// <summary>Re-resolve the full layout stack for a card and push it into its live engine frame.</summary>
    public void RefreshCardFrame(string cardId)
    {
        var settings = _store.Load();
        var resolution = ResolveCard(cardId, settings);
        if (resolution is not null)
        {
            ApplyToEngine(cardId, resolution);
        }
    }

    /// <summary>True when any persisted override maps into the card's LED space (auto-apply guard).</summary>
    public bool HasUserOverrides(string cardId, NexusSettings settings)
    {
        var ctx = ContextFor(cardId, settings);
        if (!settings.Devices.DeviceLedOverrides.TryGetValue(ctx.DeviceId, out var list) || list.Count == 0)
            return false;
        foreach (var o in list)
        {
            if (ctx.MapFromSegment(o.Segment, o.LedIndex) >= 0)
                return true;
        }
        return false;
    }

    private RgbDevice? FindRgbDevice(string deviceId)
    {
        if (_bridge is null)
            return null;
        foreach (var d in _bridge.Devices)
        {
            if (d.StableId == deviceId)
                return d;
        }
        return null;
    }
}
