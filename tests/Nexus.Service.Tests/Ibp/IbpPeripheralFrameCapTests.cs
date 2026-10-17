using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Peripherals.Ibp;
using Xunit;

namespace Nexus.Service.Tests.Ibp;

/// <summary>
/// The user frame cap on the iBUYPOWER writer, over a scripted HID layer and a stepped clock: Off is a
/// passthrough, the cap paces frames, the device limit still binds, the last frame lands, and the
/// firmware handback is not frame traffic. It cannot say what a unit does with the bytes.
/// </summary>
public sealed class IbpPeripheralFrameCapTests : IDisposable
{
    private sealed class ScriptedHidDevice : IHidDevice
    {
        public ScriptedHidDevice(HidDeviceInfo info) { Info = info; }
        public HidDeviceInfo Info { get; }
        public List<byte[]> Features { get; } = new();
        public int VendorId => Info.VendorId;
        public int ProductId => Info.ProductId;
        public string Path => Info.Path;
        public string? Serial => Info.Serial;
        public int UsagePage => Info.UsagePage;
        public int Usage => Info.Usage;
        public bool SetFeature(ReadOnlySpan<byte> report) { Features.Add(report.ToArray()); return true; }
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool Write(ReadOnlySpan<byte> report) => true;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => true;
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public void Dispose() { }
    }

    private sealed class ScriptedEnumerator : IHidEnumerator
    {
        private readonly List<HidDeviceInfo> _infos = new();
        public Dictionary<string, ScriptedHidDevice> Opened { get; } = new();

