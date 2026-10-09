using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Deck;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Peripherals.StreamDeck;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;
using Xunit;
using static Nexus.Service.Tests.StreamDeck.DeckTestHelpers;

namespace Nexus.Service.Tests.StreamDeck;

/// <summary>Records dial reads and writes; a write updates the reading so the next read sees it, like the optimistic real service.</summary>
internal sealed class FakeDialValues : IDeckDialValues
{
    public readonly Dictionary<string, DialReading> Readings = new();
    public readonly List<(string Key, double Percent)> Writes = new();
    public readonly List<(string Key, bool Muted)> Mutes = new();

    public static string KeyOf(DeckDialAction action) => action.Type + ":" + (action.DeviceId ?? action.AppId ?? action.DisplayId ?? "");

    public void Set(string key, double percent, bool muted = false) => Readings[key] = new DialReading(true, false, percent, muted);

    public DialReading Read(DeckDialAction action) =>
        Readings.TryGetValue(KeyOf(action), out var reading) ? reading : DialReading.Unsupported;

    public void Write(DeckDialAction action, double percent)
    {
        var key = KeyOf(action);
        Writes.Add((key, percent));
        Readings[key] = Readings.TryGetValue(key, out var r) ? r with { Percent = percent } : new DialReading(true, false, percent, false);
    }

    public void SetMuted(DeckDialAction action, bool muted)
    {
        var key = KeyOf(action);
        Mutes.Add((key, muted));
        Readings[key] = Readings.TryGetValue(key, out var r) ? r with { Muted = muted } : new DialReading(true, false, 0, muted);
    }
}

/// <summary>
/// Dial, touch strip, info screen, touch key and ring behaviour of
/// StreamDeckConnectionWorker against simulated expanded decks. These verify
/// dispatch and what is pushed to the surface, not what the glass shows.
/// </summary>
public sealed class StreamDeckDialTests : IDisposable
{
    private const string Serial = "sim-0001";

    private static readonly StreamDeckModel Plus = StreamDeckModels.ByProductId(0x0084)!;
    private static readonly StreamDeckModel Neo = StreamDeckModels.ByProductId(0x009a)!;
    private static readonly StreamDeckModel Studio = StreamDeckModels.ByProductId(0x00aa)!;
    private static readonly StreamDeckModel Galleon = StreamDeckModels.ByProductId(0x2b18)!;

