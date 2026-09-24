using System;
using Nexus.Service.Models.Deck;
using Nexus.Service.Models.Peripherals.StreamDeck;
using Nexus.Service.Peripherals.StreamDeck;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;

namespace Nexus.Service.Deck;

/// <summary>
/// Applies a preset (and optionally a mode) to a deck instance: upserts the
/// instance row, resets physical nav to page 0, and broadcasts the same
/// frames PUT /deck/instances/{id} always has - a physical instance's
/// streamdeck "config" frame plus the host-wide deck "active" frame. Shared
/// by that route and DeckAppPresetSwitcher so activation never drifts
/// between a manual pick and an automatic one.
/// </summary>
public sealed class DeckPresetActivator
{
    private readonly IConfigStore _store;
    private readonly MultiplexHub _hub;
    private readonly StreamDeckConnectionWorker _worker;

    public DeckPresetActivator(IConfigStore store, MultiplexHub hub, StreamDeckConnectionWorker worker)
    {
        _store = store;
        _hub = hub;
        _worker = worker;
    }

    /// <summary>presetId null leaves the instance's active preset untouched (a bare mode change); mode null leaves the mode untouched.</summary>
    public DeckInstance Activate(string instanceId, string? presetId, string? mode = null)
    {
        DeckInstance? result = null;
        _store.Update(s =>
        {
            if (!s.StreamDeck.Instances.TryGetValue(instanceId, out var instance))
            {
                instance = new DeckInstance();
                s.StreamDeck.Instances[instanceId] = instance;
            }
            if (mode is not null)
            {
                instance.Mode = mode;
            }
            if (presetId is not null)
            {
                instance.ActivePresetId = presetId;
            }
            result = instance;
        });

        if (instanceId.StartsWith("streamdeck:", StringComparison.Ordinal))
        {
            var serial = instanceId["streamdeck:".Length..];
            _worker.SetNav(serial, 0, Array.Empty<int>());
            PanelTopics.BroadcastStreamDeck(_hub, new StreamDeckChangedFrame { Kind = "config", Serial = serial });
        }

        PanelTopics.BroadcastDeck(_hub, new DeckChangedFrame { Kind = "active", InstanceId = instanceId, Instance = result! });
        return result!;
    }
}
