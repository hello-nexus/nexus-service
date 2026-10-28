using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
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
    private readonly IDisplayOrientationProvider? _orientation;
    // First-promotion default orientations that could not be applied yet (on
    // Windows the rotate goes through the user-session helper, which may not
    // be connected at boot). displayId -> orientation, replayed on helper
    // connect. In memory only: the model is already marked seen, so a restart
    // before the replay falls back to the manual orientation picker.
    private readonly ConcurrentDictionary<string, string> _pendingOrientation = new(StringComparer.Ordinal);
    // The helper's read loop starts just after Connected fires, so the first
    // replay attempt can race it; keep the attempt count small.
    private const int ReplayAttempts = 2;
    private const int ReplayGapMs = 400;

    public PanelAutoPromotion(
        PanelDeviceRegistry registry,
        IConfigStore store,
        IDisplayOrientationProvider? orientation = null
#if WINDOWS
        , Nexus.Service.Helper.HelperRegistry? helpers = null
#endif
        )
    {
        _registry = registry;
        _store = store;
        _orientation = orientation;
#if WINDOWS
        helpers?.Connected += OnHelperConnected;
#endif
    }

#if WINDOWS
    private void OnHelperConnected(Nexus.Service.Helper.HelperConnection connection)
    {
        if (_pendingOrientation.IsEmpty) return;
        _ = Task.Run(ReplayPendingAsync);
    }
#endif

    internal async Task ReplayPendingAsync()
    {
        for (var attempt = 0; attempt < ReplayAttempts; attempt++)
        {
            foreach (var (displayId, orientation) in _pendingOrientation.ToArray())
            {
                if (TryApplyOrientation(displayId, orientation)) _pendingOrientation.TryRemove(displayId, out _);
            }
            if (_pendingOrientation.IsEmpty) return;
            try { await Task.Delay(ReplayGapMs).ConfigureAwait(false); }
            catch { return; }
        }
        ServiceLog.Warn($"[panel-auto-promote] orientation replay gave up after {ReplayAttempts} attempts");
    }

    private bool TryApplyOrientation(string displayId, string orientation)
    {
        if (_orientation is null) return false;
        var record = _registry.FindByDisplayId(displayId);
        var (ok, error) = _orientation.SetDisplayOrientation(displayId, orientation, PanelDeviceRegistry.ResolveCoverBackgroundHex(record));
        ServiceLog.Info($"[panel-auto-promote] default orientation {orientation} displayId={displayId} ok={ok} detail='{error}'");
        // Same persistence as POST /displays/{id}/rotation.
        if (ok) _registry.UpdateDisplayOrientation(displayId, orientation);
        return ok;
    }

    /// <summary>Applies a first-promotion default orientation now, or queues it for the helper-connect replay.</summary>
    private void ApplyDefaultOrientation(string displayId, string orientation)
    {
        if (!TryApplyOrientation(displayId, orientation)) _pendingOrientation[displayId] = orientation;
    }

    /// <summary>
    /// Evaluate the current topology and promote unseen curated displays.
    /// Returns the record ids of panels created by this call (for hub
    /// broadcasts); empty when nothing changed.
    /// </summary>
    public IReadOnlyList<string> Reconcile(DisplayTopologyResponse topology)
    {
        List<(string DisplayId, string Orientation)>? rotations = null;
        IReadOnlyList<string> result;
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
                var known = KnownPanelDisplays.Match(
                    display.Manufacturer, display.Model, display.Name, display.Resolution?.Width, display.Resolution?.Height);
                if (known is null) continue;
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
                    display.ScaleFactor, display.IsTouch, display.Orientation, display.Dpi);
                var (record, _) = _registry.AllocateForDisplay(display.Id, display.Name, capabilities, display.Fingerprint);
                (markSeen ??= new List<string>()).Add(identity);
                (promoted ??= new List<string>()).Add(record.Id);
                // Landscape is detected from the pixel shape: width > height.
                if (known.DefaultOrientation is { } target
                    && display.Resolution is { } size && size.Width > size.Height)
                {
                    (rotations ??= new List<(string, string)>()).Add((display.Id, target));
                }
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
            result = (IReadOnlyList<string>?)promoted ?? Array.Empty<string>();
        }
        // Outside the lock: the rotate is a blocking helper round-trip.
        if (rotations is not null)
        {
            foreach (var (displayId, orientation) in rotations) ApplyDefaultOrientation(displayId, orientation);
        }
        return result;
    }

    private static string ModelIdentity(DisplayTopologyEntryDto display)
        => $"{display.Manufacturer}:{display.Model}".ToUpperInvariant();
}
