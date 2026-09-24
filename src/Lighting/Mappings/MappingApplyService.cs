using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Devices;
using Nexus.Service.Models.Devices;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;

namespace Nexus.Service.Lighting.Mappings;

/// <summary>
/// Orchestrates the local mapping lifecycle shared by the HTTP routes and
/// the first-seen auto-apply worker: validate, persist (artifact embedded),
/// run count side effects, refresh the live engine frame, broadcast, and
/// fire the best-effort adoption signals.
/// </summary>
public sealed class MappingApplyService
{
    private readonly IConfigStore _store;
    private readonly MappingCloudClient _cloud;
    private readonly ILightingDeviceProvider _devices;
    private readonly Nexus.Service.Lighting.Zones.ZoneTopology _topology;
    private readonly MultiplexHub _hub;

    public MappingApplyService(
        IConfigStore store,
        MappingCloudClient cloud,
        ILightingDeviceProvider devices,
        Nexus.Service.Lighting.Zones.ZoneTopology topology,
        MultiplexHub hub)
    {
        _store = store;
        _cloud = cloud;
        _devices = devices;
        _topology = topology;
        _hub = hub;
    }

    public LightingDevice? FindCard(string id)
    {
        foreach (var card in _devices.GetAll().Devices)
        {
            if (card.Id == id)
                return card;
        }
        return null;
    }

    /// <summary>
    /// Resolve the current layout for any lighting device id through the
    /// zone topology (partition-backed cards via their zone slices, legacy
    /// cards via the bridge / engine frame fallbacks). Null when the id is
    /// unknown.
    /// </summary>
    public ResolvedLedLayout? Resolve(string id, NexusSettings? settings = null)
        => _topology.ResolveCard(id, settings ?? _store.Load())?.Layout;

    /// <summary>Re-resolve and push the layout into the live engine frame so running effects pick the change up on the next tick.</summary>
    public void RefreshDevice(string id) => _topology.RefreshCardFrame(id);

    /// <summary>Mapping sources. Only <see cref="SourceCommunity"/> talks to the registry.</summary>
    public const string SourceCommunity = "community";
    public const string SourceFile = "file";
    public const string SourceBuiltIn = "builtin";

    public enum ApplyOutcome { Applied, NotFound, Invalid }

    /// <summary>Apply an artifact to a device. Validates first (registry, link, and file payloads all pass through here), persists with the artifact embedded, applies count side effects, refreshes, broadcasts, and adopts.</summary>
    public ApplyOutcome Apply(string id, MappingArtifact artifact, string? mappingId, string source, bool auto, string? contentHashFromRegistry = null)
    {
        var card = FindCard(id);
        if (card is null)
            return ApplyOutcome.NotFound;

        var lint = MappingLint.Validate(artifact, zoneIsLinear: _ => card.ZoneType is null or "linear" or "single");
        if (!lint.Ok)
        {
            Console.Error.WriteLine($"[mappings] rejected artifact for {id}: {string.Join("; ", lint.Errors)}");
            return ApplyOutcome.Invalid;
        }
        // Local guard rail independent of the registry's eligibility flag: a
        // mapping the lint deems unfit as a community default (e.g. mostly
        // disabled) is never applied silently, only by explicit user choice.
        if (auto && lint.AutoApplyIneligible)
        {
            Console.Error.WriteLine($"[mappings] auto-apply refused for {id}: lint flags artifact auto-apply-ineligible");
            return ApplyOutcome.Invalid;
        }

        var contentHash = contentHashFromRegistry ?? MappingHash.ContentHash(artifact);
        string? replacedMappingId = null;
        _store.Update(s =>
        {
            // Only a registry mapping can be revoked in the registry. A
            // built-in carries a product key in the same field, and sending
            // that upstream would be a signal about a row that does not exist.
            if (s.Devices.AppliedMappings.TryGetValue(id, out var previous)
                && previous.Source == SourceCommunity
                && previous.MappingId is { } prevId && prevId != mappingId)
            {
                replacedMappingId = prevId;
            }
            s.Devices.AppliedMappings[id] = new AppliedMappingRef
            {
                MappingId = mappingId,
                Source = source,
                ContentHash = contentHash,
                Name = artifact.Name,
                Artifact = artifact,
                AppliedAt = DateTimeOffset.UtcNow,
                AutoApplied = auto,
            };
            // A manual apply is a fresh decision; it clears the auto-apply veto.
            if (!auto)
                s.Devices.MappingAutoApplyDeclined.Remove(id);
        });

        var zone = LedLayoutResolver.SelectZone(artifact, card.ZoneIndex ?? -1);
        if (zone?.LedCount is { } targetCount && card.ZoneResizable && targetCount != card.LedCount)
            _devices.SetZoneLedCount(id, targetCount);

        RefreshDevice(id);
        PanelTopics.BroadcastLighting(_hub);

        if (replacedMappingId is not null)
            FireAndForget(_cloud.RevokeAsync(replacedMappingId, "switched", CancellationToken.None));
        // Adoption is a registry ranking signal. Built-in and file mappings are
        // not registry rows and must stay entirely offline.
        if (source == SourceCommunity && mappingId is not null && card.DeviceKey.Length > 0)
            FireAndForget(_cloud.AdoptAsync(mappingId, card.DeviceKey, auto, CancellationToken.None));
        return ApplyOutcome.Applied;
    }

    /// <summary>Remove the applied mapping. reason: "undo" (auto-apply veto + strongest negative registry signal) | "switched" | "reset".</summary>
    public bool Revert(string id, string reason)
    {
        string? mappingId = null;
        var existed = false;
        _store.Update(s =>
        {
            if (s.Devices.AppliedMappings.TryGetValue(id, out var applied))
            {
                existed = true;
                // Same rule as Apply: never signal the registry about a key it
                // never issued.
                mappingId = applied.Source == SourceCommunity ? applied.MappingId : null;
                s.Devices.AppliedMappings.Remove(id);
            }
            if (reason == "undo" && !s.Devices.MappingAutoApplyDeclined.Contains(id))
                s.Devices.MappingAutoApplyDeclined.Add(id);
        });
        if (!existed)
            return false;

        RefreshDevice(id);
        PanelTopics.BroadcastLighting(_hub);
        if (mappingId is not null)
            FireAndForget(_cloud.RevokeAsync(mappingId, reason, CancellationToken.None));
        return true;
    }

    /// <summary>Snapshot the device's current resolved layout as a portable artifact.</summary>
    public MappingArtifact? Export(string id, string? name = null, string? description = null)
    {
        var card = FindCard(id);
        if (card is null)
            return null;
        var resolved = Resolve(id);
        if (resolved is null || resolved.LedCount <= 0)
            return null;
        return MappingArtifactFactory.FromResolved(card, resolved, name ?? card.Name, description);
    }

    private static void FireAndForget(Task task)
        => task.ContinueWith(
            t => Console.Error.WriteLine($"[mappings] signal task failed: {t.Exception?.GetBaseException().Message}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
}
