using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting.Smart;
using Nexus.Service.Lighting.Smart.Drivers.Govee;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.SmartLights;

/// <summary>End-to-end driver tests against the loopback Govee emulator:
/// discover → pair → control → razer streaming, no hardware.</summary>
public class GoveeDriverTests : IDisposable
{
    private readonly FakeGoveeDevice _device = new();
    private readonly GoveeLanClient _client;
    private readonly GoveeDriver _driver;

    public GoveeDriverTests()
    {
        // Point both the scan target and the control port at the emulator;
        // listen on an ephemeral port (the emulator replies to the source).
        _client = new GoveeLanClient(
            scanEndpoint: new IPEndPoint(IPAddress.Loopback, _device.Port),
            listenPort: 0,
            controlPort: _device.Port);
        // Every "unreachable" answer on the Govee LAN API is a timeout, so the
        // production windows would be paid in full by the discovery and
        // pair-failure cases against a loopback emulator.
        _driver = new GoveeDriver(_client, scanTimeoutMs: 250, probeTimeoutMs: 250, pingTimeoutMs: 250);
    }

    private SmartLight PairedDevice(string extra) => new()
    {
        Id = $"govee:{_device.DeviceId}",
        Brand = "govee",
        Name = "Govee H619A",
        Host = "127.0.0.1",
        StableKey = _device.DeviceId,
        Token = "",
        Extra = extra,
    };

    [Fact]
    public async Task Discover_findsEmulatedDevice()
    {
        var found = await _driver.DiscoverAsync(CancellationToken.None);
        var dev = Assert.Single(found);
        Assert.Equal("govee", dev.Brand);
        Assert.Equal("127.0.0.1", dev.Host);
        Assert.Equal("Govee H619A", dev.Name);
        Assert.Equal(_device.DeviceId, dev.StableKey);
    }

    [Fact]
    public async Task Pair_capturesSkuCapabilities()
    {
        var result = await _driver.PairAsync(
            new DiscoveredLight("govee", "127.0.0.1", "Govee H619A", _device.DeviceId), CancellationToken.None);

        Assert.True(result.Ok);
        var cfg = Assert.Single(result.Devices);
        Assert.Equal($"govee:{_device.DeviceId}", cfg.Id);
        Assert.Equal(_device.DeviceId, cfg.StableKey);

        var extra = JsonSerializer.Deserialize(cfg.Extra, GoveeJsonContext.Default.GoveeExtra)!;
        Assert.Equal("H619A", extra.Sku);
        Assert.True(extra.Razer);
        Assert.Equal(20, extra.Segments);

        var plan = _driver.PlanFrames(PairedDevice(cfg.Extra));
        Assert.Equal(20, plan.LedCount);
        Assert.False(plan.AverageToSingle);
    }

    [Fact]
    public async Task Pair_statusOnlyFallback_defaultsToControlOnly()
    {
        // Scan filtered but devStatus answers: pairing succeeds without a SKU,
        // and the unknown device must NOT be assumed razer-capable (a bulb fed
        // razer packets it ignores would show dead effects).
        _device.ScanEnabled = false;
        var result = await _driver.PairAsync(
            new DiscoveredLight("govee", "127.0.0.1", "Govee", _device.DeviceId), CancellationToken.None);

        Assert.True(result.Ok);
        var cfg = Assert.Single(result.Devices);
        var extra = JsonSerializer.Deserialize(cfg.Extra, GoveeJsonContext.Default.GoveeExtra)!;
        Assert.Equal("", extra.Sku);
        Assert.False(extra.Razer);
        Assert.True(_driver.PlanFrames(PairedDevice(cfg.Extra)).AverageToSingle);
    }

