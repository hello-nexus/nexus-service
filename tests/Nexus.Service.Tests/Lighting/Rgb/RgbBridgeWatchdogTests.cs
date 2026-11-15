using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Sensors;
using Nexus.Service.Tests.Common;

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
        // A session that closes under the request: the real controller then answers with an empty list.
        public volatile bool DropOnList;
        // A daemon whose SDK server is not accepting yet refuses this many connects.
        public int RefuseConnects;
        private volatile bool _connected;

        public bool IsConnected => _connected;
        public event Action? DeviceListChanged;
        public void RaiseListChanged() => DeviceListChanged?.Invoke();
        public event Action<bool>? DetectionStateChanged;
        public event Action? DetectionProgress;
        public void RaiseProgress() => DetectionProgress?.Invoke();
        public void StartDetection() => DetectionStateChanged?.Invoke(true);
        public void CompleteDetection() => DetectionStateChanged?.Invoke(false);
        public event Action<int, uint, uint>? WriteRejected { add { } remove { } }
        // Holds the controller-count request until released, so a test can overlap a second deadline with a look in flight.
        public volatile Task? AddressGate;

        public async Task<IReadOnlyList<int>> GetControllerAddressesAsync(CancellationToken ct = default)
        {
            Record("addresses");
            if (AddressGate is { } gate)
            {
                await gate;
            }
            if (DropOnList)
            {
                _connected = false;
                return Array.Empty<int>();
            }
            return (await GetDevicesAsync(ct)).Select(d => d.Address).ToList();
        }
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
    private readonly FakeOneShot.Factory _deadlines = new();

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
        _bridge.OneShotFactory = _deadlines.Create;
        _bridge.SettleStallEnabled = true;
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

    // Settle-stall tests drive time through these instead of waiting on the wall clock.
    private long _clockTicks;
    private TimeSpan _uptime = TimeSpan.FromMinutes(5);

    private void UseFakeClock()
    {
        _clockTicks = DateTime.UtcNow.Ticks;
        _bridge.NowTicks = () => Volatile.Read(ref _clockTicks);
        _bridge.DaemonUptimeProbe = () => _uptime;
    }

    private void Advance(TimeSpan by) => Interlocked.Add(ref _clockTicks, by.Ticks);

    private async Task ResumeAndWaitForBounceAsync(int disconnects)
    {
        _bridge.OnSystemResume();
        Assert.True(await WaitUntilAsync(() => _controller.Count("disconnect") == disconnects && _controller.IsConnected, TimeSpan.FromSeconds(5)),
            "the restart did not complete");
    }

    // What the settle deadline does when it fires: one look, then the look a refused early restart asks for at once.
    private async Task LookAtSettleAsync()
    {
        await _bridge.EvaluateSettleStallAsync();
        Assert.True(await WaitUntilAsync(() => !_bridge.SelfRestartDeciding, TimeSpan.FromSeconds(5)));
        if (_deadlines.Single is { Armed: true } armed && armed.Due == TimeSpan.Zero)
        {
            await _bridge.EvaluateSettleStallAsync();
        }
    }

    private async Task PollAndExpectBounceAsync(int disconnects)
    {
        await LookAtSettleAsync();
        Assert.True(await WaitUntilAsync(() => _controller.Count("disconnect") == disconnects && _controller.IsConnected, TimeSpan.FromSeconds(5)),
            "the stalled daemon was not restarted");
    }

    // Whether the look decided a daemon restart is known when it returns, so a refusal needs no waiting.
    private async Task PollAndExpectNoBounceAsync(int disconnects)
    {
        var decided = _bridge.BounceCount;
        await LookAtSettleAsync();
        Assert.Equal(decided, _bridge.BounceCount);
        Assert.Equal(disconnects, _controller.Count("disconnect"));
    }

    [Fact]
    public async Task A_resume_restart_that_lists_nothing_is_restarted_at_the_settle_timeout_even_after_a_silent_started()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        _controller.Devices = Array.Empty<RgbDevice>();
        _uptime = TimeSpan.Zero;
        await ResumeAndWaitForBounceAsync(1);
        Assert.Equal(1, _bridge.ExpectedDevices);
        _controller.StartDetection();

        _uptime = TimeSpan.FromSeconds(29);
        Advance(_uptime);
        await PollAndExpectNoBounceAsync(1);

        // The STARTED hold alone would last the 60 s stall limit.
        _uptime = TimeSpan.FromSeconds(30);
        Advance(TimeSpan.FromSeconds(1));
        await PollAndExpectBounceAsync(2);
    }

    [Fact]
    public async Task A_second_stall_restart_in_one_arming_respects_the_detection_hold()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        _controller.Devices = Array.Empty<RgbDevice>();
        await ResumeAndWaitForBounceAsync(1);
        Advance(TimeSpan.FromSeconds(30));
        await PollAndExpectBounceAsync(2);

        // Detection started again and is merely slow: silent 30 s, inside the 60 s hold.
        _controller.StartDetection();
        Advance(TimeSpan.FromSeconds(30));
        await PollAndExpectNoBounceAsync(2);

        // Silent past the stall limit, and past the watchdog floor.
        Advance(TimeSpan.FromSeconds(40));
        await PollAndExpectBounceAsync(3);
    }

    [Fact]
    public async Task Only_resume_and_watchdog_restarts_arm_the_stall_check()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        _controller.Devices = Array.Empty<RgbDevice>();

        _bridge.BounceForManualDevices();
        Assert.True(await WaitUntilAsync(() => _controller.Count("disconnect") == 1 && _controller.IsConnected, TimeSpan.FromSeconds(5)));
        Assert.Equal(0, _bridge.ExpectedDevices);
        Advance(TimeSpan.FromMinutes(5));
        await PollAndExpectNoBounceAsync(1);
    }

    [Fact]
    public async Task A_rescan_that_falls_back_to_a_restart_does_not_arm_the_stall_check()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        _controller.Fail = true;
        _bridge.ForceRescan();
        Assert.True(await WaitUntilAsync(() => _controller.Count("disconnect") == 1 && _controller.IsConnected, TimeSpan.FromSeconds(5)));
        _controller.Fail = false;

        Assert.Equal(0, _bridge.ExpectedDevices);
    }

    [Fact]
    public async Task A_completed_empty_detection_is_the_truth_and_disarms_the_stall_check()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        _controller.Devices = Array.Empty<RgbDevice>();
        await ResumeAndWaitForBounceAsync(1);

        _controller.CompleteDetection();

        Assert.Equal(0, _bridge.ExpectedDevices);
        Advance(TimeSpan.FromMinutes(5));
        await PollAndExpectNoBounceAsync(1);
    }

    [Fact]
    public async Task A_restart_that_lists_its_devices_disarms_the_stall_check()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        await ResumeAndWaitForBounceAsync(1);
        Assert.Equal(1, _bridge.ExpectedDevices);

        // The detection quiet window lapses, then the refresh loop commits the daemon's list.
        Advance(TimeSpan.FromSeconds(10));
        Assert.True(await WaitUntilAsync(() => _bridge.ExpectedDevices == 0, TimeSpan.FromSeconds(8)));
        Advance(TimeSpan.FromMinutes(5));
        await PollAndExpectNoBounceAsync(1);
    }

    [Fact]
    public async Task A_session_that_closes_under_the_request_is_a_reconnect_not_a_stall()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        _controller.Devices = Array.Empty<RgbDevice>();
        await ResumeAndWaitForBounceAsync(1);
        Advance(TimeSpan.FromSeconds(30));

        _controller.DropOnList = true;
        await PollAndExpectNoBounceAsync(1);
        _controller.DropOnList = false;
    }

    [Fact]
    public async Task Giving_up_the_stall_restarts_stops_expecting_the_devices()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        _controller.Devices = Array.Empty<RgbDevice>();
        await ResumeAndWaitForBounceAsync(1);

        for (var n = 2; n <= 4; n++)
        {
            Advance(TimeSpan.FromSeconds(100));
            await PollAndExpectBounceAsync(n);
        }
        Assert.Equal(1, _bridge.ExpectedDevices);

        Advance(TimeSpan.FromSeconds(100));
        await PollAndExpectNoBounceAsync(4);
        Assert.Equal(0, _bridge.ExpectedDevices);
    }

    [Fact]
    public async Task Deactivate_disarms_the_stall_check()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        await ResumeAndWaitForBounceAsync(1);
        Assert.Equal(1, _bridge.ExpectedDevices);

        _bridge.Deactivate();

        Assert.Equal(0, _bridge.ExpectedDevices);
    }

    [Fact]
    public async Task A_daemon_that_never_listed_a_device_is_not_restarted_for_listing_none()
    {
        UseFakeClock();
        _controller.Devices = Array.Empty<RgbDevice>();
        _bridge.Activate();
        Assert.True(await WaitUntilAsync(() => _controller.Count("getDevices") >= 1, TimeSpan.FromSeconds(10)));

        Advance(TimeSpan.FromMinutes(5));
        await PollAndExpectNoBounceAsync(0);
    }

    [Fact]
    public async Task A_stalled_LHM_walk_with_the_smbus_mutex_held_restarts_the_daemon()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        _bridge.SmbusMutexProbe = () => "held";

        _bridge.OnLhmWalkStalled(TimeSpan.FromSeconds(60));

        Assert.True(await WaitUntilAsync(() => _controller.Count("disconnect") == 1 && _controller.IsConnected, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_stalled_LHM_walk_with_the_smbus_mutex_free_leaves_the_daemon_alone()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        _bridge.SmbusMutexProbe = () => "free";

        _bridge.OnLhmWalkStalled(TimeSpan.FromSeconds(60));

        Assert.Equal(0, _bridge.BounceCount);
        Assert.Equal(0, _controller.Count("disconnect"));
    }

    [Fact]
    public async Task LHM_stall_restarts_share_the_bounded_watchdog_budget()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        _bridge.SmbusMutexProbe = () => "held";

        for (var n = 1; n <= 3; n++)
        {
            Advance(TimeSpan.FromSeconds(100));
            _bridge.OnLhmWalkStalled(TimeSpan.FromSeconds(60));
            Assert.True(await WaitUntilAsync(() => _controller.Count("disconnect") == n && _controller.IsConnected, TimeSpan.FromSeconds(5)));
        }

        Advance(TimeSpan.FromSeconds(100));
        _bridge.OnLhmWalkStalled(TimeSpan.FromSeconds(60));
        Assert.Equal(3, _bridge.BounceCount);
        Assert.Equal(3, _controller.Count("disconnect"));
    }

    private List<string> _selfRestarts = new();
    private long _walkMs;

    private void WireSelfRestart(string mutex = "held")
    {
        _bridge.SmbusMutexProbe = () => mutex;
        _bridge.SmbusMutexWait = _ => mutex;
        _walkMs = 10_000;
        _bridge.WalkWatch = new LhmWalkWatch(() => Volatile.Read(ref _walkMs), _deadlines.Create);
        _bridge.SelfRestartQuiesceWait = TimeSpan.Zero;
        _bridge.SelfRestart = context =>
        {
            _selfRestarts.Add(context);
            return true;
        };
    }

    [Fact]
    public async Task Self_restart_runs_once_when_every_condition_holds()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        WireSelfRestart();
        await ResumeAndWaitForBounceAsync(1);

        Assert.True(_bridge.MaybeSelfRestart("test"));
        Assert.False(_bridge.MaybeSelfRestart("test"));

        Assert.Single(_selfRestarts);
        Assert.Contains("1 devices known before the suspend", _selfRestarts[0]);
    }

    [Fact]
    public async Task Self_restart_is_refused_without_a_resume()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        WireSelfRestart();

        Assert.False(_bridge.MaybeSelfRestart("test"));
        Assert.Empty(_selfRestarts);
    }

    [Fact]
    public async Task Self_restart_is_refused_when_the_resume_is_past_the_window()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        WireSelfRestart();
        await ResumeAndWaitForBounceAsync(1);
        Advance(TimeSpan.FromMinutes(16));

        Assert.False(_bridge.MaybeSelfRestart("test"));
        Assert.Empty(_selfRestarts);
    }

    [Fact]
    public async Task Self_restart_is_refused_when_no_devices_were_known_before_the_resume()
    {
        UseFakeClock();
        _controller.Devices = Array.Empty<RgbDevice>();
        _bridge.Activate();
        Assert.True(await WaitUntilAsync(() => _controller.Count("getDevices") >= 1, TimeSpan.FromSeconds(10)));
        WireSelfRestart();
        await ResumeAndWaitForBounceAsync(1);

        Assert.False(_bridge.MaybeSelfRestart("test"));
        Assert.Empty(_selfRestarts);
    }

    [Theory]
    [InlineData("free")]
    [InlineData("absent")]
    [InlineData("abandoned")]
    [InlineData("n/a")]
    public async Task Self_restart_is_refused_unless_the_smbus_mutex_is_held(string mutex)
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        WireSelfRestart(mutex);
        await ResumeAndWaitForBounceAsync(1);

        Assert.False(_bridge.MaybeSelfRestart("test"));
        Assert.Empty(_selfRestarts);
    }

    [Fact]
    public async Task Self_restart_is_refused_when_nothing_is_wired()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        _bridge.SmbusMutexProbe = () => "held";
        await ResumeAndWaitForBounceAsync(1);

        Assert.False(_bridge.MaybeSelfRestart("test"));
    }

    [Fact]
    public async Task A_first_settle_failure_with_the_mutex_held_restarts_the_service_without_a_daemon_restart()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        _controller.Devices = Array.Empty<RgbDevice>();
        WireSelfRestart();
        await ResumeAndWaitForBounceAsync(1);

        Advance(TimeSpan.FromSeconds(30));
        await PollAndExpectNoBounceAsync(1);

        Assert.True(await WaitUntilAsync(() => _selfRestarts.Count == 1, TimeSpan.FromSeconds(5)));
        Assert.Contains("mutex held on the first settle check", _selfRestarts[0]);
        Assert.Equal(1, _controller.Count("disconnect"));
    }

    [Fact]
    public async Task A_hold_the_wait_finds_free_falls_back_to_the_daemon_restart_at_once()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        _controller.Devices = Array.Empty<RgbDevice>();
        WireSelfRestart();
        await ResumeAndWaitForBounceAsync(1);
        var waits = 0;
        _bridge.SmbusMutexWait = _ =>
        {
            Interlocked.Increment(ref waits);
            return "free";
        };

        Advance(TimeSpan.FromSeconds(30));
        await _bridge.EvaluateSettleStallAsync();
        Assert.True(await WaitUntilAsync(() => !_bridge.SelfRestartDeciding, TimeSpan.FromSeconds(5)));

        // The refusal asks for the next look immediately instead of waiting for a poll.
        Assert.Equal(1, Volatile.Read(ref waits));
        Assert.True(_deadlines.Single!.Armed);
        Assert.Equal(TimeSpan.Zero, _deadlines.Single!.Due);
        await PollAndExpectBounceAsync(2);
        Assert.Empty(_selfRestarts);
    }

    [Fact]
    public async Task A_self_restart_the_service_refuses_falls_back_to_the_daemon_restart_at_once()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        _controller.Devices = Array.Empty<RgbDevice>();
        WireSelfRestart();
        var asked = 0;
        _bridge.SelfRestart = _ =>
        {
            Interlocked.Increment(ref asked);
            return false;
        };
        await ResumeAndWaitForBounceAsync(1);

        Advance(TimeSpan.FromSeconds(30));
        await _bridge.EvaluateSettleStallAsync();
        Assert.True(await WaitUntilAsync(() => Volatile.Read(ref asked) == 1 && !_bridge.SelfRestartDeciding, TimeSpan.FromSeconds(5)));

        Assert.Equal(TimeSpan.Zero, _deadlines.Single!.Due);
        await PollAndExpectBounceAsync(2);
    }

    [Fact]
    public async Task Exhausting_the_daemon_watchdog_budget_with_the_mutex_held_still_restarts_the_service_once()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        WireSelfRestart();
        await ResumeAndWaitForBounceAsync(1);

        Assert.True(_bridge.MaybeSelfRestart("daemon watchdog budget exhausted"));
        Assert.False(_bridge.MaybeSelfRestart("daemon watchdog budget exhausted"));

        Assert.Single(_selfRestarts);
    }

    [Fact]
    public async Task Exhausting_the_budget_with_the_mutex_free_does_not_restart_the_service()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        _controller.Devices = Array.Empty<RgbDevice>();
        WireSelfRestart("free");
        await ResumeAndWaitForBounceAsync(1);

        for (var n = 2; n <= 4; n++)
        {
            Advance(TimeSpan.FromSeconds(100));
            await PollAndExpectBounceAsync(n);
        }
        Advance(TimeSpan.FromSeconds(100));
        await PollAndExpectNoBounceAsync(4);

        Assert.Empty(_selfRestarts);
    }

    private int _waitCalls;
    private TimeSpan _lastWaitTimeout;

    private async Task ResumedAsync(Func<TimeSpan, LhmWalkWatch, string> wait, Func<string>? probe = null)
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        WireSelfRestart();
        await ResumeAndWaitForBounceAsync(1);
        var watch = _bridge.WalkWatch;
        _bridge.SmbusMutexWait = timeout =>
        {
            Interlocked.Increment(ref _waitCalls);
            _lastWaitTimeout = timeout;
            return wait(timeout, watch);
        };
        _bridge.SmbusMutexProbe = probe ?? (() => "held");
    }

    [Fact]
    public async Task One_wait_for_the_whole_window_decides_and_a_timeout_means_held()
    {
        await ResumedAsync((_, _) => "held");

        Assert.True(_bridge.MaybeSelfRestart("test"));

        Assert.Equal(1, _waitCalls);
        Assert.Equal(_bridge.SelfRestartMutexWindow, _lastWaitTimeout);
        Assert.Single(_selfRestarts);
    }

    [Fact]
    public async Task A_wait_that_acquires_the_mutex_means_it_was_not_held_throughout()
    {
        await ResumedAsync((_, _) => "free");

        Assert.False(_bridge.MaybeSelfRestart("test"));

        Assert.Equal(1, _waitCalls);
        Assert.Empty(_selfRestarts);
        Assert.False(_bridge.WalkWatch.SpdQuiesced);
    }

    [Fact]
    public async Task A_refused_wait_does_not_spend_the_once_per_resume_claim()
    {
        var state = "free";
        await ResumedAsync((_, _) => state);
        Assert.False(_bridge.MaybeSelfRestart("test"));

        state = "held";

        Assert.True(_bridge.MaybeSelfRestart("test"));
    }

    [Fact]
    public async Task The_wait_summary_names_what_the_walk_was_doing_when_it_started_and_ended()
    {
        await ResumedAsync((_, watch) =>
        {
            watch.EndWalk();
            return "held";
        });
        _bridge.WalkWatch.BeginWalk();
        _bridge.WalkWatch.SetItem("/memory/dimm/3");

        Assert.True(_bridge.MaybeSelfRestart("test"));

        Assert.Contains("held", _bridge.MutexWaitSummary);
        Assert.Contains("walk at start: lhm walk running", _bridge.MutexWaitSummary);
        Assert.Contains("inside /memory/dimm/3", _bridge.MutexWaitSummary);
        Assert.Contains("at end: lhm last walk", _bridge.MutexWaitSummary);
    }

    [Fact]
    public async Task A_decided_restart_pauses_our_dimm_reads_and_samples_the_mutex_once_more()
    {
        var probes = 0;
        await ResumedAsync((_, _) => "held", () =>
        {
            Interlocked.Increment(ref probes);
            return "held";
        });

        Assert.True(_bridge.MaybeSelfRestart("test"));

        Assert.True(_bridge.WalkWatch.SpdQuiesced);
        Assert.Equal(1, probes);
        Assert.Contains("not our DIMM reads", _bridge.HolderProbeResult);
    }

    [Fact]
    public async Task The_quiesce_probe_runs_once_per_resume()
    {
        var probes = 0;
        await ResumedAsync((_, _) => "held", () =>
        {
            Interlocked.Increment(ref probes);
            return "held";
        });

        Assert.True(_bridge.MaybeSelfRestart("test"));
        Assert.False(_bridge.MaybeSelfRestart("test"));

        Assert.Equal(1, probes);
        Assert.Equal(2, _waitCalls);
    }

    [Fact]
    public async Task A_quiesce_wait_that_times_out_inside_a_memory_item_is_reported_inconclusive()
    {
        await ResumedAsync((_, watch) =>
        {
            watch.BeginWalk();
            watch.SetItem("/memory/dimm/2");
            return "held";
        });
        _bridge.SelfRestartQuiesceWait = TimeSpan.FromMilliseconds(200);

        Assert.True(_bridge.MaybeSelfRestart("test"));

        Assert.Contains("inconclusive", _bridge.HolderProbeResult);
        Assert.Contains("/memory/dimm/2", _bridge.HolderProbeResult);
        Assert.DoesNotContain("not our DIMM reads", _bridge.HolderProbeResult);
    }

    [Fact]
    public async Task The_quiesce_wait_ends_the_moment_the_walk_leaves_its_memory_item()
    {
        await ResumedAsync((_, watch) =>
        {
            watch.BeginWalk();
            watch.SetItem("/memory/dimm/2");
            return "held";
        });
        _bridge.SelfRestartQuiesceWait = TimeSpan.FromSeconds(30);
        var decided = Task.Run(() => _bridge.MaybeSelfRestart("test"));
        Assert.True(await WaitUntilAsync(() => _bridge.WalkWatch.SpdQuiesced, TimeSpan.FromSeconds(5)));

        _bridge.WalkWatch.SetItem("/intelcpu/0");

        Assert.True(await decided);
        Assert.Contains("not our DIMM reads", _bridge.HolderProbeResult);
    }

    // ── Settle deadline ──

    [Fact]
    public async Task A_restart_that_must_bring_devices_back_arms_one_settle_deadline_at_the_timeout()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();

        await ResumeAndWaitForBounceAsync(1);

        Assert.True(await WaitUntilAsync(() => _deadlines.Single is { Armed: true }, TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.FromSeconds(30), _deadlines.Single!.Due);
    }

    [Fact]
    public async Task A_restart_with_no_devices_known_arms_no_deadline()
    {
        UseFakeClock();
        _controller.Devices = Array.Empty<RgbDevice>();
        _bridge.Activate();
        Assert.True(await WaitUntilAsync(() => _controller.Count("getDevices") >= 1, TimeSpan.FromSeconds(10)));

        await ResumeAndWaitForBounceAsync(1);

        Assert.Empty(_deadlines.Created);
    }

    [Fact]
    public async Task A_manual_device_restart_arms_no_deadline()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();

        _bridge.BounceForManualDevices();
        Assert.True(await WaitUntilAsync(() => _controller.Count("disconnect") == 1 && _controller.IsConnected, TimeSpan.FromSeconds(5)));

        Assert.Empty(_deadlines.Created);
    }

    [Fact]
    public async Task A_deadline_that_fires_before_the_timeout_asks_for_the_remainder()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        _controller.Devices = Array.Empty<RgbDevice>();
        _uptime = TimeSpan.Zero;
        await ResumeAndWaitForBounceAsync(1);
        _uptime = TimeSpan.FromSeconds(12);

        var next = await _bridge.EvaluateSettleStallAsync();

        Assert.Equal(TimeSpan.FromSeconds(18) + TimeSpan.FromMilliseconds(10), next);
        Assert.Equal(1, _controller.Count("disconnect"));
    }

    [Fact]
    public async Task A_deadline_inside_the_detection_quiet_window_asks_for_the_rest_of_it()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        _controller.Devices = Array.Empty<RgbDevice>();
        await ResumeAndWaitForBounceAsync(1);
        Advance(TimeSpan.FromSeconds(30));
        _controller.StartDetection();
        Advance(TimeSpan.FromSeconds(3));

        var next = await _bridge.EvaluateSettleStallAsync();

        Assert.NotNull(next);
        Assert.InRange(next!.Value, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(6));
        Assert.Equal(1, _controller.Count("disconnect"));
    }

    [Fact]
    public async Task Firing_the_settle_deadline_restarts_a_stalled_daemon_and_arms_the_next_one()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        _controller.Devices = Array.Empty<RgbDevice>();
        await ResumeAndWaitForBounceAsync(1);
        Advance(TimeSpan.FromSeconds(30));

        _deadlines.Single!.Fire();

        Assert.True(await WaitUntilAsync(() => _controller.Count("disconnect") == 2 && _controller.IsConnected, TimeSpan.FromSeconds(5)));
        Assert.True(await WaitUntilAsync(() => _deadlines.Single!.Armed, TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.FromSeconds(30), _deadlines.Single!.Due);
    }

    [Fact]
    public async Task A_finished_detection_pass_disarms_the_settle_deadline()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        _controller.Devices = Array.Empty<RgbDevice>();
        await ResumeAndWaitForBounceAsync(1);
        Assert.True(await WaitUntilAsync(() => _deadlines.Single is { Armed: true }, TimeSpan.FromSeconds(5)));

        _controller.CompleteDetection();

        Assert.False(_deadlines.Single!.Armed);
    }

    [Fact]
    public async Task Deactivating_disarms_the_settle_deadline()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        await ResumeAndWaitForBounceAsync(1);
        Assert.True(await WaitUntilAsync(() => _deadlines.Single is { Armed: true }, TimeSpan.FromSeconds(5)));

        _bridge.Deactivate();

        Assert.False(_deadlines.Single!.Armed);
    }

    [Fact]
    public async Task Disposing_the_bridge_disposes_the_settle_deadline()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        await ResumeAndWaitForBounceAsync(1);
        Assert.True(await WaitUntilAsync(() => _deadlines.Single is not null, TimeSpan.FromSeconds(5)));

        _bridge.Dispose();

        Assert.True(_deadlines.Single!.Disposed);
    }

    [Fact]
    public async Task A_settle_look_that_finds_no_session_waits_for_the_connect_instead_of_re_arming()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        _controller.Devices = Array.Empty<RgbDevice>();
        await ResumeAndWaitForBounceAsync(1);
        Assert.True(await WaitUntilAsync(() => _deadlines.Single is { ArmCount: 1 }, TimeSpan.FromSeconds(5)));
        Advance(TimeSpan.FromSeconds(30));
        await _controller.DisconnectAsync();

        var next = await _bridge.EvaluateSettleStallAsync();

        Assert.Null(next);
        // The refresh loop reconnects, and that connect is the event that arms the next look.
        Assert.True(await WaitUntilAsync(() => _deadlines.Single is { ArmCount: >= 2, Due: var due } && due == TimeSpan.Zero, TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public async Task A_settle_look_that_finds_listed_devices_leaves_the_commit_to_disarm_it()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        await ResumeAndWaitForBounceAsync(1);
        Advance(TimeSpan.FromSeconds(30));

        var next = await _bridge.EvaluateSettleStallAsync();

        Assert.Null(next);
        Assert.Equal(1, _controller.Count("disconnect"));
    }

    [Fact]
    public async Task Looks_that_only_find_the_stall_not_yet_due_never_use_up_the_cap()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        _controller.Devices = Array.Empty<RgbDevice>();
        _uptime = TimeSpan.Zero;
        await ResumeAndWaitForBounceAsync(1);
        Assert.True(await WaitUntilAsync(() => _deadlines.Single is { ArmCount: 1 }, TimeSpan.FromSeconds(5)));

        // Slow but live detection keeps pushing the look back, far past the cap.
        for (var look = 1; look <= 30; look++)
        {
            _deadlines.Single!.Fire();
            await _bridge.LastSettleDeadlineCheck!;
            Assert.Equal(1 + look, _deadlines.Single!.ArmCount);
        }

        // The daemon then goes silent with nothing listed: the check is still alive and acts.
        _uptime = TimeSpan.FromMinutes(5);
        Advance(TimeSpan.FromSeconds(30));
        _deadlines.Single!.Fire();

        Assert.True(await WaitUntilAsync(() => _controller.Count("disconnect") == 2 && _controller.IsConnected, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Looks_that_find_the_stall_due_but_cannot_act_stop_re_arming_after_a_bounded_number()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        _controller.Devices = Array.Empty<RgbDevice>();
        await ResumeAndWaitForBounceAsync(1);
        Advance(TimeSpan.FromSeconds(30));
        _deadlines.Single!.Fire();
        Assert.True(await WaitUntilAsync(() => _controller.Count("disconnect") == 2 && _controller.IsConnected, TimeSpan.FromSeconds(5)));
        Assert.True(await WaitUntilAsync(() => _deadlines.Single is { Armed: true, Due: var due } && due == TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5)));
        Advance(TimeSpan.FromSeconds(30));
        var armsBefore = _deadlines.Single!.ArmCount;

        // Inside the restart floor the stall is due and nothing can be done.
        for (var look = 1; look <= 12; look++)
        {
            _deadlines.Single!.Fire();
            await _bridge.LastSettleDeadlineCheck!;
            Assert.Equal(armsBefore + look, _deadlines.Single!.ArmCount);
        }
        _deadlines.Single!.Fire();
        await _bridge.LastSettleDeadlineCheck!;

        Assert.Equal(armsBefore + 12, _deadlines.Single!.ArmCount);
        Assert.False(_deadlines.Single!.Armed);
        Assert.Equal(2, _controller.Count("disconnect"));
    }

    [Fact]
    public async Task A_self_restart_decision_that_throws_is_a_refusal_and_the_daemon_restart_follows()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        _controller.Devices = Array.Empty<RgbDevice>();
        WireSelfRestart();
        _bridge.SelfRestart = _ => throw new InvalidOperationException("hook failed");
        await ResumeAndWaitForBounceAsync(1);
        Advance(TimeSpan.FromSeconds(30));

        await _bridge.EvaluateSettleStallAsync();
        Assert.True(await WaitUntilAsync(() => !_bridge.SelfRestartDeciding, TimeSpan.FromSeconds(5)));

        Assert.True(_deadlines.Single!.Armed);
        Assert.Equal(TimeSpan.Zero, _deadlines.Single!.Due);
        await PollAndExpectBounceAsync(2);
    }

    [Fact]
    public async Task A_deadline_that_fires_during_a_look_is_coalesced_into_exactly_one_more_look()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        _controller.Devices = Array.Empty<RgbDevice>();
        await ResumeAndWaitForBounceAsync(1);
        Advance(TimeSpan.FromSeconds(30));
        // A full fetch still running from the restart would make the look return before it reaches the gate.
        Assert.True(await WaitUntilAsync(() => !_bridge.RefreshInFlight, TimeSpan.FromSeconds(5)));
        var gate = new TaskCompletionSource();
        _controller.AddressGate = gate.Task;
        var looksBefore = _bridge.SettleLookCount;

        _deadlines.Single!.Fire();
        Assert.True(await WaitUntilAsync(() => _controller.Count("addresses") >= 1, TimeSpan.FromSeconds(5)));
        _deadlines.Single!.Fire();
        _deadlines.Single!.Fire();
        _deadlines.Single!.Fire();
        Assert.Equal(looksBefore + 1, _bridge.SettleLookCount);

        _controller.AddressGate = null;
        gate.SetResult();
        await _bridge.LastSettleDeadlineCheck!;

        Assert.Equal(looksBefore + 2, _bridge.SettleLookCount);
    }

    [Fact]
    public async Task A_lone_deadline_runs_exactly_one_look()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        _controller.Devices = Array.Empty<RgbDevice>();
        await ResumeAndWaitForBounceAsync(1);
        var looksBefore = _bridge.SettleLookCount;

        _deadlines.Single!.Fire();
        await _bridge.LastSettleDeadlineCheck!;

        Assert.Equal(looksBefore + 1, _bridge.SettleLookCount);
    }

    [Fact]
    public async Task A_look_that_raced_a_fresh_arm_does_not_replace_it()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        _controller.Devices = Array.Empty<RgbDevice>();
        await ResumeAndWaitForBounceAsync(1);
        Advance(TimeSpan.FromSeconds(30));
        // A full fetch still running from the restart would make the look return before it reaches the gate.
        Assert.True(await WaitUntilAsync(() => !_bridge.RefreshInFlight, TimeSpan.FromSeconds(5)));
        var gate = new TaskCompletionSource();
        _controller.AddressGate = gate.Task;
        _deadlines.Single!.Fire();
        Assert.True(await WaitUntilAsync(() => _controller.Count("addresses") >= 1, TimeSpan.FromSeconds(5)));

        // A resume restarts the daemon and arms a fresh deadline while the look is still waiting.
        _bridge.OnSystemResume();
        Assert.True(await WaitUntilAsync(() => _controller.Count("disconnect") == 2 && _controller.IsConnected, TimeSpan.FromSeconds(5)));
        var arms = _deadlines.Single!.ArmCount;
        _controller.AddressGate = null;
        _controller.Fail = true;
        gate.SetResult();
        await _bridge.LastSettleDeadlineCheck!;
        _controller.Fail = false;

        Assert.Equal(arms, _deadlines.Single!.ArmCount);
        Assert.Equal(TimeSpan.FromSeconds(30), _deadlines.Single!.Due);
    }

    [Fact]
    public async Task A_disposed_bridge_does_not_arm_the_settle_deadline_again()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        _controller.Devices = Array.Empty<RgbDevice>();
        await ResumeAndWaitForBounceAsync(1);
        Assert.True(await WaitUntilAsync(() => _deadlines.Single is { ArmCount: 1 }, TimeSpan.FromSeconds(5)));
        var deadline = _deadlines.Single!;

        _bridge.Dispose();
        _bridge.OnSystemResume();
        deadline.Fire();

        Assert.True(deadline.Disposed);
        Assert.Equal(1, deadline.ArmCount);
        Assert.Single(_deadlines.Created);
    }

    [Fact]
    public async Task A_look_in_flight_when_the_bridge_deactivates_arms_nothing()
    {
        UseFakeClock();
        await ActivateAndSettleAsync();
        _controller.Devices = Array.Empty<RgbDevice>();
        await ResumeAndWaitForBounceAsync(1);
        Advance(TimeSpan.FromSeconds(30));
        // A full fetch still running from the restart would make the look return before it reaches the gate.
        Assert.True(await WaitUntilAsync(() => !_bridge.RefreshInFlight, TimeSpan.FromSeconds(5)));
        var gate = new TaskCompletionSource();
        _controller.AddressGate = gate.Task;
        _deadlines.Single!.Fire();
        Assert.True(await WaitUntilAsync(() => _controller.Count("addresses") >= 1, TimeSpan.FromSeconds(5)));
        var arms = _deadlines.Single!.ArmCount;

        _bridge.Deactivate();
        _controller.AddressGate = null;
        gate.SetResult();
        await _bridge.LastSettleDeadlineCheck!;

        Assert.False(_deadlines.Single!.Armed);
        Assert.Equal(arms, _deadlines.Single!.ArmCount);
    }

    [Fact]
    public async Task With_the_settle_stall_recovery_off_a_resume_restart_arms_nothing()
    {
        UseFakeClock();
        _bridge.SettleStallEnabled = false;
        await ActivateAndSettleAsync();

        await ResumeAndWaitForBounceAsync(1);

        Assert.Equal(0, _bridge.ExpectedDevices);
        Assert.Empty(_deadlines.Created);
    }
}
