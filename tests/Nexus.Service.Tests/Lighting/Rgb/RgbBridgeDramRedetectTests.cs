using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Persistence;
using Nexus.Service.Tests.Common;

namespace Nexus.Service.Tests.Lighting.Rgb;

/// <summary>The missing-DRAM re-detect series driven through the bridge with hand-fired deadlines; no daemon, no hardware.</summary>
public class RgbBridgeDramRedetectTests : IDisposable
{
    private const string DimmId = "openrgb-l-I2C__PawnIO_SMBus_i801_0__address_0x71";
    private const string DimmLocation = "I2C: PawnIO SMBus i801 0: address 0x71";

    private sealed class Controller : IRgbController
    {
        private int _rescans;
        public volatile IReadOnlyList<RgbDevice> Devices = Array.Empty<RgbDevice>();
        public int Rescans => Volatile.Read(ref _rescans);
        public bool IsConnected { get; private set; }
        public event Action? DeviceListChanged { add { } remove { } }
        public event Action<bool>? DetectionStateChanged;
        public event Action? DetectionProgress { add { } remove { } }
        public event Action<int, uint, uint>? WriteRejected { add { } remove { } }
        public void StartDetection() => DetectionStateChanged?.Invoke(true);
        public void CompleteDetection() => DetectionStateChanged?.Invoke(false);
        public Task<bool> RescanAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _rescans);
            return Task.FromResult(true);
        }
        public Task<bool> SetSettingsAsync(string key, string valueJson, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> TryConnectAsync(CancellationToken ct = default)
        {
            IsConnected = true;
            return Task.FromResult(true);
        }
        public Task DisconnectAsync()
        {
            IsConnected = false;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<int>> GetControllerAddressesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<int>>(Devices.Select(d => d.Address).ToList());
        public Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(CancellationToken ct = default) => Task.FromResult(Devices);
        public Task SetDirectModeAsync(RgbDevice device, CancellationToken ct = default) => Task.CompletedTask;
        public Task PushFrameAsync(int address, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetOffAsync(int address, int ledCount, CancellationToken ct = default) => Task.CompletedTask;
        public Task PushZoneFrameAsync(int address, int zoneIndex, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResizeZoneAsync(int address, int zoneIndex, int newSize, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static readonly RgbDevice Mouse = new() { Index = 0, Address = 0, Name = "Mouse", Type = 6, LedCount = 8, Serial = "M1" };

    private static RgbDevice Dimm() => new() { Index = 1, Address = 1, Name = "ENE DRAM", Type = 1, LedCount = 8, Location = DimmLocation };

    private readonly string _tempDir;
    private readonly Controller _controller = new();
    private readonly TestableConfigStore _store;
    private readonly DeviceControlGate _gate;
    private readonly RgbBridge _bridge;
    private readonly FakeOneShot.Factory _deadlines = new();

    public RgbBridgeDramRedetectTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-test-dram-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new TestableConfigStore(Path.Combine(_tempDir, "settings.json"));
        _gate = new DeviceControlGate(_store);
        _controller.Devices = new[] { Mouse };
        _bridge = new RgbBridge(
            new OpenRgbProcessManager(Path.Combine(_tempDir, "missing-openrgb")),
            _controller,
            new LightingEngine(),
            _store,
            new StubUsbEnumerator(),
            controlGate: _gate);
        _bridge.DaemonUptimeProbe = () => TimeSpan.FromMinutes(5);
        _bridge.OneShotFactory = _deadlines.Create;
    }

    public void Dispose()
    {
        _bridge.Dispose();
        _bridge.AwaitShutdown();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private void KnowFromAnEarlierBuild(params string[] cardIds)
        => _store.Update(s => s.Devices.MappingKnownDevices.AddRange(cardIds));

    private async Task ActivateAndSettleAsync()
    {
        _bridge.Activate();
        Assert.True(await WaitUntilAsync(() => _bridge.Devices.Count > 0, TimeSpan.FromSeconds(20)));
        _controller.CompleteDetection();
        await Task.Delay(300);
    }

    /// <summary>The list a finished detection pass leaves, committed as settled.</summary>
    private async Task SettleWithAsync(params RgbDevice[] devices)
    {
        _controller.Devices = devices;
        _controller.CompleteDetection();
        _controller.CompleteDetection();
        Assert.True(await WaitUntilAsync(() => _bridge.Devices.Count == devices.Length, TimeSpan.FromSeconds(10)));
        await Task.Delay(300);
    }

    private FakeOneShot? ArmedPass(int index)
        => _deadlines.Created.FirstOrDefault(o => o.Armed && o.Due == DramRedetectPlan.Delays[index]);

    private static async Task<bool> WaitUntilAsync(Func<bool> cond, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (cond())
            {
                return true;
            }
            await Task.Delay(30);
        }
        return cond();
    }

    private async Task FirePassAsync(FakeOneShot timer, int rescansBefore)
    {
        timer.Fire();
        Assert.True(await WaitUntilAsync(() => _controller.Rescans > rescansBefore, TimeSpan.FromSeconds(5)), "the pass never rescanned");
    }

    [Fact]
    public async Task A_known_missing_device_arms_the_first_pass_and_firing_it_rescans()
    {
        KnowFromAnEarlierBuild(DimmId);
        await ActivateAndSettleAsync();

        var timer = ArmedPass(0);
        Assert.NotNull(timer);
        await FirePassAsync(timer!, 0);
        Assert.Equal(1, _controller.Rescans);
    }

    [Fact]
    public async Task Nothing_known_arms_nothing()
    {
        await ActivateAndSettleAsync();

        Assert.Null(_deadlines.Created.FirstOrDefault(o => DramRedetectPlan.Delays.Contains(o.Due) && o.Armed));
        Assert.Equal(0, _controller.Rescans);
    }

    [Fact]
    public async Task A_split_board_card_id_does_not_count_as_known_missing()
    {
        // A board with no RAM: card ids carry a zone suffix, and the board shows up as a motherboard, never as DRAM.
        KnowFromAnEarlierBuild("openrgb-l-I2C__PawnIO_SMBus_i801_0__address_0x40-3", "openrgb-l-I2C__PawnIO_SMBus_i801_0__address_0x40:z1");
        var board = new RgbDevice { Index = 1, Address = 1, Name = "ASUS Aura", Type = 0, LedCount = 8, Location = "I2C: PawnIO SMBus i801 0: address 0x40" };
        _controller.Devices = new[] { Mouse, board };
        await ActivateAndSettleAsync();

        Assert.Null(_deadlines.Created.FirstOrDefault(o => DramRedetectPlan.Delays.Contains(o.Due) && o.Armed));
        Assert.Equal(0, _controller.Rescans);
    }

    [Fact]
    public async Task A_user_excluded_device_is_not_chased()
    {
        KnowFromAnEarlierBuild(DimmId);
        _store.Update(s =>
        {
            s.Devices.OpenRgbDetectorExclusions[DimmId] = new OpenRgbDetectorExclusion { DetectorName = "ENE" };
            s.Devices.UncontrolledLightingDevices.Add(DimmId);
        });
        await ActivateAndSettleAsync();

        Assert.Null(_deadlines.Created.FirstOrDefault(o => DramRedetectPlan.Delays.Contains(o.Due) && o.Armed));
    }

    [Fact]
    public async Task A_pass_that_finds_the_device_ends_the_series_and_a_later_loss_starts_over()
    {
        KnowFromAnEarlierBuild(DimmId);
        await ActivateAndSettleAsync();
        await FirePassAsync(ArmedPass(0)!, 0);

        await SettleWithAsync(Mouse, Dimm());

        Assert.Null(_deadlines.Created.FirstOrDefault(o => DramRedetectPlan.Delays.Contains(o.Due) && o.Armed));
        Assert.Equal(0, _store.Load().Devices.OpenRgbKnownDram[DimmId]);

        await SettleWithAsync(Mouse);

        Assert.NotNull(ArmedPass(0));
    }

    [Fact]
    public async Task Passes_stop_at_the_cap_and_the_missed_session_is_counted()
    {
        KnowFromAnEarlierBuild(DimmId);
        await ActivateAndSettleAsync();

        for (var pass = 0; pass < DramRedetectPlan.Delays.Length; pass++)
        {
            var timer = ArmedPass(pass);
            Assert.NotNull(timer);
            await FirePassAsync(timer!, pass);
            await SettleWithAsync(Mouse);
        }

        Assert.Equal(DramRedetectPlan.Delays.Length, _controller.Rescans);
        Assert.Null(_deadlines.Created.FirstOrDefault(o => DramRedetectPlan.Delays.Contains(o.Due) && o.Armed));
        Assert.Equal(1, _store.Load().Devices.OpenRgbKnownDram[DimmId]);
    }

    [Fact]
    public async Task A_busy_daemon_retries_shortly_without_spending_the_pass()
    {
        KnowFromAnEarlierBuild(DimmId);
        await ActivateAndSettleAsync();
        var timer = ArmedPass(0)!;

        _controller.StartDetection();
        timer.Fire();

        Assert.Equal(0, _controller.Rescans);
        Assert.True(timer.Armed);
        Assert.Equal(TimeSpan.FromSeconds(30), timer.Due);
    }

    [Fact]
    public async Task A_pass_skipped_for_shutdown_is_not_spent()
    {
        KnowFromAnEarlierBuild(DimmId);
        await ActivateAndSettleAsync();
        var timer = ArmedPass(0)!;

        _bridge.OsShuttingDown = () => true;
        timer.Fire();
        Assert.Equal(0, _controller.Rescans);

        _bridge.OsShuttingDown = () => false;
        await SettleWithAsync(Mouse);

        Assert.NotNull(ArmedPass(0));
    }

    [Fact]
    public async Task Turning_the_memory_gate_off_cancels_an_armed_pass()
    {
        KnowFromAnEarlierBuild(DimmId);
        await ActivateAndSettleAsync();
        var timer = ArmedPass(0)!;

        // The toggle itself re-detects (the daemon's denylist changes); only the series' own pass is under test.
        _gate.SetEnabled(SmbusDramHandler.HandlerId, false);
        Assert.True(await WaitUntilAsync(() => _controller.Rescans == 1, TimeSpan.FromSeconds(5)));
        timer.Fire();
        await Task.Delay(200);

        Assert.Equal(1, _controller.Rescans);
    }

    [Fact]
    public async Task A_disposed_bridge_runs_no_pass()
    {
        KnowFromAnEarlierBuild(DimmId);
        await ActivateAndSettleAsync();
        var timer = ArmedPass(0)!;

        _bridge.Dispose();
        timer.Fire();
        await Task.Delay(200);

        Assert.True(timer.Disposed);
        Assert.Equal(0, _controller.Rescans);
    }
}