        public void AddUnit(IbpPeripheralModel model, string serial)
        {
            _infos.Add(new HidDeviceInfo
            {
                VendorId = IbpPeripheralProtocol.VendorId, ProductId = model.ProductId,
                Path = $"{model.Id}-{serial}-vendor", Serial = serial, UsagePage = 0xFF00, Usage = 0x01,
                FeatureReportByteLength = model.ReportLength,
            });
        }

        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) =>
            _infos.Where(i => i.VendorId == vendorId && i.ProductId == productId).ToList();
        public IReadOnlyList<HidDeviceInfo> FindAll() => _infos.ToList();
        public IHidDevice? Open(string path, bool forInput = false)
        {
            var dev = new ScriptedHidDevice(_infos.First(i => i.Path == path));
            Opened[path] = dev;
            return dev;
        }
    }

    private sealed class SteadyEffect : IEffect
    {
        public string Name => "steady";
        public void RenderFrame(CanvasBuffer canvas, double tickMs) { }
        public void Dispose() { }
    }

    private const long TickMs = 33;

    private readonly LightingEngine _engine = new();
    private readonly InMemoryConfigStore _store = new();
    private readonly ScriptedEnumerator _hid = new();
    private readonly IbpPeripheralHub _hub;
    private long _clockMs = 10_000;

    public IbpPeripheralFrameCapTests()
    {
        _hid.AddUnit(IbpPeripheralProtocol.Km7Keyboard, "KB1");
        _hid.AddUnit(IbpPeripheralProtocol.Mek4Keyboard, "MK1");
        // The hub's own clock is in stopwatch stamps; keep both clocks on the same stepped time.
        _hub = new IbpPeripheralHub(_hid, () => _clockMs * Stopwatch.Frequency / 1000);
        _hub.Reconcile(new HashSet<IbpPeripheralModel>(IbpPeripheralProtocol.Models));
        var frames = _hub.Attached
            .Select((u, i) => new DeviceFrame(i, u.DeviceId + IbpPeripheralLightingDeviceProvider.LedsSuffix, u.Model.LedCount))
            .ToArray();
        _engine.UpdateDevices(frames);
        _engine.SetEffect(new SteadyEffect());
    }

    public void Dispose()
    {
        _hub.Dispose();
        _engine.Dispose();
    }

    private IbpPeripheralLightingFrameWriter Writer(bool withCap)
    {
        var cap = withCap ? new LightingFrameCap(_store, () => _clockMs) : null;
        return new IbpPeripheralLightingFrameWriter(_engine, _hub, _store, new Np50IdentifyTracker(), cap: cap);
    }

    private string Id(IbpPeripheralModel model) => _hub.Attached.Single(a => a.Model == model).DeviceId;

    private void SetCap(IbpPeripheralModel model, int hz) =>
        _store.Update(s => s.Devices.LightingMaxRates[Id(model)] = hz);

    private void Run(IbpPeripheralLightingFrameWriter writer, int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            writer.Tick();
            _clockMs += TickMs;
        }
    }

    private ScriptedHidDevice Km7 => _hid.Opened["km7-keyboard-KB1-vendor"];
    private ScriptedHidDevice Mek4 => _hid.Opened["mek4-keyboard-MK1-vendor"];

    [Fact]
    public void Off_writes_every_tick_with_or_without_a_cap_service()
    {
        Run(Writer(withCap: false), 30);
        var plainKm7 = Km7.Features.Select(f => f.Length).ToList();
        var plainMek4 = Mek4.Features.Count;

        // Back to firmware so the second run starts from the same hub state as the first.
        _hub.ReleaseAllToFirmware();
        Km7.Features.Clear();
        Mek4.Features.Clear();
        Run(Writer(withCap: true), 30);

        Assert.Equal(plainKm7, Km7.Features.Select(f => f.Length).ToList());
        Assert.Equal(plainMek4, Mek4.Features.Count);
        Assert.True(Km7.Features.Count >= 90);
    }

    [Fact]
    public void A_cap_paces_the_frames_to_its_average_rate()
    {
        SetCap(IbpPeripheralProtocol.Km7Keyboard, 5);
        var writer = Writer(withCap: true);
        // The first tick also carries the one-time software-mode report.
        Run(writer, 1);
        var afterFirst = Km7.Features.Count;

        // Ten more seconds of ticks.
        Run(writer, (int)(10_000 / TickMs));

        var reportsPerFrame = IbpPeripheralProtocol.Km7Keyboard.ReportCount;
        var frames = (Km7.Features.Count - afterFirst) / (double)reportsPerFrame / 10.0;
        Assert.InRange(frames, 4.5, 5.6);
    }

    [Fact]
    public void A_cap_on_one_unit_leaves_the_other_unit_alone()
    {
        SetCap(IbpPeripheralProtocol.Km7Keyboard, 1);
        var writer = Writer(withCap: true);
        Run(writer, 30);

        Assert.InRange(Km7.Features.Count, 1, 12);
        // The MEK 4 is held to its own limit by the hub, which the cap does not change.
        Assert.Equal(15, Mek4.Features.Count);
    }

    [Fact]
    public void The_device_limit_binds_when_it_is_stricter_than_the_cap()
    {
        SetCap(IbpPeripheralProtocol.Mek4Keyboard, 30);
        Run(Writer(withCap: true), 30);
        Assert.Equal(15, Mek4.Features.Count);
    }

    [Fact]
    public void A_cap_below_the_device_limit_paces_the_mek4()
    {
        SetCap(IbpPeripheralProtocol.Mek4Keyboard, 5);
        var writer = Writer(withCap: true);
        Run(writer, (int)(10_000 / TickMs));
        var perSecond = Mek4.Features.Count / 10.0;
        Assert.InRange(perSecond, 4.5, 5.6);
    }

    [Fact]
    public void The_firmware_handback_goes_at_once_inside_a_cap_interval()
    {
        SetCap(IbpPeripheralProtocol.Km7Keyboard, 1);
        var writer = Writer(withCap: true);
        Run(writer, 3);
        var streaming = Km7.Features.Count;

        _engine.Stop();
        Run(writer, 1);

        // The handback is not frame traffic: it is not held by the cap's interval.
        Assert.True(Km7.Features.Count > streaming);
        Assert.Equal("none", _engine.CurrentEffectName);
    }

    [Fact]
    public void The_latest_frame_is_written_within_one_cap_interval()
    {
        SetCap(IbpPeripheralProtocol.Km7Keyboard, 1);
        var writer = Writer(withCap: true);
        var frame = _engine.Devices.Single(d => d.Id == Id(IbpPeripheralProtocol.Km7Keyboard) + IbpPeripheralLightingDeviceProvider.LedsSuffix);
        Run(writer, 1);
        var before = Km7.Features.Count;

        // A static look changes once, mid-hold, then never again.
        _clockMs += 100;
        frame.Fill(1, 2, 3);
        frame.Publish();
        Run(writer, 1);
        Assert.Equal(before, Km7.Features.Count);

        Run(writer, (int)(1000 / TickMs) + 1);
        Assert.True(Km7.Features.Count > before);
    }
}
