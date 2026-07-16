using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Nexus.Service.Deck;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Peripherals.StreamDeck;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.StreamDeck;

/// <summary>Manually-advanced TimeProvider so idle-timeout tests never sleep real time.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;
    public ManualTimeProvider(DateTimeOffset utcNow) { _utcNow = utcNow; }
    public override DateTimeOffset GetUtcNow() => _utcNow;
    public void Advance(TimeSpan by) => _utcNow += by;
}

/// <summary>
/// Covers StreamDeckConnectionWorker's sleep-after-idle timer: ApplySleepAfterIdle
/// (ticked every TickMs) and WakeIfAsleep (driven by HandleKeyDown). Uses the
/// simulated surface (no HID plumbing needed) and a ManualTimeProvider so idle
/// elapsed time is injected, never a real sleep.
/// </summary>
public sealed class StreamDeckSleepAfterTests : IDisposable
{
    private static readonly StreamDeckModel Mini = StreamDeckModels.ByProductId(0x0063)!;

    private readonly string _imageCacheDir = Path.Combine(Path.GetTempPath(), "nexus-streamdeck-sleep-test-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly InMemoryConfigStore _store = new();
    private readonly FakeDeckActionExecutor _executor = new();
    private readonly FakeSensorProvider _sensors = new();
    private readonly ManualTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly SimulatedStreamDeckSurface _simulated;
    private readonly StreamDeckConnectionWorker _worker;

    public StreamDeckSleepAfterTests()
    {
        _simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        var presence = new HardwarePresence(new FixedUsbEnumerator());
        var gate = new DeviceControlGate(_store);
        // See NewFixtures in StreamDeckConnectionWorkerTests.cs: "streamdeck"
        // defaults off (mapped Elgato competitor), so opt in explicitly.
        gate.SetEnabled("streamdeck", true);
        var imageCache = new StreamDeckImageCache(_imageCacheDir);
        _worker = new StreamDeckConnectionWorker(
            new FakeWorkerHidEnumerator(), presence, gate, _store, _executor, imageCache, new MultiplexHub(), _sensors,
            _simulated, _clock);
    }

    public void Dispose()
    {
        _worker.Dispose();
        try { Directory.Delete(_imageCacheDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Tick_IdleBeyondSleepAfterSeconds_BlanksBrightness()
    {
        _store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings { Brightness = 80, SleepAfterSeconds = 30 });

        _worker.Tick(); // connects; ApplyPersistedBrightness pushes the persisted value
        Assert.Equal(80, _simulated.Brightness);
        Assert.False(_worker.IsAsleep("sim-0001"));

        _clock.Advance(TimeSpan.FromSeconds(31));
        _worker.Tick();

        Assert.Equal(0, _simulated.Brightness);
        Assert.True(_worker.IsAsleep("sim-0001"));
    }

    [Fact]
    public void Tick_IdleWithinThreshold_StaysAwake()
    {
        _store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings { Brightness = 80, SleepAfterSeconds = 30 });
        _worker.Tick();

        _clock.Advance(TimeSpan.FromSeconds(29));
        _worker.Tick();

        Assert.Equal(80, _simulated.Brightness);
        Assert.False(_worker.IsAsleep("sim-0001"));
    }

    [Fact]
    public void Tick_SleepAfterSecondsZero_NeverSleeps()
    {
        _store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings { Brightness = 80, SleepAfterSeconds = 0 });
        _worker.Tick();

        _clock.Advance(TimeSpan.FromDays(1));
        _worker.Tick();

        Assert.Equal(80, _simulated.Brightness);
        Assert.False(_worker.IsAsleep("sim-0001"));
    }

    [Fact]
    public async Task KeyDown_WhileAsleep_RestoresLatestPersistedBrightnessBeforeDispatch()
    {
        var action = new DeckAction { Type = "openUrl", Url = "https://example.com" };
        _store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Brightness = 80,
            SleepAfterSeconds = 30,
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
        });
        _worker.Tick();
        _clock.Advance(TimeSpan.FromSeconds(31));
        _worker.Tick();
        Assert.Equal(0, _simulated.Brightness);

        // A brightness change made while asleep (e.g. from the editor) must
        // apply on wake, not the value captured before sleep.
        _store.Update(s => s.StreamDeck.Decks["sim-0001"].Brightness = 55);

        _simulated.Poke(0, true);
        _worker.Tick();
        if (_worker.LastDispatchTask is not null)
        {
            await _worker.LastDispatchTask;
        }

        Assert.Equal(55, _simulated.Brightness);
        Assert.False(_worker.IsAsleep("sim-0001"));
        var call = Assert.Single(_executor.Calls);
        Assert.Same(action, call.Action);
    }

    [Fact]
    public async Task SimulatePress_WhileAsleep_RestoresBrightnessBeforeDispatch()
    {
        var action = new DeckAction { Type = "openUrl", Url = "https://example.com" };
        _store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Brightness = 80,
            SleepAfterSeconds = 30,
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
        });
        _worker.Tick();
        _clock.Advance(TimeSpan.FromSeconds(31));
        _worker.Tick();
        Assert.Equal(0, _simulated.Brightness);

        // A test-press on a sleeping deck must wake it too, the same as a
        // real key press - not just dispatch the action into the dark.
        var config = _store.Load().StreamDeck.Decks["sim-0001"].Deck;
        Assert.True(_worker.SimulatePress("sim-0001", new List<int> { 0 }, config));
        if (_worker.LastDispatchTask is not null)
        {
            await _worker.LastDispatchTask;
        }

        Assert.Equal(80, _simulated.Brightness);
        Assert.False(_worker.IsAsleep("sim-0001"));
        var call = Assert.Single(_executor.Calls);
        Assert.Same(action, call.Action);
    }

    [Fact]
    public void Tick_KeyActivityResetsTheIdleClock()
    {
        _store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings { Brightness = 80, SleepAfterSeconds = 30 });
        _worker.Tick();

        _clock.Advance(TimeSpan.FromSeconds(20));
        _simulated.Poke(0, true);
        _worker.Tick();
        _simulated.Poke(0, false);
        _worker.Tick();

        // Elapsed time since connect now exceeds the threshold, but elapsed
        // time since the press/release above does not.
        _clock.Advance(TimeSpan.FromSeconds(20));
        _worker.Tick();

        Assert.Equal(80, _simulated.Brightness);
        Assert.False(_worker.IsAsleep("sim-0001"));
    }

    [Fact]
    public void IsAsleep_UnknownSerial_ReturnsFalse()
    {
        Assert.False(_worker.IsAsleep("never-connected"));
    }

    [Fact]
    public void Tick_MonitoringSlot_StopsPushingOnceTheDeckIsAsleep()
    {
        _sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 10f, Formatted = "10%", Parent = new SensorParent() },
        };
        var action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "line" };
        _store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Brightness = 80,
            SleepAfterSeconds = 30,
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
        });

        // Connect drives PushCurrentView's two-pass repaint for the one
        // monitoring slot: an empty placeholder, then the real tile.
        _worker.Tick();
        Assert.Equal(2, _simulated.SetKeyImageCallCount);

        _clock.Advance(TimeSpan.FromSeconds(31));
        _worker.Tick();
        Assert.True(_worker.IsAsleep("sim-0001"));

        // Change the sensor value while asleep so an unguarded render would
        // produce a different, pushable frame - it must still not push.
        _sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 90f, Formatted = "90%", Parent = new SensorParent() },
        };
        _worker.Tick();
        _worker.Tick();

        Assert.Equal(2, _simulated.SetKeyImageCallCount);
    }
}
