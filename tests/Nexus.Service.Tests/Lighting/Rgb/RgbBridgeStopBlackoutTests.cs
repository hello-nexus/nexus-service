using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Media;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests.Lighting.Rgb;

/// <summary>
/// Turning lighting off hard-kills the OpenRGB subprocess, so the final black
/// has to be pushed and awaited first - a fire-and-forget push dies in flight
/// on a slow controller (an ENE DRAM module over SMBus) and the hardware keeps
/// its last colour.
/// </summary>
public class RgbBridgeStopBlackoutTests : IDisposable
{
    private sealed record Op(string Kind, RgbColor[]? Colors);

    private sealed class RecordingController : IRgbController
    {
        private readonly List<Op> _ops = new();
        private readonly object _gate = new();

        public bool IsConnected => true;
        public event Action? DeviceListChanged { add { } remove { } }
        public event Action<bool>? DetectionStateChanged { add { } remove { } }
        public event Action? DetectionProgress { add { } remove { } }
        public event Action<int, uint, uint>? WriteRejected { add { } remove { } }
        public async Task<IReadOnlyList<int>> GetControllerAddressesAsync(CancellationToken ct = default) =>
            (await GetDevicesAsync(ct)).Select(d => d.Address).ToList();
        public Task<bool> RescanAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SetSettingsAsync(string key, string valueJson, CancellationToken ct = default) => Task.FromResult(true);

        public IReadOnlyList<RgbDevice> Devices { get; set; } = Array.Empty<RgbDevice>();

        public Op[] Ops
        {
            get { lock (_gate) { return _ops.ToArray(); } }
        }

        public void ResetOps()
        {
            lock (_gate) { _ops.Clear(); }
        }

        private void Record(string kind, RgbColor[]? colors = null)
        {
            lock (_gate) { _ops.Add(new Op(kind, colors)); }
        }

        public Task<bool> TryConnectAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task DisconnectAsync() { Record("disconnect"); return Task.CompletedTask; }

        public Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(CancellationToken ct = default)
        {
            Record("getDevices");
            return Task.FromResult(Devices);
        }

        public Task SetDirectModeAsync(RgbDevice device, CancellationToken ct = default) => Task.CompletedTask;

        public Task PushFrameAsync(int address, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default)
        {
            Record("push", colors.ToArray());
            return Task.CompletedTask;
        }

        public Task SetOffAsync(int address, int ledCount, CancellationToken ct = default) => Task.CompletedTask;
        public Task PushZoneFrameAsync(int address, int zoneIndex, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResizeZoneAsync(int address, int zoneIndex, int newSize, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private readonly string _tempDir;
    private readonly RecordingController _controller = new();
    private readonly LightingEngine _engine = new();
    private readonly RgbBridge _bridge;

    public RgbBridgeStopBlackoutTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-test-stopblackout-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _controller.Devices = new[]
        {
            new RgbDevice { Index = 0, Address = 0, Name = "DRAM", LedCount = 4 },
        };
        // Nonexistent binary: Start() is a no-op and the bridge still activates.
        _bridge = new RgbBridge(
            new OpenRgbProcessManager(Path.Combine(_tempDir, "missing-openrgb")),
            _controller,
            _engine,
            new TestableConfigStore(Path.Combine(_tempDir, "settings.json")),
            new StubUsbEnumerator());
    }

    public void Dispose()
    {
        _bridge.Dispose();
        _bridge.AwaitShutdown();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    /// <summary>Activates and waits for the per-physical frame buffers, NOT just
    /// for <c>Devices</c>. The initial-rescan hold delays the first committed
    /// device list, and the buffers are allocated after it - a blackout can only
    /// push to a physical that already has one, so waiting on Devices alone
    /// races that allocation and intermittently sees zero pushes.</summary>
    private async Task ActivateAndWaitForBuffersAsync()
    {
        _bridge.Activate();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline && _bridge.PhysicalBufferCount == 0)
        {
            await Task.Delay(20);
        }
        Assert.NotEmpty(_bridge.Devices);
        Assert.True(_bridge.PhysicalBufferCount > 0, "per-physical frame buffers were never allocated");
    }

    [Fact]
    public async Task Blackout_pushes_black_to_every_driven_device()
    {
        await ActivateAndWaitForBuffersAsync();
        _controller.ResetOps();

        var count = await _bridge.BlackoutAsync();

        var pushes = _controller.Ops.Where(o => o.Kind == "push").ToArray();
        Assert.NotEmpty(pushes);
        Assert.Equal(count, pushes.Length);
        Assert.All(pushes, p => Assert.All(p.Colors!, c => Assert.Equal(default, c)));
    }

    [Fact]
    public async Task StopAll_blacks_the_devices_out_before_the_subprocess_is_stopped()
    {
        await ActivateAndWaitForBuffersAsync();

        using var gpu = new GpuContext(160, 90);
        using var provider = new LightingProvider(
            new TestableConfigStore(Path.Combine(_tempDir, "provider.json")),
            _engine,
            new LightingOutputHub(),
            gpu,
            new MediaLibrary(),
            new Nexus.Service.Platform.DefaultMonitorEnumerator(),
            rgb: _bridge);

        // A live render loop is the thing the ordering exists to preserve: the
        // blackout has to publish through it, so the effect must be running.
        provider.StartAnimate(new Nexus.Service.Models.Lighting.AnimateHeadlessStart
        { Effect = "plasma", Speed = 50, Intensity = 1f, Saturation = 1f, Contrast = 1f });
        var spin = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < spin && !_engine.LoopPublishing) await Task.Delay(20);
        Assert.True(_engine.LoopPublishing, "the effect must be painting before StopAll");

        // The published frame carries a header, so it is never all-zero; the
        // engine's own hold flag is the signal. OnFrame fires inline on the
        // publishing thread, so seeing the hold engaged on a live loop can only
        // mean the blackout ran before Stop tore that loop down.
        var blackWhilePublishing = false;
        void OnFrame(ReadOnlyMemory<byte> frame)
        {
            if (_engine.Blackout && _engine.LoopPublishing) blackWhilePublishing = true;
        }
        _engine.OnFrame += OnFrame;

        _controller.ResetOps();
        provider.StopAll();
        _engine.OnFrame -= OnFrame;

        var ops = _controller.Ops;
        var disconnect = Array.FindIndex(ops, o => o.Kind == "disconnect");
        Assert.True(disconnect >= 0, "StopAll must disconnect");

        var pushes = ops.Take(disconnect).Where(o => o.Kind == "push").ToArray();
        Assert.NotEmpty(pushes);
        // Earlier pushes are the live effect's own frames - the loop is still
        // painting while StopAll runs. What has to be black is the last thing
        // on the wire before the socket closes.
        Assert.All(pushes[^1].Colors!, c => Assert.Equal(default, c));
        // Ordering: OnFrame fires inline on the publishing thread, so a black
        // frame seen while the loop was alive can only come from a blackout
        // that ran BEFORE Stop. Run it after and the loop is already gone -
        // the shape that left ENE DRAM lit on hardware.
        Assert.True(blackWhilePublishing,
            "the blackout must publish through the engine while the loop is still running");
    }
}
