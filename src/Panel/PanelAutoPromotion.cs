using System;
using System.Collections.Generic;
using Nexus.Service.Models.Displays;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Platform.Displays;

namespace Nexus.Service.Panel;

/// <summary>
/// Promotes curated always-a-panel displays (KnownPanelDisplays, e.g. the
/// Corsair Xeneon Edge) to Nexus panels automatically, so they host a kiosk
/// out of the box the same way the Y70 does - no manual "Use as Nexus panel"
/// step. Runs the same allocation as the manual promote route, so the record,
/// grid, touch, and per-device settings behave identically.
///
/// Auto-promotion acts at most once per display MODEL per machine, tracked in
/// <see cref="NexusSettings.AutoPromotedPanelModels"/> by EDID identity
/// ("CRX:ED00"), not by the OS displayId - the displayId embeds the PnP
/// connection instance, so a port change would mint a fresh id and re-promote
/// a panel the user deleted. Consequence, accepted: a second unit of the same
/// model plugged in later is not auto-promoted (the same-pass case is; the
/// manual promote flow covers the rest).
/// </summary>
public sealed class PanelAutoPromotion
{
    private readonly PanelDeviceRegistry _registry;
    private readonly IConfigStore _store;
    // Serializes Reconcile: the topology watcher's debounce timer can fire a
    // new callback while a prior one is still inside the helper round-trip,
    // and the seen-set read below must not race its own Update.
    private readonly object _reconcileLock = new();

    public PanelAutoPromotion(PanelDeviceRegistry registry, IConfigStore store)
    {
        _registry = registry;
        _store = store;
    }

    /// <summary>
    /// Evaluate the current topology and promote unseen curated displays.
    /// Returns the record ids of panels created by this call (for hub
    /// broadcasts); empty when nothing changed.
    /// </summary>
    public IReadOnlyList<string> Reconcile(DisplayTopologyResponse topology)
    {
        lock (_reconcileLock)
        {
            List<string>? promoted = null;
            List<string>? markSeen = null;
            // Snapshot so two same-model displays in one topology both
            // promote; the seen mark lands once, after the pass.
            var seen = new HashSet<string>(_store.Load().AutoPromotedPanelModels, StringComparer.OrdinalIgnoreCase);
            foreach (var display in topology.Displays)
            {
                if (string.IsNullOrEmpty(display.Id)) continue;
                if (display.IsY70 || !display.HostingSupported) continue;
                if (KnownPanelDisplays.Match(display.Manufacturer, display.Model, display.Name) is null) continue;
                var identity = ModelIdentity(display);
                if (seen.Contains(identity)) continue;

                // A record for this display (enabled or disabled, auto or
                // manual) means the user already manages it: mark seen, never
                // touch it. AssignedPanelDeviceId only reflects ENABLED
                // records, so check the registry directly.
                if (display.AssignedPanelDeviceId is not null || _registry.FindByDisplayId(display.Id) is not null)
                {
                    (markSeen ??= new List<string>()).Add(identity);
                    continue;
                }

                var capabilities = DisplayTopologyService.BuildPromotedCapabilities(
                    display.Manufacturer, display.Model, display.Name,
                    display.Resolution?.Width ?? 0, display.Resolution?.Height ?? 0,
                    display.ScaleFactor, display.IsTouch, display.Orientation);
                var (record, _) = _registry.AllocateForDisplay(display.Id, display.Name, capabilities);
                (markSeen ??= new List<string>()).Add(identity);
                (promoted ??= new List<string>()).Add(record.Id);
                ServiceLog.Info($"[panel-auto-promote] '{display.Name}' ({identity}) promoted to panel {record.Id}");
            }

            if (markSeen is not null)
            {
                _store.Update(s =>
                {
                    foreach (var identity in markSeen)
                    {
                        if (!s.AutoPromotedPanelModels.Contains(identity)) s.AutoPromotedPanelModels.Add(identity);
                    }
                });
            }
            return (IReadOnlyList<string>?)promoted ?? Array.Empty<string>();
        }
    }

    private static string ModelIdentity(DisplayTopologyEntryDto display)
        => $"{display.Manufacturer}:{display.Model}".ToUpperInvariant();
}