    [Fact]
    public async Task Pair_failsWhenLanControlDisabled()
    {
        _device.LanEnabled = false;
        var result = await _driver.PairAsync(
            new DiscoveredLight("govee", "127.0.0.1", "Govee", _device.DeviceId), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal("lan-control", result.Error);
    }

    [Fact]
    public async Task Send_static_turnsBrightensAndColors()
    {
        var dev = PairedDevice("""{"sku":"H619A","segments":20,"razer":true}""");
        await _driver.SendAsync(dev, new LightFrame(On: true, 255, 0, 0, 0.5f), CancellationToken.None);
        await TestWait.ForAsync(() => !_device.Colors.IsEmpty);

        Assert.Equal(new[] { true }, _device.Turns.ToArray());
        Assert.Equal(new[] { 50 }, _device.Brightnesses.ToArray());
        var (r, g, b, kelvin) = _device.Colors.Single();
        Assert.Equal((255, 0, 0, 0), (r, g, b, kelvin));

        await _driver.SendAsync(dev, new LightFrame(On: false, 0, 0, 0, 0f), CancellationToken.None);
        await TestWait.ForAsync(() => _device.Turns.Count == 2);
        Assert.False(_device.Turns.Last());
    }

    [Fact]
    public async Task Send_zones_entersRazerModeAndStreams_thenStaticExits()
    {
        var dev = PairedDevice("""{"sku":"H619A","segments":20,"razer":true}""");
        var zones = new byte[20 * 3];
        for (var i = 0; i < 20; i++) { zones[i * 3] = (byte)(i * 10); zones[i * 3 + 2] = 255; }

        await _driver.SendAsync(dev, new LightFrame(On: true, 0, 0, 255, 1f, zones), CancellationToken.None);
        await TestWait.ForAsync(() => _device.RazerPackets.Count >= 2);

        var packets = _device.RazerPackets.ToArray();
        Assert.Equal(GoveePackets.BuildRazerMode(enable: true), packets[0]);
        var frame = packets[1];
        Assert.Equal(0xB0, frame[3]);
        Assert.Equal(20, frame[5]);
        Assert.Equal(0, frame[6]);        // zone 0 red channel
        Assert.Equal(255, frame[8]);      // zone 0 blue channel
        Assert.Equal(190, frame[6 + 19 * 3]); // zone 19 red channel

        // Second zone frame must NOT re-send the enable packet.
        await _driver.SendAsync(dev, new LightFrame(On: true, 0, 0, 255, 1f, zones), CancellationToken.None);
        await TestWait.ForAsync(() => _device.RazerPackets.Count >= 3);
        Assert.Equal(0xB0, _device.RazerPackets.ToArray()[2][3]);

        // A static frame (effect stopped) leaves razer mode before colorwc.
        await _driver.SendAsync(dev, new LightFrame(On: true, 10, 20, 30, 1f), CancellationToken.None);
        await TestWait.ForAsync(() => !_device.Colors.IsEmpty);
        Assert.Equal(GoveePackets.BuildRazerMode(enable: false), _device.RazerPackets.Last());
    }

    [Fact]
    public async Task FullStack_pairThroughProvider_streamsToEmulator()
    {
        // The complete no-hardware path: provider pair → frame plan → effect
        // frame through the throttle → razer packets on the wire.
        var dir = Path.Combine(Path.GetTempPath(), "nexus-gv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var throttle = new NetworkSendThrottle();
        try
        {
            var store = new JsonConfigStore(Path.Combine(dir, "settings.json"));
            var provider = new SmartLightProvider(new ILightDriver[] { _driver }, store, throttle);

            var pair = await provider.PairAsync(new Nexus.Service.Models.SmartLights.PairSmartLightBody
            {
                Brand = "govee",
                Host = "127.0.0.1",
                StableKey = _device.DeviceId,
                Name = "Govee H619A",
            }, CancellationToken.None);
            Assert.True(pair.Ok, pair.Message);
            provider.SetBrandEnabled("govee", true); // brands default off; this one drives the canvas

            var frame = Assert.Single(provider.BuildFrames(0));
            Assert.Equal(20, frame.LedCount);

            var zones = new byte[20 * 3];
            for (var i = 0; i < zones.Length; i++) zones[i] = (byte)i;
            provider.SubmitEffectFrame(frame.Id, zones, 20, 1f);

            await TestWait.ForAsync(() => _device.RazerPackets.Count >= 2);
            var packets = _device.RazerPackets.ToArray();
            Assert.Equal(GoveePackets.BuildRazerMode(enable: true), packets[0]);
            Assert.Equal(GoveePackets.BuildRazerFrame(zones, 20, 1f, gradient: true), packets[1]);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SetColor_onRazerDevice_streamsRazerFramesNotColorwc()
    {
        // The color-route path: a solid color on a razer strip must take over via
        // the razer stream - single-color colorwc can't override its built-in scene.
        var dir = Path.Combine(Path.GetTempPath(), "nexus-gv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var throttle = new NetworkSendThrottle();
        try
        {
            var store = new JsonConfigStore(Path.Combine(dir, "settings.json"));
            var provider = new SmartLightProvider(new ILightDriver[] { _driver }, store, throttle);
            var pair = await provider.PairAsync(new Nexus.Service.Models.SmartLights.PairSmartLightBody
            {
                Brand = "govee", Host = "127.0.0.1", StableKey = _device.DeviceId, Name = "Govee H619A",
            }, CancellationToken.None);
            Assert.True(pair.Ok, pair.Message);
            var id = $"govee:{_device.DeviceId}";

            provider.SetHue(id, 0.33f);
            provider.SetSaturation(id, 1f);

            await TestWait.ForAsync(() => _device.RazerPackets.Count >= 2);
            var packets = _device.RazerPackets.ToArray();
            Assert.Equal(GoveePackets.BuildRazerMode(enable: true), packets[0]);
            Assert.Equal(0xB0, packets[1][3]);     // per-segment color frame
            Assert.Equal(20, packets[1][5]);       // all 20 segments
            Assert.True(_device.Colors.IsEmpty);   // never fell back to colorwc
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task MaintainStreamedStatic_rePushesToKeepTakeoverAlive()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-gv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var throttle = new NetworkSendThrottle();
        try
        {
            var store = new JsonConfigStore(Path.Combine(dir, "settings.json"));
            var provider = new SmartLightProvider(new ILightDriver[] { _driver }, store, throttle);
            var pair = await provider.PairAsync(new Nexus.Service.Models.SmartLights.PairSmartLightBody
            {
                Brand = "govee", Host = "127.0.0.1", StableKey = _device.DeviceId, Name = "Govee H619A",
            }, CancellationToken.None);
            Assert.True(pair.Ok, pair.Message);
            var id = $"govee:{_device.DeviceId}";

            provider.SetHue(id, 0.5f);
            await TestWait.ForAsync(() => _device.RazerPackets.Count >= 2);
            var before = _device.RazerPackets.Count;

            provider.MaintainStreamedStatic();
            await TestWait.ForAsync(() => _device.RazerPackets.Count > before);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SetEnabled_false_stopsStreamingDisabledDevice()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-gv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var throttle = new NetworkSendThrottle();
        try
        {
            var store = new JsonConfigStore(Path.Combine(dir, "settings.json"));
            var provider = new SmartLightProvider(new ILightDriver[] { _driver }, store, throttle);
            var pair = await provider.PairAsync(new Nexus.Service.Models.SmartLights.PairSmartLightBody
            {
                Brand = "govee", Host = "127.0.0.1", StableKey = _device.DeviceId, Name = "Govee H619A",
            }, CancellationToken.None);
            Assert.True(pair.Ok, pair.Message);
            var id = $"govee:{_device.DeviceId}";

            provider.SetHue(id, 0.5f);
            await TestWait.ForAsync(() => _device.RazerPackets.Count >= 2);

            provider.SetEnabled(id, false);
            await Task.Delay(150);                  // let any in-flight send drain
            var afterDisable = _device.RazerPackets.Count;
            provider.MaintainStreamedStatic();      // must not re-stream a disabled light
            await Task.Delay(150);
            Assert.Equal(afterDisable, _device.RazerPackets.Count);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private int EnableCount() =>
        _device.RazerPackets.ToArray().Count(p => p.AsSpan().SequenceEqual(GoveePackets.BuildRazerMode(enable: true)));

    private const string RazerExtra = """{"sku":"H619A","segments":20,"razer":true}""";
    private static readonly LightFrame ZoneFrame = new(On: true, 0, 0, 255, 1f, new byte[20 * 3]);

    private GoveeDriver FastDriver(int frameGapMs = 30_000, int probeMs = 10_000)
        => new(_client, 250, 250, 250, frameGapMs, probeMs);

    private static Task PassTimeAsync(int ms)
    {
        var until = Environment.TickCount64 + ms;
        return TestWait.ForAsync(() => Environment.TickCount64 >= until, ms + 2_000);
    }

    [Fact]
    public async Task Send_zones_steadyStreaming_sendsExactlyOneEnable()
    {
        var dev = PairedDevice(RazerExtra);
        for (var i = 0; i < 40; i++)
        {
            await _driver.SendAsync(dev, ZoneFrame, CancellationToken.None);
            await PassTimeAsync(100);
        }
        await TestWait.ForAsync(() => _device.RazerPackets.Count >= 41);
        Assert.Equal(1, EnableCount());
        Assert.Single(_device.Turns);
    }

    [Fact]
    public async Task Send_zones_afterAFrameGap_entersAgain()
    {
        var driver = FastDriver(frameGapMs: 1_500);
        var dev = PairedDevice(RazerExtra);
        await driver.SendAsync(dev, ZoneFrame, CancellationToken.None);
        await driver.SendAsync(dev, ZoneFrame, CancellationToken.None);
        await TestWait.ForAsync(() => EnableCount() == 1 && _device.Turns.Count == 1);

        await PassTimeAsync(1_700);
        await driver.SendAsync(dev, ZoneFrame, CancellationToken.None);
        await TestWait.ForAsync(() => EnableCount() == 2 && _device.Turns.Count == 2);
    }

    [Fact]
    public async Task MissesWhileArmed_neverReEnable()
    {
        var driver = FastDriver(probeMs: 100);
        var dev = PairedDevice(RazerExtra);
        await driver.SendAsync(dev, ZoneFrame, CancellationToken.None);
        Assert.True(await driver.PingAsync(dev, CancellationToken.None));

        _device.LanEnabled = false;
        for (var i = 0; i < 4; i++)
        {
            Assert.False(await driver.PingAsync(dev, CancellationToken.None));
            await driver.SendAsync(dev, ZoneFrame, CancellationToken.None);
        }
        await PassTimeAsync(600);   // driver probes miss too
        await driver.SendAsync(dev, ZoneFrame, CancellationToken.None);

        Assert.Single(_device.Turns);
        Assert.Equal(1, EnableCount());
    }

    [Fact]
    public async Task NeverReplyingStrip_isNeverColdEnteredOnMisses()
    {
        var driver = FastDriver(probeMs: 100);
        var dev = PairedDevice(RazerExtra);
        _device.LanEnabled = false;   // controls are still recorded, status never answers
        await driver.SendAsync(dev, ZoneFrame, CancellationToken.None);

        // Many probe cycles and ping misses.
        var until = Environment.TickCount64 + 2_500;
        while (Environment.TickCount64 < until)
        {
            await driver.SendAsync(dev, ZoneFrame, CancellationToken.None);
            Assert.False(await driver.PingAsync(dev, CancellationToken.None));
        }
        Assert.True(_device.StatusQueries >= 6);
        Assert.Single(_device.Turns);
        Assert.Equal(1, EnableCount());
    }

    [Fact]
    public async Task Ping_missThenReply_doesNotColdEnter()
    {
        var driver = FastDriver();
        var dev = PairedDevice(RazerExtra);
        await driver.SendAsync(dev, ZoneFrame, CancellationToken.None);
        Assert.True(await driver.PingAsync(dev, CancellationToken.None));

        _device.LanEnabled = false;
        Assert.False(await driver.PingAsync(dev, CancellationToken.None));
        _device.LanEnabled = true;
        Assert.True(await driver.PingAsync(dev, CancellationToken.None));
        // A later single miss is a new streak, still below the threshold.
        _device.LanEnabled = false;
        Assert.False(await driver.PingAsync(dev, CancellationToken.None));
        _device.LanEnabled = true;
        var packets = _device.RazerPackets.Count;
        await driver.SendAsync(dev, ZoneFrame, CancellationToken.None);
        await TestWait.ForAsync(() => _device.RazerPackets.Count > packets);
        Assert.Single(_device.Turns);
        Assert.Equal(1, EnableCount());
    }

    [Fact]
    public async Task Ping_offStatus_suppressesFramesEvenAcrossAGap()
    {
        var driver = FastDriver(frameGapMs: 600, probeMs: 100);
        var dev = PairedDevice(RazerExtra);
        await driver.SendAsync(dev, ZoneFrame, CancellationToken.None);
        await TestWait.ForAsync(() => _device.RazerPackets.Count >= 2);

        _device.OnOff = false;
        Assert.True(await driver.PingAsync(dev, CancellationToken.None));
        Assert.True(await driver.PingAsync(dev, CancellationToken.None));
        var packets = _device.RazerPackets.Count;
        await driver.SendAsync(dev, ZoneFrame, CancellationToken.None);

        // No frames for longer than the gap, then another frame.
        await PassTimeAsync(1_000);
        await driver.SendAsync(dev, ZoneFrame, CancellationToken.None);
        await PassTimeAsync(300);
        Assert.Single(_device.Turns);
        Assert.Equal(1, EnableCount());
        Assert.Equal(packets, _device.RazerPackets.Count);
    }

    [Fact]
    public async Task Suppressed_missesNeverEndTheSuppression()
    {
        var driver = FastDriver();
        var dev = PairedDevice(RazerExtra);
        await driver.SendAsync(dev, ZoneFrame, CancellationToken.None);
        _device.OnOff = false;
        Assert.True(await driver.PingAsync(dev, CancellationToken.None));
        Assert.True(await driver.PingAsync(dev, CancellationToken.None));

        _device.LanEnabled = false;
        Assert.False(await driver.PingAsync(dev, CancellationToken.None));
        Assert.False(await driver.PingAsync(dev, CancellationToken.None));
        Assert.False(await driver.PingAsync(dev, CancellationToken.None));
        _device.LanEnabled = true;
        var packets = _device.RazerPackets.Count;
        await driver.SendAsync(dev, ZoneFrame, CancellationToken.None);
        await PassTimeAsync(300);
        Assert.Single(_device.Turns);
        Assert.Equal(packets, _device.RazerPackets.Count);

        _device.OnOff = true;
        Assert.True(await driver.PingAsync(dev, CancellationToken.None));
        await driver.SendAsync(dev, ZoneFrame, CancellationToken.None);
        await TestWait.ForAsync(() => _device.Turns.Count == 2 && EnableCount() == 2);
    }

    [Fact]
    public async Task Suppressed_idleProbeStops_andARestartedFrameRestartsIt()
    {
        var driver = FastDriver(frameGapMs: 400, probeMs: 100);
        var dev = PairedDevice(RazerExtra);
        await driver.SendAsync(dev, ZoneFrame, CancellationToken.None);
        _device.OnOff = false;
        await TestWait.ForAsync(() => _device.StatusQueries >= 2);
        await driver.SendAsync(dev, ZoneFrame, CancellationToken.None);   // probes have seen off

        // Idle past the gap: the probe stops querying.
        await PassTimeAsync(1_000);
        var settled = _device.StatusQueries;
        await PassTimeAsync(500);
        Assert.Equal(settled, _device.StatusQueries);
        Assert.Single(_device.Turns);

        // A new frame restarts the probe, which notices the strip back on.
        await driver.SendAsync(dev, ZoneFrame, CancellationToken.None);
        _device.OnOff = true;
        var deadline = Environment.TickCount64 + 5_000;
        while (_device.Turns.Count < 2 && Environment.TickCount64 < deadline)
        {
            await driver.SendAsync(dev, ZoneFrame, CancellationToken.None);
            await PassTimeAsync(20);
        }
        await TestWait.ForAsync(() => _device.Turns.Count == 2 && EnableCount() == 2);
    }

    [Fact]
    public async Task Ping_onStatusWhileSuppressed_resumesWithAColdEntry()
    {
        var driver = FastDriver();
        var dev = PairedDevice(RazerExtra);
        await driver.SendAsync(dev, ZoneFrame, CancellationToken.None);

        _device.OnOff = false;
        Assert.True(await driver.PingAsync(dev, CancellationToken.None));
        Assert.True(await driver.PingAsync(dev, CancellationToken.None));
        await driver.SendAsync(dev, ZoneFrame, CancellationToken.None);
        Assert.Single(_device.Turns);

        _device.OnOff = true;
        Assert.True(await driver.PingAsync(dev, CancellationToken.None));
        await driver.SendAsync(dev, ZoneFrame, CancellationToken.None);
        await TestWait.ForAsync(() => EnableCount() == 2 && _device.Turns.Count == 2);
    }

    [Fact]
    public async Task Ping_singleOffReply_doesNotSuppress_twoDo()
    {
        var driver = FastDriver();
        var dev = PairedDevice(RazerExtra);
        await driver.SendAsync(dev, ZoneFrame, CancellationToken.None);

        // One off reply, then an on reply: the count restarts.
        _device.OnOff = false;
        Assert.True(await driver.PingAsync(dev, CancellationToken.None));
        _device.OnOff = true;
        Assert.True(await driver.PingAsync(dev, CancellationToken.None));
        _device.OnOff = false;
        Assert.True(await driver.PingAsync(dev, CancellationToken.None));
        var packets = _device.RazerPackets.Count;
        await driver.SendAsync(dev, ZoneFrame, CancellationToken.None);
        await TestWait.ForAsync(() => _device.RazerPackets.Count > packets);
        Assert.Single(_device.Turns);

        // A second consecutive off reply suppresses: frames stop.
        Assert.True(await driver.PingAsync(dev, CancellationToken.None));
        packets = _device.RazerPackets.Count;
        await driver.SendAsync(dev, ZoneFrame, CancellationToken.None);
        await PassTimeAsync(300);
        Assert.Equal(packets, _device.RazerPackets.Count);
        Assert.Single(_device.Turns);
    }

    [Fact]
    public async Task DriverProbe_noticesTheStripComingBackOn()
    {
        var driver = FastDriver(probeMs: 100);
        var dev = PairedDevice(RazerExtra);
        await driver.SendAsync(dev, ZoneFrame, CancellationToken.None);

        _device.OnOff = false;
        await PassTimeAsync(500);   // probes see off
        await driver.SendAsync(dev, ZoneFrame, CancellationToken.None);
        Assert.Single(_device.Turns);

        _device.OnOff = true;
        var deadline = Environment.TickCount64 + 5_000;
        while (_device.Turns.Count < 2 && Environment.TickCount64 < deadline)
        {
            await driver.SendAsync(dev, ZoneFrame, CancellationToken.None);
            await PassTimeAsync(20);
        }
        await TestWait.ForAsync(() => _device.Turns.Count == 2 && EnableCount() == 2);
    }

    [Fact]
    public async Task Dispose_stopsTheProbe()
    {
        var driver = FastDriver(probeMs: 100);
        var dev = PairedDevice(RazerExtra);
        await driver.SendAsync(dev, ZoneFrame, CancellationToken.None);
        await TestWait.ForAsync(() => _device.StatusQueries >= 2);

        driver.Dispose();
        await PassTimeAsync(300);
        var settled = _device.StatusQueries;
        await PassTimeAsync(500);
        Assert.Equal(settled, _device.StatusQueries);
    }

    [Fact]
    public async Task Ping_reflectsDeviceReachability()
    {
        var dev = PairedDevice("""{"sku":"H619A","segments":20,"razer":true}""");
        Assert.True(await _driver.PingAsync(dev, CancellationToken.None));
        _device.LanEnabled = false;
        Assert.False(await _driver.PingAsync(dev, CancellationToken.None));
    }

    public void Dispose()
    {
        _client.Dispose();
        _device.Dispose();
    }
}
