using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting.Rgb;

/// <summary>The OpenRGB bridge reports what it pushes (before the channel swap) to the dashboard sink.</summary>
public class RgbBridgeLiveLedTests : IDisposable
{
    private sealed class QuietController : IRgbController
    {
        public bool IsConnected => true;
        public event Action? DeviceListChanged { add { } remove { } }
        public event Action<bool>? DetectionStateChanged { add { } remove { } }
        public event Action? DetectionProgress { add { } remove { } }
        public event Action<int, uint, uint>? WriteRejected { add { } remove { } }
        public IReadOnlyList<RgbDevice> Devices { get; set; } = Array.Empty<RgbDevice>();
        public async Task<IReadOnlyList<int>> GetControllerAddressesAsync(CancellationToken ct = default) =>
            (await GetDevicesAsync(ct)).Select(d => d.Address).ToList();
        public Task<bool> RescanAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SetSettingsAsync(string key, string valueJson, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> TryConnectAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(CancellationToken ct = default) => Task.FromResult(Devices);
        public Task SetDirectModeAsync(RgbDevice device, CancellationToken ct = default) => Task.CompletedTask;
        public Task PushFrameAsync(int address, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetOffAsync(int address, int ledCount, CancellationToken ct = default) => Task.CompletedTask;
        public Task PushZoneFrameAsync(int address, int zoneIndex, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResizeZoneAsync(int address, int zoneIndex, int newSize, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private readonly string _tempDir;
    private readonly LightingEngine _engine = new();
    private readonly TestableConfigStore _store;
    private readonly RgbBridge _bridge;

    public RgbBridgeLiveLedTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-test-liveled-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        var controller = new QuietController
        {
            Devices = new[] { new RgbDevice { Index = 0, Address = 0, Name = "DRAM", LedCount = 4 } },
        };
        _store = new TestableConfigStore(Path.Combine(_tempDir, "settings.json"));
        _bridge = new RgbBridge(new OpenRgbProcessManager(Path.Combine(_tempDir, "missing-openrgb")), controller, _engine, _store, new StubUsbEnumerator());
    }

    public void Dispose()
    {
        _bridge.Dispose();
        _bridge.AwaitShutdown();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private async Task<DeviceFrame> ActivateAsync()
    {
        _bridge.Activate();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline && (_bridge.PhysicalBufferCount == 0 || _engine.Devices.Length == 0))
        {
            await Task.Delay(20);
        }
        Assert.True(_bridge.PhysicalBufferCount > 0);
        var frame = _engine.Devices[0];
        for (var i = 0; i < frame.LedCount; i++)
        {
            frame.SetLed(i, (byte)(200 - i * 20), (byte)(100 + i * 10), (byte)(40 + i));
        }
        frame.Publish();
        return frame;
    }

    // A v3 frame by hand: a one-pixel canvas, then each device's LED section.
    private static ReadOnlyMemory<byte> Wire(DeviceFrame[] devices)
    {
        var bytes = new List<byte> { 0x03, 1, 0, 1, 0, 0, 0, 0, (byte)devices.Length };
        foreach (var d in devices)
        {
            bytes.Add(DeviceFrameIndex.WireByte(d));
            bytes.Add((byte)(d.LedCount & 0xFF));
            bytes.Add((byte)(d.LedCount >> 8));
            bytes.AddRange(d.LedBytes.ToArray());
        }
        return bytes.ToArray();
    }

    private void Push() => _bridge.OnFrame(Wire(_engine.Devices));

    private static byte[] Expected(DeviceFrame frame, double mul, DeviceColorAdjust adjust)
    {
        var bytes = new byte[frame.LedCount * 3];
        LiveLedSink.Compute(bytes, frame.LedBytes, frame.LedCount, mul, adjust);
        return bytes;
    }

    [Fact]
    public async Task Dashboard_bytes_are_brightness_and_trim_without_the_order_swap()
    {
        var frame = await ActivateAsync();
        _store.Update(s => s.Devices.LightingDevicePrefs[frame.Id] = new LightingDevicePreference
        {
            Brightness = 60,
            AdjustRed = 1.2f,
            AdjustBlue = 0.7f,
            ColorOrder = "GRB",
        });
        var settings = _store.Load();
        var pref = settings.Devices.LightingDevicePrefs[frame.Id];
        var mul = MasterBrightness.Resolve(settings).Combine(0.6);

        _engine.Live.Enabled = true;
        Push();

        Assert.Equal(Expected(frame, mul, DeviceColorAdjust.For(pref)), _engine.Live.TryGetFresh(frame.Id));
    }

    [Fact]
    public async Task Disabled_device_reports_black_and_uncontrolled_device_reports_nothing()
    {
        var frame = await ActivateAsync();
        _engine.Live.Enabled = true;

        _store.Update(s => s.Devices.DisabledLightingDevices = new List<string> { frame.Id });
        Push();
        Assert.Equal(new byte[frame.LedCount * 3], _engine.Live.TryGetFresh(frame.Id));

        _store.Update(s =>
        {
            s.Devices.DisabledLightingDevices = new List<string>();
            s.Devices.UncontrolledLightingDevices = new List<string> { frame.Id };
        });
        Push();
        Assert.Null(_engine.Live.TryGetFresh(frame.Id));
    }

    [Fact]
    public async Task A_deduped_push_keeps_the_last_pushed_output_on_show()
    {
        var frame = await ActivateAsync();
        var clock = 0L;
        _engine.Live.Clock = () => clock;
        _engine.Live.Enabled = true;

        Push();
        var pushed = _engine.Live.TryGetFresh(frame.Id);
        Assert.NotNull(pushed);

        // The same frame again is deduped, so nothing is sent; the touch keeps the last push fresh past the window.
        clock = 400;
        Push();
        clock = 800;
        Assert.Equal(pushed, _engine.Live.TryGetFresh(frame.Id));
    }

    [Fact]
    public async Task A_rate_capped_push_does_not_publish_the_bytes_it_skipped()
    {
        var frame = await ActivateAsync();
        _engine.Live.Enabled = true;
        _store.Update(s => s.Devices.LightingMaxRates[frame.Id] = 1);

        // The first frame passes the cap and publishes; the next one inside the gap is held back.
        frame.SetLed(0, 1, 2, 3);
        frame.Publish();
        Push();
        var pushed = _engine.Live.TryGetFresh(frame.Id);
        Assert.Equal(new byte[] { 1, 2, 3 }, pushed![..3]);

        frame.SetLed(0, 9, 9, 9);
        frame.Publish();
        Push();

        Assert.Equal(pushed, _engine.Live.TryGetFresh(frame.Id));
    }

    [Fact]
    public async Task A_deduped_frame_commits_what_the_device_already_shows()
    {
        var frame = await ActivateAsync();
        _engine.Live.Enabled = true;
        Push();

        // A dashboard session starting later sees an identical frame deduped; it still gets the color, not grey.
        _engine.Live.ReportNone(frame.Id);
        Assert.Null(_engine.Live.TryGetFresh(frame.Id));
        Push();
        Assert.NotNull(_engine.Live.TryGetFresh(frame.Id));
    }
}
