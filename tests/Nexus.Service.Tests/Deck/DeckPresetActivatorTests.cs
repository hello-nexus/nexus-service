using System;
using System.Collections.Generic;
using Nexus.Service.Deck;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Peripherals.StreamDeck;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;
using Xunit;
using static Nexus.Service.Tests.StreamDeck.DeckTestHelpers;

namespace Nexus.Service.Tests.Deck;

/// <summary>Covers DeckPresetActivator.Activate - the single path PUT /deck/instances/{id} and DeckAppPresetSwitcher both use, so they cannot drift.</summary>
public sealed class DeckPresetActivatorTests : IDisposable
{
    private static readonly StreamDeckModel Mini = StreamDeckModels.ByProductId(0x0063)!;

    private readonly InMemoryConfigStore _store = new();
    private readonly MultiplexHub _hub = new();
    private readonly SimulatedStreamDeckSurface _simulated;
    private readonly StreamDeckConnectionWorker _worker;
    private readonly DeckPresetActivator _activator;

    public DeckPresetActivatorTests()
    {
        _simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        var presence = new HardwarePresence(new FixedUsbEnumerator());
        var gate = new DeviceControlGate(_store);
        gate.SetEnabled("streamdeck", true);
        _worker = new StreamDeckConnectionWorker(
            new Nexus.Service.Tests.StreamDeck.FakeWorkerHidEnumerator(), presence, gate, _store,
            new Nexus.Service.Tests.StreamDeck.FakeDeckActionExecutor(), NewTestKeyRenderer(), _hub,
            new Nexus.Service.Tests.StreamDeck.FakeSensorProvider(), _simulated);
        _activator = new DeckPresetActivator(_store, _hub, _worker);
    }

    public void Dispose() => _worker.Dispose();

    [Fact]
    public void Activate_PhysicalInstance_SetsPresetResetsPageAndBroadcastsBothFrames()
    {
        _store.Update(s =>
        {
            s.StreamDeck.Presets.Add(new DeckPreset
            {
                Id = "p1", Name = "One", Cols = 3, Rows = 2,
                Deck = new DeckConfig { Pages = { new DeckPage(), new DeckPage() } },
            });
            s.StreamDeck.Presets.Add(new DeckPreset { Id = "p2", Name = "Two", Cols = 3, Rows = 2 });
            s.StreamDeck.Instances["streamdeck:sim-0001"] = new DeckInstance { Mode = "appAware", ActivePresetId = "p1" };
        });
        _worker.Tick();
        Assert.True(_worker.SetNav("sim-0001", 1, Array.Empty<int>()));
        Assert.Equal(1, _worker.GetCurrentPage("sim-0001"));

        var streamDeckSeen = 0;
        var deckSeen = 0;
        _hub.OnBroadcastForTest += (topic, _) =>
        {
            if (topic == PanelTopics.StreamDeck) streamDeckSeen++;
            if (topic == PanelTopics.Deck) deckSeen++;
        };
        using var subSd = _hub.AddTestSubscription(PanelTopics.StreamDeck);
        using var subDeck = _hub.AddTestSubscription(PanelTopics.Deck);

        var result = _activator.Activate("streamdeck:sim-0001", "p2");

        Assert.Equal("p2", result.ActivePresetId);
        Assert.Equal("p2", _store.Load().StreamDeck.Instances["streamdeck:sim-0001"].ActivePresetId);
        Assert.Equal(0, _worker.GetCurrentPage("sim-0001"));
        Assert.Equal(1, streamDeckSeen);
        Assert.Equal(1, deckSeen);
    }

    [Fact]
    public void Activate_WidgetInstance_CreatesRowAndSkipsWorkerNav()
    {
        _store.Update(s => s.StreamDeck.Presets.Add(new DeckPreset { Id = "p1", Name = "One", Cols = 2, Rows = 2 }));

        var result = _activator.Activate("widget:w1", "p1");

        Assert.Equal("p1", result.ActivePresetId);
        Assert.Equal("fixed", result.Mode);
        Assert.Equal("p1", _store.Load().StreamDeck.Instances["widget:w1"].ActivePresetId);
    }

    [Fact]
    public void Activate_ModeOnly_LeavesActivePresetIdUntouched()
    {
        _store.Update(s => s.StreamDeck.Instances["widget:w1"] = new DeckInstance { Mode = "fixed", ActivePresetId = "p1" });

        var result = _activator.Activate("widget:w1", presetId: null, mode: "appAware");

        Assert.Equal("appAware", result.Mode);
        Assert.Equal("p1", result.ActivePresetId);
    }
}
