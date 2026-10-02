using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting.Rgb;

/// <summary>Bridge orchestration against a controller that stops answering; the socket bound itself is proven in <see cref="OpenRgbControllerTimeoutTests"/>.</summary>
public class RgbBridgeWatchdogTests : IDisposable
{
    private sealed class WedgeableController : IRgbController
    {
        private readonly List<string> _ops = new();
        private readonly object _gate = new();
        // Parked requests never return; the controller's real bound is not
        // under test here, only that the bridge does not wait on them.
        private readonly TaskCompletionSource _never = new();

        public volatile bool Park;
        public volatile bool Fail;
        // A daemon whose SDK server is not accepting yet refuses this many connects.
        public int RefuseConnects;
        private volatile bool _connected;

        public bool IsConnected => _connected;
        public event Action? DeviceListChanged;
        public void RaiseListChanged() => DeviceListChanged?.Invoke();
        public event Action<bool>? DetectionStateChanged;
        public event Action? DetectionProgress;
        public void RaiseProgress() => DetectionProgress?.Invoke();
        public void CompleteDetection() => DetectionStateChanged?.Invoke(false);
        public event Action<int, uint, uint>? WriteRejected { add { } remove { } }
        public async Task<IReadOnlyList<int>> GetControllerAddressesAsync(CancellationToken ct = default) =>
            (await GetDevicesAsync(ct)).Select(d => d.Address).ToList();
        // A wedged daemon: the real request lock wait times out the same way, shortened here.
        public async Task<bool> RescanAsync(CancellationToken ct = default)
        {
            Record("rescan");
            if (Park || Fail)
            {
                await Task.Delay(200, ct);
                throw new IOException("OpenRGB socket busy for 5s");
            }
            return true;
        }
        public Task<bool> SetSettingsAsync(string key, string valueJson, CancellationToken ct = default) => Task.FromResult(true);

        public IReadOnlyList<RgbDevice> Devices { get; set; } = new[]
        {
            new RgbDevice { Index = 0, Address = 0, Name = "DRAM", LedCount = 4, Serial = "A1" },
        };

        public int Count(string kind)
        {
            lock (_gate) { return _ops.Count(o => o == kind); }
        }

        private void Record(string kind)
        {
            lock (_gate) { _ops.Add(kind); }
        }

        public Task<bool> TryConnectAsync(CancellationToken ct = default)
        {
            Record("connect");
            if (Interlocked.Decrement(ref RefuseConnects) >= 0)
            {
                return Task.FromResult(false);
            }
            _connected = true;
            return Task.FromResult(true);
        }

        // Bounded like the real one: records and returns even with a request
        // parked, so the test observes what the bridge does next.
        public Task DisconnectAsync()
        {
            Record("disconnect");
            _connected = false;
            return Task.CompletedTask;
        }

        public async Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(CancellationToken ct = default)
        {
            Record("getDevices");
            if (Park)
            {
                await _never.Task;
            }
            if (Fail)
            {
                throw new IOException("OpenRGB read timed out after 3s");
            }
            return Devices;
        }

        public Task SetDirectModeAsync(RgbDevice device, CancellationToken ct = default) => Task.CompletedTask;
        public Task PushFrameAsync(int address, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetOffAsync(int address, int ledCount, CancellationToken ct = default) => Task.CompletedTask;
        public Task PushZoneFrameAsync(int address, int zoneIndex, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResizeZoneAsync(int address, int zoneIndex, int newSize, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private readonly string _tempDir;
    private readonly WedgeableController _controller = new();
    private readonly LightingEngine _engine = new();
    private readonly RgbBridge _bridge;

    public RgbBridgeWatchdogTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-test-watchdog-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        // Nonexistent binary: Start()/Stop() are no-ops and the bridge still activates.
        _bridge = new RgbBridge(
            new OpenRgbProcessManager(Path.Combine(_tempDir, "missing-openrgb")),
            _controller,
            _engine,
            new TestableConfigStore(Path.Combine(_tempDir, "settings.json")),
            new StubUsbEnumerator());
        // Past warm-up: failures count.
        _bridge.DaemonUptimeProbe = () => TimeSpan.FromMinutes(5);
    }

    public void Dispose()
    {
        _bridge.Dispose();
        _bridge.AwaitShutdown();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private async Task ActivateAndSettleAsync()
    {
        _bridge.Activate();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline && _bridge.Devices.Count == 0)
        {
            await Task.Delay(20);
        }
        Assert.NotEmpty(_bridge.Devices);
        _controller.CompleteDetection();
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> cond, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (cond())
            {
                return true;
            }
            await Task.Delay(50);
        }
        return cond();
    }

    [Fact]
    public async Task Rescan_while_a_refresh_is_parked_still_restarts_and_reconnects()
    {
        await ActivateAndSettleAsync();
        _controller.Park = true;
        // Let the refresh loop park a GetDevices call.
        Assert.True(await WaitUntilAsync(() => _controller.Count("getDevices") >= 2, TimeSpan.FromSeconds(8)));
        var connectsBefore = _controller.Count("connect");

        _bridge.ForceRescan();

        // The bounce reaches its reconnect step without the parked request ever returning.
        Assert.True(await WaitUntilAsync(() => _controller.Count("connect") > connectsBefore, TimeSpan.FromSeconds(3)),
            "rescan waited on the parked request");
    }

    [Fact]
    public async Task Deactivate_while_a_refresh_is_parked_finishes_within_a_bound()
    {
        await ActivateAndSettleAsync();
        _controller.Park = true;
        Assert.True(await WaitUntilAsync(() => _controller.Count("getDevices") >= 2, TimeSpan.FromSeconds(8)));

        _bridge.Deactivate();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _bridge.AwaitShutdown();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"shutdown took {sw.Elapsed}");
        Assert.Equal(1, _controller.Count("disconnect"));
    }

