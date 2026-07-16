#if WINDOWS
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Helper;
using Nexus.Service.Helper.Domains;
using Nexus.Service.Panel;
using Nexus.Service.Sockets;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Bridges the helper's WM_DISPLAYCHANGE push (<c>displays.changed</c>
/// envelopes) to the <c>displays</c> multiplex topic. A short quiet-period
/// timer coalesces the burst Windows fires during a single arrangement
/// change. Helper connect/disconnect also rebroadcasts: topology flips
/// between known and unknown at those edges.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DisplayTopologyWatcher : BackgroundService
{
    private const int DebounceMs = 500;

    private readonly HelperRegistry _helpers;
    private readonly MultiplexHub _hub;
    private readonly DisplayTopologyService _topology;
    private readonly PanelAutoPromotion _autoPromotion;
    private readonly Timer _debounce;

    public DisplayTopologyWatcher(HelperRegistry helpers, MultiplexHub hub, DisplayTopologyService topology, PanelAutoPromotion autoPromotion)
    {
        _helpers = helpers;
        _hub = hub;
        _topology = topology;
        _autoPromotion = autoPromotion;
        _debounce = new Timer(_ => Broadcast(), null, Timeout.Infinite, Timeout.Infinite);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _helpers.InboundEnvelope += OnEnvelope;
        _helpers.Connected += OnHelperEdge;
        _helpers.Disconnected += OnHelperEdge;
        _topology.PromotedPanelCapabilitiesChanged += OnPromotedCapabilitiesChanged;
        stoppingToken.Register(() =>
        {
            _helpers.InboundEnvelope -= OnEnvelope;
            _helpers.Connected -= OnHelperEdge;
            _helpers.Disconnected -= OnHelperEdge;
            _topology.PromotedPanelCapabilitiesChanged -= OnPromotedCapabilitiesChanged;
            _debounce.Dispose();
        });
        return Task.CompletedTask;
    }

    /// <summary>Editors key their grid on the record's capabilities, so a
    /// refresh (rotation, scaling change) must push panel/device.</summary>
    private void OnPromotedCapabilitiesChanged(IReadOnlyList<string> recordIds)
    {
        try
        {
            foreach (var id in recordIds) PanelTopics.BroadcastPanelDevice(_hub, id);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[displays] panel capability broadcast failed: {ex.Message}");
        }
    }

    private void OnEnvelope(HelperConnection _, HelperEnvelope env)
    {
        if (env.Type == DisplayTopologyCommands.ChangedType) Kick();
    }

    private void OnHelperEdge(HelperConnection _) => Kick();

    private void Kick()
    {
        try { _debounce.Change(DebounceMs, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    private void Broadcast()
    {
        try
        {
            // Re-enumerate before broadcasting: GetTopology runs the
            // promoted-record capability sync, so display-bound records
            // track rotation/rescale even when no client refetches topology.
            var topology = _topology.GetTopology();
            // Curated always-a-panel displays (Xeneon Edge) promote here so a
            // fresh plug-in or boot hosts its kiosk with no manual step; the
            // debounced topology edge is the exact moment a new display can
            // appear. Broadcast created records so open dashboards update.
            foreach (var recordId in _autoPromotion.Reconcile(topology))
            {
                PanelTopics.BroadcastPanelDevice(_hub, recordId);
            }
            PanelTopics.BroadcastDisplays(_hub);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[displays] topology broadcast failed: {ex.Message}");
        }
    }
}
#endif