    private readonly InMemoryConfigStore _store = new();
    private readonly FakeDeckActionExecutor _executor = new();
    private readonly FakeSensorProvider _sensors = new();
    private readonly FakeDialValues _values = new();
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 10, 8, 9, 30, 0, TimeSpan.Zero));
    private readonly MultiplexHub _hub = new();
    private StreamDeckConnectionWorker? _worker;
    private SimulatedStreamDeckSurface? _sim;

    public void Dispose() => _worker?.Dispose();

    private StreamDeckConnectionWorker Build(StreamDeckModel model, string configJson)
    {
        _sim = new SimulatedStreamDeckSurface(model, Serial);
        var gate = new DeviceControlGate(_store);
        gate.SetEnabled("streamdeck", true);
        _worker = new StreamDeckConnectionWorker(
            new FakeWorkerHidEnumerator(), new HardwarePresence(new FixedUsbEnumerator()), gate, _store, _executor,
            NewTestKeyRenderer(), _hub, _sensors, _sim, _clock, dialValues: _values);
        var config = JsonSerializer.Deserialize(configJson, AppJsonContext.Default.DeckConfig)!;
        _store.Update(s => s.StreamDeck.Decks[Serial] = new PhysicalDeckSettings { LegacyDeck = config });
        _store.Update(s => ActivateLegacyDeck(s, Serial, model.Columns, model.Rows));
        _worker.Tick();
        return _worker;
    }

    private static string Dials(params string[] dials) =>
        "{\"pages\":[{\"slots\":[],\"dials\":[" + string.Join(",", dials) + "]}]}";

    private static string Volume(string device, int? step = null) =>
        "{\"action\":{\"type\":\"volume\",\"deviceId\":\"" + device + "\"" + (step is null ? "" : ",\"step\":" + step) + "}}";

    private void Drain() => _worker!.DrainSimulatedInput();

    private void Press(int dial)
    {
        _sim!.PokeDialPress(dial, true);
        Drain();
        _sim.PokeDialPress(dial, false);
        Drain();
    }

    private (int X, int Y, int W, int H) LastRegion()
    {
        var r = _sim!.ScreenRegions.Last();
        return (r.X, r.Y, r.Width, r.Height);
    }

    private void WaitForCalls(int count)
    {
        SpinWait.SpinUntil(() => _executor.Calls.Count >= count, TimeSpan.FromSeconds(3));
        Assert.True(_executor.Calls.Count >= count, $"expected {count} executor calls, saw {_executor.Calls.Count}");
    }

    // ── Connect and push ──

    [Fact]
    public void Connect_PushesTheWholeStripAsOneRegionWrite()
    {
        Build(Plus, Dials(Volume("out")));

        Assert.Single(_sim!.ScreenRegions);
        Assert.Equal((0, 0, 800, 100), LastRegion());
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, _sim.ScreenRegions[0].Bytes[..2]);
    }

    [Fact]
    public void Tick_DoesNotRepushAnUnchangedSegment_ButRepaintsAChangedOne()
    {
        Build(Plus, Dials(Volume("out")));
        _values.Set("volume:out", 30);
        _worker!.Tick();
        var afterFirst = _sim!.ScreenRegions.Count;

        _worker.Tick();
        Assert.Equal(afterFirst, _sim.ScreenRegions.Count);

        _values.Set("volume:out", 31);
        _worker.Tick();
        Assert.Equal(afterFirst + 1, _sim.ScreenRegions.Count);
        Assert.Equal((0, 0, 200, 100), LastRegion());
    }

    [Fact]
    public void ButtonKeys_StillDispatchOnAnExpandedDeck()
    {
        Build(Plus, "{\"pages\":[{\"slots\":[{\"action\":{\"type\":\"openUrl\",\"url\":\"https://example.com\"}}]}]}");

        _sim!.Poke(0, true);
        Drain();

        WaitForCalls(1);
        Assert.Equal("openUrl", _executor.Calls.First().Action!.Type);
    }

    // ── Turn ──

    [Fact]
    public void Turn_MovesTheValueByTheStep_AndRepaintsOnlyThatSegment()
    {
        Build(Plus, Dials("{}", Volume("out")));
        _values.Set("volume:out", 50);
        var before = _sim!.ScreenRegions.Count;

        _sim.PokeRotate(1, 1);
        Drain();

        Assert.Equal(new[] { ("volume:out", 52d) }, _values.Writes);
        Assert.Equal(before + 1, _sim.ScreenRegions.Count);
        Assert.Equal((200, 0, 200, 100), LastRegion());
    }

    [Fact]
    public void Turn_UsesTheConfiguredStep_AndSumsAccumulatedTicks()
    {
        Build(Plus, Dials(Volume("out", step: 3)));
        _values.Set("volume:out", 50);

        _sim!.PokeRotate(0, -4);
        Drain();

        Assert.Equal(("volume:out", 38d), _values.Writes.Single());
    }

    [Fact]
    public void Turn_ClampsToTheRange()
    {
        Build(Plus, Dials(Volume("out")));
        _values.Set("volume:out", 99);

        _sim!.PokeRotate(0, 10);
        Drain();

        Assert.Equal(100d, _values.Writes.Single().Percent);
    }

    [Fact]
    public void Turn_OnAnUnsupportedTarget_WritesNothing()
    {
        Build(Plus, Dials(Volume("missing")));

        _sim!.PokeRotate(0, 1);
        Drain();

        Assert.Empty(_values.Writes);
    }

    [Fact]
    public void TurnWhileHeld_MovesFiveTimesTheStep_AndSuppressesThePress()
    {
        Build(Plus, Dials(Volume("out")));
        _values.Set("volume:out", 50);

        _sim!.PokeDialPress(0, true);
        Drain();
        _sim.PokeRotate(0, 1);
        Drain();
        _sim.PokeDialPress(0, false);
        Drain();

        Assert.Equal(("volume:out", 60d), _values.Writes.Single());
        Assert.Empty(_values.Mutes);
    }

    [Fact]
    public void PressWithoutTurn_FiresOnRelease_NotOnDown()
    {
        Build(Plus, Dials(Volume("out")));
        _values.Set("volume:out", 50);

        _sim!.PokeDialPress(0, true);
        Drain();
        Assert.Empty(_values.Mutes);

        _sim.PokeDialPress(0, false);
        Drain();
        Assert.Equal(("volume:out", true), _values.Mutes.Single());
    }

    [Fact]
    public void Press_AfterAHeldTurn_FiresAgainNextTime()
    {
        Build(Plus, Dials(Volume("out")));
        _values.Set("volume:out", 50);
        _sim!.PokeDialPress(0, true);
        _sim.PokeRotate(0, 1);
        _sim.PokeDialPress(0, false);
        Drain();

        Press(0);

        Assert.Single(_values.Mutes);
    }

    [Fact]
    public void BrightnessPress_TogglesBetweenZeroAndTheLastNonZeroValue()
    {
        Build(Plus, Dials("{\"action\":{\"type\":\"lightingBrightness\"}}"));
        _values.Set("lightingBrightness:", 40);

        Press(0);
        Press(0);

        Assert.Equal(new[] { ("lightingBrightness:", 0d), ("lightingBrightness:", 40d) }, _values.Writes);
    }

    [Fact]
    public void DeckBrightnessDial_TurnsThePersistedBrightness_WithAFloor_AndNoPress()
    {
        Build(Plus, Dials("{\"action\":{\"type\":\"deckBrightness\"}}"));
        _store.Update(s => s.StreamDeck.Decks[Serial].Brightness = 60);

        _sim!.PokeRotate(0, -1);
        Drain();
        Assert.Equal(58, _store.Load().StreamDeck.Decks[Serial].Brightness);
        Assert.Equal(58, _sim.Brightness);

        _sim.PokeRotate(0, -100);
        Drain();
        Assert.Equal(5, _store.Load().StreamDeck.Decks[Serial].Brightness);

        var before = _store.Load().StreamDeck.Decks[Serial].Brightness;
        Press(0);
        Assert.Equal(before, _store.Load().StreamDeck.Decks[Serial].Brightness);
    }

    [Fact]
    public void PageDial_TurnPagesInTheTickDirection_PressGoesToPageOne()
    {
        const string pageDials = "\"dials\":[{\"action\":{\"type\":\"page\"}}]";
        Build(Plus, "{\"pages\":[{\"slots\":[]," + pageDials + "},{\"slots\":[]," + pageDials + "},{\"slots\":[]," + pageDials + "}]}");

        _sim!.PokeRotate(0, 5);
        Drain();
        Assert.Equal(1, _worker!.GetCurrentPage(Serial));

        _sim.PokeRotate(0, -1);
        Drain();
        Assert.Equal(0, _worker.GetCurrentPage(Serial));

        _sim.PokeRotate(0, 1);
        _sim.PokeRotate(0, 1);
        Drain();
        Assert.Equal(2, _worker.GetCurrentPage(Serial));
    }

    // ── Custom ──

    private const string CustomDial =
        "{\"action\":{\"type\":\"custom\"," +
        "\"turnRight\":{\"type\":\"openUrl\",\"url\":\"https://r\"}," +
        "\"turnLeft\":{\"type\":\"openUrl\",\"url\":\"https://l\"}," +
        "\"push\":{\"type\":\"openUrl\",\"url\":\"https://p\"}," +
        "\"touch\":{\"type\":\"openUrl\",\"url\":\"https://t\"}}}";

    [Fact]
    public void CustomDial_RunsTurnRightOncePerTick_AndTurnLeftForNegative()
    {
        Build(Plus, Dials(CustomDial));

        _sim!.PokeRotate(0, 3);
        Drain();
        WaitForCalls(3);
        _sim.PokeRotate(0, -2);
        Drain();

        WaitForCalls(5);
        var urls = _executor.Calls.Select(c => c.Action!.Url).OrderBy(u => u).ToArray();
        Assert.Equal(new[] { "https://l", "https://l", "https://r", "https://r", "https://r" }, urls);
    }

    [Fact]
    public void CustomDial_PushRunsOnRelease_TapRunsTouch()
    {
        Build(Plus, Dials(CustomDial));

        Press(0);
        WaitForCalls(1);
        Assert.Equal("https://p", _executor.Calls.Single().Action!.Url);

        _sim!.PokeTouch(StreamDeckTouchKind.Tap, 100, 50);
        Drain();
        WaitForCalls(2);
        Assert.Equal("https://t", _executor.Calls.Last().Action!.Url);
    }

    [Fact]
    public void CustomDial_TapFallsBackToPush_WhenNoTouchAction()
    {
        Build(Plus, Dials("{\"action\":{\"type\":\"custom\",\"push\":{\"type\":\"openUrl\",\"url\":\"https://p\"}}}"));

        _sim!.PokeTouch(StreamDeckTouchKind.Tap, 100, 50);
        Drain();

        WaitForCalls(1);
        Assert.Equal("https://p", _executor.Calls.Single().Action!.Url);
    }

    [Fact]
    public void MonitoringDial_PressRunsItsPressAction_TurnDoesNothing()
    {
        Build(Plus, Dials("{\"action\":{\"type\":\"monitoring\",\"category\":\"cpu\",\"sensor\":\"cpu/core0\",\"press\":\"taskManager\"}}"));

        _sim!.PokeRotate(0, 2);
        Drain();
        Assert.Empty(_executor.Calls);

        Press(0);
        WaitForCalls(1);
        var call = _executor.Calls.Single().Action!;
        Assert.Equal("monitoring", call.Type);
        Assert.Equal("taskManager", call.Press);
    }

    // ── Stack ──

    private const string StackedDial =
        "{\"label\":\"ignored\",\"action\":{\"type\":\"volume\",\"deviceId\":\"own\"},\"stack\":[" +
        "{\"action\":{\"type\":\"volume\",\"deviceId\":\"a\"}},{\"action\":{\"type\":\"volume\",\"deviceId\":\"b\"}},{\"action\":{\"type\":\"volume\",\"deviceId\":\"c\"}}]}";

    [Fact]
    public void Stack_PressAdvancesAndWraps_TurnAndTapActOnTheShownEntry()
    {
        Build(Plus, Dials(StackedDial));
        _values.Set("volume:a", 10);
        _values.Set("volume:b", 20);
        _values.Set("volume:c", 30);

        _sim!.PokeRotate(0, 1);
        Drain();
        Assert.Equal("volume:a", _values.Writes.Last().Key);

        Press(0);
        Assert.Equal(1, _worker!.DialStackIndexForTests(Serial, 0));
        _sim.PokeRotate(0, 1);
        Drain();
        Assert.Equal(("volume:b", 22d), _values.Writes.Last());

        _sim.PokeTouch(StreamDeckTouchKind.Tap, 100, 50);
        Drain();
        Assert.Equal("volume:b", _values.Mutes.Single().Key);
        Assert.Equal(1, _worker.DialStackIndexForTests(Serial, 0));

        Press(0);
        Press(0);
        Assert.Equal(0, _worker.DialStackIndexForTests(Serial, 0));
        Assert.DoesNotContain(_values.Writes, w => w.Key == "volume:own");
    }

    [Fact]
    public void Stack_PositionIsDroppedWhenTheDeckDisconnects()
    {
        Build(Plus, Dials(StackedDial));
        Press(0);
        Assert.Equal(1, _worker!.DialStackIndexForTests(Serial, 0));

        _worker.ClearSimulatedModel();

        Assert.Equal(0, _worker.DialStackIndexForTests(Serial, 0));
    }

    [Fact]
    public void ZeroToggle_RestoresPerStackEntry()
    {
        Build(Plus, Dials("{\"stack\":[{\"action\":{\"type\":\"lightingBrightness\"}},{\"action\":{\"type\":\"displayBrightness\",\"displayId\":\"d1\"}}]}"));
        _values.Set("lightingBrightness:", 40);
        _values.Set("displayBrightness:d1", 70);

        _sim!.PokeTouch(StreamDeckTouchKind.Tap, 100, 50);
        Drain();
        Press(0);
        _sim.PokeTouch(StreamDeckTouchKind.Tap, 100, 50);
        Drain();
        _sim.PokeTouch(StreamDeckTouchKind.Tap, 100, 50);
        Drain();
        Press(0);
        _sim.PokeTouch(StreamDeckTouchKind.Tap, 100, 50);
        Drain();

        Assert.Equal(new[] { ("lightingBrightness:", 0d), ("displayBrightness:d1", 0d), ("displayBrightness:d1", 70d), ("lightingBrightness:", 40d) },
            _values.Writes.ToArray());
    }

    private const string PushAndTurnDial =
        "{\"action\":{\"type\":\"custom\",\"turnRight\":{\"type\":\"text\",\"text\":\"r\"},\"turnLeft\":{\"type\":\"text\",\"text\":\"l\"},\"push\":{\"type\":\"text\",\"text\":\"p\"}}}";

    private GateExecutor BuildGated()
    {
        var executor = new GateExecutor();
        _sim = new SimulatedStreamDeckSurface(Plus, Serial);
        var gateDev = new DeviceControlGate(_store);
        gateDev.SetEnabled("streamdeck", true);
        _worker = new StreamDeckConnectionWorker(
            new FakeWorkerHidEnumerator(), new HardwarePresence(new FixedUsbEnumerator()), gateDev, _store, executor,
            NewTestKeyRenderer(), _hub, _sensors, _sim, _clock, dialValues: _values);
        var config = JsonSerializer.Deserialize(Dials(PushAndTurnDial), AppJsonContext.Default.DeckConfig)!;
        _store.Update(s => s.StreamDeck.Decks[Serial] = new PhysicalDeckSettings { LegacyDeck = config });
        _store.Update(s => ActivateLegacyDeck(s, Serial, Plus.Columns, Plus.Rows));
        _worker.Tick();
        return executor;
    }

    [Fact]
    public async Task SlowTurnAction_ReverseTurnNetsOutWaitingTicks_AndPushIsNeverDropped()
    {
        var executor = BuildGated();
        _sim!.PokeRotate(0, 1);
        Drain();
        Assert.True(SpinWait.SpinUntil(() => executor.Started >= 1, TimeSpan.FromSeconds(3)));

        _sim.PokeRotate(0, 5);
        _sim.PokeRotate(0, -3);
        Drain();
        Press(0);
        executor.Release();
        await _worker!.LastDispatchTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "r", "r", "r", "p" }, executor.Texts.ToArray());
    }

    [Fact]
    public async Task SlowTurnAction_WaitingTicksAreBounded_AndThePushStillRuns()
    {
        var executor = BuildGated();
        _sim!.PokeRotate(0, 1);
        Drain();
        Assert.True(SpinWait.SpinUntil(() => executor.Started >= 1, TimeSpan.FromSeconds(3)));

        for (var i = 0; i < 6; i++)
        {
            _sim.PokeRotate(0, 10);
        }
        Drain();
        Press(0);
        executor.Release();
        await _worker!.LastDispatchTask!.WaitAsync(TimeSpan.FromSeconds(5));

        var texts = executor.Texts.ToArray();
        Assert.Equal("p", texts[^1]);
        Assert.Equal(25, texts.Count(t => t == "r"));
    }

    private sealed class GateExecutor : IDeckActionExecutor
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Texts = new();
        public int Started;
        public void Release() => _gate.TrySetResult();
        public async Task ExecuteAsync(DeckAction? action, string serial, int keyIndex, string latchKey, CancellationToken ct)
        {
            Interlocked.Increment(ref Started);
            await _gate.Task;
            Texts.Enqueue(action!.Text!);
        }
        public bool IsToggleOn(DeckToggleState? state, string latchKey) => false;
        public void OpenApp() { }
    }

    [Fact]
    public async Task CustomTurn_BacklogIsCapped_AndTheChainStopsWhenTheDeckDisconnects()
    {
        var executor = new BlockingExecutor();
        _sim = new SimulatedStreamDeckSurface(Plus, Serial);
        var gateDev = new DeviceControlGate(_store);
        gateDev.SetEnabled("streamdeck", true);
        _worker = new StreamDeckConnectionWorker(
            new FakeWorkerHidEnumerator(), new HardwarePresence(new FixedUsbEnumerator()), gateDev, _store, executor,
            NewTestKeyRenderer(), _hub, _sensors, _sim, _clock, dialValues: _values);
        var config = JsonSerializer.Deserialize(Dials("{\"action\":{\"type\":\"custom\",\"turnRight\":{\"type\":\"text\",\"text\":\"r\"}}}"), AppJsonContext.Default.DeckConfig)!;
        _store.Update(s => s.StreamDeck.Decks[Serial] = new PhysicalDeckSettings { LegacyDeck = config });
        _store.Update(s => ActivateLegacyDeck(s, Serial, Plus.Columns, Plus.Rows));
        _worker.Tick();

        for (var i = 0; i < 30; i++)
        {
            _sim.PokeRotate(0, 1);
        }
        Drain();
        Assert.True(SpinWait.SpinUntil(() => executor.Started >= 1, TimeSpan.FromSeconds(3)));
        var chain = _worker.LastDispatchTask!;

        _worker.ClearSimulatedModel();
        await chain.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, executor.Started);
    }

    private sealed class BlockingExecutor : IDeckActionExecutor
    {
        public int Started;
        public async Task ExecuteAsync(DeckAction? action, string serial, int keyIndex, string latchKey, CancellationToken ct)
        {
            Interlocked.Increment(ref Started);
            await Task.Delay(Timeout.Infinite, ct);
        }
        public bool IsToggleOn(DeckToggleState? state, string latchKey) => false;
        public void OpenApp() { }
    }

    [Fact]
    public void RecentAppsMode_LongTouchDoesNotOpenTheEditor()
    {
        Build(Plus, Dials(Volume("a")));
        _store.Update(s => s.StreamDeck.Instances[DeckInstanceResolver.PhysicalInstanceId(Serial)].Mode = "recentApps");
        _worker!.RefreshView(Serial);

        _sim!.PokeTouch(StreamDeckTouchKind.Long, 650, 50);
        Drain();

        Assert.False(_worker.TryGetPendingEdit(out _));
        Assert.Equal(0, _executor.OpenAppCount);
    }

    [Fact]
    public void SameViewRefresh_DoesNotRewriteTheStripOrAddASparklineSample()
    {
        Build(Plus, Dials(Volume("a"), "{\"action\":{\"type\":\"deckBrightness\"}}"));
        _values.Set("volume:a", 30);
        _worker!.Tick();
        var writes = _sim!.ScreenRegions.Count;

        _worker.RefreshView(Serial);
        _worker.RefreshView(Serial);

        Assert.Equal(writes, _sim.ScreenRegions.Count);

        _values.Set("volume:a", 31);
        _worker.RefreshView(Serial);
        Assert.Equal(writes + 1, _sim.ScreenRegions.Count);
        Assert.Equal((0, 0, 200, 100), LastRegion());
    }

    [Fact]
    public void MonitoringHistory_IsKeptSeparatePerFolderLevel()
    {
        const string mon = "{\"action\":{\"type\":\"monitoring\",\"category\":\"cpu\",\"sensor\":\"cpu/core0\"}}";
        Build(Plus, "{\"pages\":[{\"slots\":[{\"folder\":{\"slots\":[{}],\"dials\":[" + mon + "]}}],\"dials\":[" + mon + "]}]}");
        _sensors.CpuSensors = new[] { new Nexus.Service.Models.Sensors.HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 10 } };
        _worker!.Tick();
        _worker.Tick();
        _worker.Tick();
        var before = _sim!.ScreenRegions.Count;

        _sim.Poke(0, true);
        _sim.Poke(0, false);
        Drain();
        _worker.Tick();

        // Entering the folder starts a fresh history (one sample), not the page dial's three.
        var folderSegment = _sim.ScreenRegions.Skip(before).Last();
        var pageSegment = _sim.ScreenRegions.Take(before).Last(r => r.Width == 800);
        Assert.NotEqual(folderSegment.Bytes.Length, pageSegment.Bytes.Length);
    }

    // ── Touch ──

    [Fact]
    public void Tap_ActsOnTheDialUnderTheFinger()
    {
        Build(Plus, Dials(Volume("a"), Volume("b"), Volume("c"), "{}"));
        _values.Set("volume:c", 10);

        _sim!.PokeTouch(StreamDeckTouchKind.Tap, 450, 50);
        Drain();

        Assert.Equal(("volume:c", true), _values.Mutes.Single());
    }

    [Fact]
    public void Tap_OnAnEmptySegment_DoesNothing()
    {
        Build(Plus, Dials(Volume("a")));

        _sim!.PokeTouch(StreamDeckTouchKind.Tap, 700, 50);
        Drain();

        Assert.Empty(_values.Mutes);
        Assert.False(_worker!.TryGetPendingEdit(out _));
    }

    [Fact]
    public async Task LongTouch_OnAnEmptySegment_StartsHoldToEditForThatDial()
    {
        var captured = new List<string>();
        Build(Plus, Dials(Volume("a")));
        _hub.OnBroadcastForTest += (_, payload) => captured.Add(Encoding.UTF8.GetString(payload.ToArray()));
        using var sub = _hub.AddTestSubscription(PanelTopics.StreamDeck);

        _sim!.PokeTouch(StreamDeckTouchKind.Long, 650, 50);
        Drain();

        Assert.True(_worker!.TryGetPendingEdit(out var edit));
        Assert.Equal(Serial, edit.Serial);
        Assert.Equal(3, edit.DialIndex);
        Assert.Equal(0, edit.Page);
        Assert.Contains(captured, p => p.Contains("editRequest") && p.Contains("\"dialIndex\":3"));
        Assert.NotNull(_worker.LastHoldFireTask);
        await _worker.LastHoldFireTask!;
        Assert.Equal(1, _executor.OpenAppCount);
    }

    [Fact]
    public void LongTouch_OnABoundSegment_BehavesAsATap()
    {
        Build(Plus, Dials(Volume("a")));
        _values.Set("volume:a", 10);

        _sim!.PokeTouch(StreamDeckTouchKind.Long, 50, 50);
        Drain();

        Assert.Single(_values.Mutes);
        Assert.False(_worker!.TryGetPendingEdit(out _));
    }

    [Fact]
    public void Swipe_LeftIsNextPage_RightIsPreviousPage()
    {
        Build(Plus, "{\"pages\":[{\"slots\":[]},{\"slots\":[]},{\"slots\":[]}]}");

        _sim!.PokeTouch(StreamDeckTouchKind.Flick, 535, 75, 485, 64);
        Drain();
        Assert.Equal(1, _worker!.GetCurrentPage(Serial));

        _sim.PokeTouch(StreamDeckTouchKind.Flick, 485, 64, 535, 75);
        Drain();
        Assert.Equal(0, _worker.GetCurrentPage(Serial));
    }

    [Fact]
    public void Swipe_TooShortOrMostlyVertical_IsIgnored()
    {
        Build(Plus, "{\"pages\":[{\"slots\":[]},{\"slots\":[]}]}");

        _sim!.PokeTouch(StreamDeckTouchKind.Flick, 400, 50, 390, 50);
        _sim.PokeTouch(StreamDeckTouchKind.Flick, 400, 10, 370, 90);
        Drain();

        Assert.Equal(0, _worker!.GetCurrentPage(Serial));
    }

    // ── Folder dials and tiles ──

    private const string FolderConfig =
        "{\"pages\":[{\"slots\":[" +
        "{\"folder\":{\"slots\":[{}],\"dials\":[{\"action\":{\"type\":\"volume\",\"deviceId\":\"inner\"}}]}}," +
        "{\"folder\":{\"slots\":[{}]}}]," +
        "\"dials\":[{\"action\":{\"type\":\"volume\",\"deviceId\":\"page\"}}]}]}";

    [Fact]
    public void FolderDials_ReplaceThePageDials_AFolderWithoutDialsKeepsThem()
    {
        Build(Plus, FolderConfig);
        _values.Set("volume:page", 10);
        _values.Set("volume:inner", 10);

        _sim!.Poke(0, true);
        _sim.Poke(0, false);
        Drain();
        Press(0);
        Assert.Equal("volume:inner", _values.Mutes.Single().Key);

        _sim.Poke(0, true);
        _sim.Poke(0, false);
        Drain();
        Assert.Empty(_worker!.GetFolderPath(Serial));
        _sim.Poke(1, true);
        _sim.Poke(1, false);
        Drain();
        Press(0);
        Assert.Equal("volume:page", _values.Mutes.Last().Key);
    }

    [Fact]
    public void Tiles_BroadcastDialSegments_WithFolderPrefixedPaths()
    {
        var captured = new List<(string Topic, byte[] Payload)>();
        _hub.OnBroadcastForTest += (topic, payload) => captured.Add((topic, payload.ToArray()));
        using var sub = _hub.AddTestSubscription(PanelTopics.StreamDeckTiles);

        Build(Plus, FolderConfig);

        string[] Paths() => captured.Where(c => c.Topic == PanelTopics.StreamDeckTiles)
            .Select(c => { using var doc = JsonDocument.Parse(c.Payload); return doc.RootElement.GetProperty("d").GetProperty("slotPath").GetString()!; })
            .ToArray();
        Assert.Equal(new[] { "dial:0", "dial:1", "dial:2", "dial:3" }, Paths().Where(p => p.StartsWith("dial")).OrderBy(p => p).ToArray());

        captured.Clear();
        _sim!.Poke(0, true);
        _sim.Poke(0, false);
        Drain();
        Assert.Contains("0.dial:0", Paths());
    }

    [Fact]
    public void Tiles_FirstSubscriberGetsEveryStaticDialSegment_AndUnchangedOnesAreNotResent()
    {
        Build(Plus, Dials(Volume("a"), "{\"action\":{\"type\":\"deckBrightness\"}}", "{\"action\":{\"type\":\"page\"}}"));
        _values.Set("volume:a", 30);
        _worker!.Tick();
        var captured = new List<(string Topic, byte[] Payload)>();
        _hub.OnBroadcastForTest += (topic, payload) => captured.Add((topic, payload.ToArray()));

        using var sub = _hub.AddTestSubscription(PanelTopics.StreamDeckTiles);

        string[] Paths() => captured.Where(c => c.Topic == PanelTopics.StreamDeckTiles)
            .Select(c => { using var doc = JsonDocument.Parse(c.Payload); return doc.RootElement.GetProperty("d").GetProperty("slotPath").GetString()!; })
            .ToArray();
        Assert.Equal(new[] { "dial:0", "dial:1", "dial:2", "dial:3" }, Paths().OrderBy(p => p).ToArray());

        captured.Clear();
        _worker.Tick();
        _worker.Tick();
        Assert.Empty(Paths());
    }

    [Fact]
    public void Tiles_NeoInfoTileIsBroadcastToAFirstSubscriber()
    {
        Build(Neo, "{\"pages\":[{\"slots\":[]}]}");
        var captured = new List<(string Topic, byte[] Payload)>();
        _hub.OnBroadcastForTest += (topic, payload) => captured.Add((topic, payload.ToArray()));

        using var sub = _hub.AddTestSubscription(PanelTopics.StreamDeckTiles);

        var paths = captured.Where(c => c.Topic == PanelTopics.StreamDeckTiles)
            .Select(c => { using var doc = JsonDocument.Parse(c.Payload); return doc.RootElement.GetProperty("d").GetProperty("slotPath").GetString(); })
            .ToList();
        Assert.Contains("info", paths);
    }

    // ── Feedback ──

    [Fact]
    public void Turn_ShowsAShortFeedbackFade_ThenSettlesOnThePlainSegment()
    {
        Build(Plus, Dials(Volume("out")));
        _values.Set("volume:out", 50);

        _sim!.PokeRotate(0, 1);
        Drain();
        var afterTurn = _sim.ScreenRegions.Count;

        _clock.Advance(TimeSpan.FromMilliseconds(100));
        _worker!.AnimateDialFeedback();
        Assert.True(_sim.ScreenRegions.Count > afterTurn);

        _clock.Advance(TimeSpan.FromMilliseconds(400));
        _worker.AnimateDialFeedback();
        var settled = _sim.ScreenRegions.Count;
        Assert.Equal((0, 0, 200, 100), LastRegion());

        _worker.AnimateDialFeedback();
        Assert.Equal(settled, _sim.ScreenRegions.Count);
    }

    // ── Info screen and touch keys (Neo) ──

    [Fact]
    public void Neo_PushesTheInfoScreen_AndFollowsTheSetting()
    {
        Build(Neo, "{\"pages\":[{\"slots\":[]},{\"slots\":[]}]}");
        var clock = _sim!.InfoScreenImage;
        Assert.NotNull(clock);

        _store.Update(s => s.StreamDeck.Decks[Serial].InfoScreen = "page");
        _worker!.RefreshView(Serial);
        var page = _sim.InfoScreenImage;
        Assert.NotEqual(clock, page);

        _store.Update(s => s.StreamDeck.Decks[Serial].InfoScreen = "off");
        _worker.RefreshView(Serial);
        Assert.NotEqual(page, _sim.InfoScreenImage);
    }

    [Fact]
    public void Neo_ClockRepaintsWhenTheMinuteChanges_NotBefore()
    {
        Build(Neo, "{\"pages\":[{\"slots\":[]}]}");
        var first = _sim!.InfoScreenImage;

        _clock.Advance(TimeSpan.FromSeconds(20));
        _worker!.Tick();
        Assert.Same(first, _sim.InfoScreenImage);

        _clock.Advance(TimeSpan.FromMinutes(2));
        _worker.Tick();
        Assert.NotSame(first, _sim.InfoScreenImage);
    }

    [Fact]
    public void Neo_TouchKeysPageAndLightWhereAPageExists()
    {
        Build(Neo, "{\"pages\":[{\"slots\":[]},{\"slots\":[]}]}");
        Assert.Equal((0, 0, 0), _sim!.KeyFills[8]);
        Assert.NotEqual((0, 0, 0), _sim.KeyFills[9]);

        _sim.PokeTouchKey(1, true);
        Drain();
        _sim.PokeTouchKey(1, false);
        Drain();

        Assert.Equal(1, _worker!.GetCurrentPage(Serial));
        Assert.NotEqual((0, 0, 0), _sim.KeyFills[8]);
        Assert.Equal((0, 0, 0), _sim.KeyFills[9]);

        _sim.PokeTouchKey(0, true);
        Drain();
        Assert.Equal(0, _worker.GetCurrentPage(Serial));
    }

    // ── Rings ──

    [Fact]
    public void Studio_RingsShowTheValue_AndGoDarkWhileAsleep()
    {
        Build(Studio, Dials(Volume("out")));
        _values.Set("volume:out", 50);
        _worker!.Tick();

        var ring = _sim!.PeekRing(0)!;
        Assert.Equal(24 * 3, ring.Length);
        var lit = Enumerable.Range(0, 24).Count(i => ring[i * 3] > 40 || ring[i * 3 + 1] > 40 || ring[i * 3 + 2] > 40);
        Assert.Equal(12, lit);
        Assert.All(_sim.PeekRing(1)!, b => Assert.Equal(0, b));
        Assert.NotEqual((0, 0, 0), ((int)_sim.PeekCenterLed(0).R, _sim.PeekCenterLed(0).G, _sim.PeekCenterLed(0).B));

        _worker.PutAsleep(Serial);
        Assert.All(_sim.PeekRing(0)!, b => Assert.Equal(0, b));

        _sim.PokeDialPress(0, true);
        Drain();
        var woke = _sim.PeekRing(0)!;
        Assert.Contains(woke, b => b > 40);
    }

    [Theory]
    [InlineData(100, 4)]
    [InlineData(51, 3)]
    [InlineData(25, 1)]
    [InlineData(0, 0)]
    public void Galleon_RingLightsFourLedsFromTheValue(int percent, int expectedLit)
    {
        Build(Galleon, Dials(Volume("out")));
        _values.Set("volume:out", percent);
        _worker!.Tick();

        var ring = _sim!.PeekRing(0)!;
        Assert.Equal(4 * 3, ring.Length);
        var lit = Enumerable.Range(0, 4).Count(i => ring[i * 3] > 40 || ring[i * 3 + 1] > 40 || ring[i * 3 + 2] > 40);
        Assert.Equal(expectedLit, lit);
    }

    [Fact]
    public void Galleon_PushesTheDialScreenWithTwoSegments()
    {
        Build(Galleon, Dials(Volume("a"), Volume("b")));

        Assert.Equal((0, 0, 720, 384), LastRegion());
        _values.Set("volume:b", 10);
        _sim!.PokeRotate(1, 1);
        Drain();
        Assert.Equal((360, 0, 360, 384), LastRegion());
    }

    [Fact]
    public void Studio_HasNoScreen_SoNoRegionIsEverWritten()
    {
        Build(Studio, Dials(Volume("a")));
        _sim!.PokeRotate(0, 1);
        Drain();

        Assert.Empty(_sim.ScreenRegions);
    }
}