    [Fact]
    public async Task Watchdog_bounces_after_consecutive_failures_then_holds_the_floor()
    {
        await ActivateAndSettleAsync();
        Assert.Equal(0, _controller.Count("disconnect"));

        _controller.Fail = true;

        // Two failed refreshes on the refresh cadence -> one watchdog bounce.
        Assert.True(await WaitUntilAsync(() => _controller.Count("disconnect") == 1, TimeSpan.FromSeconds(15)),
            "watchdog did not bounce after two failures");

        // Failures keep coming, but the floor holds: no second bounce.
        await Task.Delay(TimeSpan.FromSeconds(8));
        Assert.Equal(1, _controller.Count("disconnect"));
    }

    [Fact]
    public async Task A_restarted_daemon_reusing_the_same_ids_still_gets_its_push_buffers_back()
    {
        await ActivateAndSettleAsync();
        Assert.Equal(1, _bridge.PhysicalBufferCount);

        _controller.Fail = true;
        Assert.True(await WaitUntilAsync(() => _controller.Count("disconnect") == 1, TimeSpan.FromSeconds(15)),
            "watchdog did not bounce");
        _controller.Fail = false;

        // The new daemon lists the same controller id and this test never sends
        // DETECTION_COMPLETE (a client that connects late misses it).
        Assert.True(await WaitUntilAsync(() => _controller.Count("connect") >= 2 && _bridge.PhysicalBufferCount == 1, TimeSpan.FromSeconds(20)),
            "push buffers never came back after the reconnect");
    }

    [Fact]
    public async Task A_hotplugged_device_is_committed_without_waiting_out_the_detection_hold()
    {
        await ActivateAndSettleAsync();
        _controller.Devices = new[]
        {
            new RgbDevice { Index = 0, Address = 0, Name = "DRAM", LedCount = 4, Serial = "A1" },
            new RgbDevice { Index = 1, Address = 1, Name = "Mouse", LedCount = 15, Serial = "M1" },
        };

        // Hotplug runs the device's detector (a PROGRESS with no STARTED) then announces the list.
        _controller.RaiseProgress();
        _controller.RaiseListChanged();

        Assert.True(await WaitUntilAsync(() => _bridge.Devices.Count == 2, TimeSpan.FromSeconds(3)),
            "the hotplugged device waited for the detection hold");
    }

    [Fact]
    public async Task Watchdog_ignores_failures_while_the_daemon_is_still_warming_up()
    {
        _bridge.DaemonUptimeProbe = () => TimeSpan.FromSeconds(5);
        await ActivateAndSettleAsync();

        _controller.Fail = true;
        Assert.True(await WaitUntilAsync(() => _controller.Count("getDevices") >= 3, TimeSpan.FromSeconds(12)),
            "refresh loop did not keep ticking");

        Assert.Equal(0, _controller.Count("disconnect"));
    }

    [Fact]
    public async Task A_just_started_daemon_that_is_not_listening_yet_is_connected_before_the_reconnect_cooldown()
    {
        _bridge.DaemonUptimeProbe = () => TimeSpan.FromSeconds(1);
        _controller.RefuseConnects = 2;

        _bridge.Activate();

        // The reconnect cooldown alone would put the first retry past this bound.
        Assert.True(await WaitUntilAsync(() => _bridge.Devices.Count > 0, TimeSpan.FromSeconds(4)), "still waiting out the reconnect cooldown");
    }
}
