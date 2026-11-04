using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals.Corsair.Galleon;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Hyte.Np50;
using Xunit;

namespace Nexus.Service.Tests.Corsair;

/// <summary>Writer ticks, connection worker policy and the dial card on scripted HID layers; no hardware.</summary>
[Collection("ExperimentalUnlock")]
public class GalleonSdWriterAndWorkerTests
{
    private sealed class PaintEffect : IEffect
    {
        public string Name => "paint";
        public void RenderFrame(CanvasBuffer canvas, double tickMs) { }
        public void Dispose() { }
    }

    private sealed class ScriptedUsb : IUsbEnumerator
    {
        public List<UsbDeviceEntry> Bus { get; } = new();
        public List<UsbDeviceEntry> Enumerate() => Bus;
    }

    private sealed class KeyboardDevice : IHidDevice
    {
        public List<byte[]> Writes { get; } = new();
        public int VendorId => GalleonSdProtocol.VendorId;
        public int ProductId => GalleonSdProtocol.ProductId;
        public string Path => "kb";
        public string? Serial => "KB1";
        public int UsagePage => GalleonSdProtocol.VendorUsagePage;
        public int Usage => 1;
        public bool Write(ReadOnlySpan<byte> report) { Writes.Add(report[1..].ToArray()); return true; }
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => true;
        public void Dispose() { }
    }

    private sealed class KeyboardHid : IHidEnumerator
    {
        public KeyboardDevice Device { get; } = new();
        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) => FindAll();
        public IReadOnlyList<HidDeviceInfo> FindAll() => new[]
        {
            new HidDeviceInfo { VendorId = 0x1B1C, ProductId = 0x2B0C, Path = "kb", Serial = "KB1", UsagePage = GalleonSdProtocol.VendorUsagePage, OutputReportByteLength = 129, InputReportByteLength = 129 },
        };
        public IHidDevice? Open(string path, bool forInput = false) => Device;
    }

    private static IEnumerable<byte[]> Commands(KeyboardDevice d, byte cmd) => d.Writes.Where(w => w[3] == cmd);

    private static void Light(LightingEngine engine, GalleonSdLightingDeviceProvider provider)
    {
        var frames = provider.BuildFrames(0).ToArray();
        foreach (var f in frames)
        {
            for (var i = 0; i < f.LedCount; i++) f.SetLed(i, (byte)(i + 1), 0, 0);
            f.Publish();
        }
        engine.UpdateDevices(frames);
    }

    private sealed class KeyboardRig
    {
        public KeyboardHid Hid { get; } = new();
        public InMemoryConfigStore Store { get; } = new();
        public LightingEngine Engine { get; } = new();
        public GalleonSdHub Hub { get; }
        public GalleonSdLightingFrameWriter Writer { get; }

        public KeyboardRig(FeatureGates? gates = null)
        {
            Hub = new GalleonSdHub(Hid, matchOnLinux: true);
            Hub.Reconcile(true);
            var provider = new GalleonSdLightingDeviceProvider(Hub, Store, new Np50IdentifyTracker());
            Light(Engine, provider);
            Writer = new GalleonSdLightingFrameWriter(Engine, Hub, Store, new Np50IdentifyTracker(), gates);
        }
    }

    [Fact]
    public void Keyboard_writer_streams_while_an_effect_runs_then_hands_back_once_when_it_stops()
    {
        var rig = new KeyboardRig();
        rig.Engine.SetEffect(new PaintEffect());

        rig.Writer.Tick();
        Assert.Single(Commands(rig.Hid.Device, GalleonSdProtocol.CmdBlockFirst));
        Assert.Contains(Commands(rig.Hid.Device, GalleonSdProtocol.CmdSet), w => w[6] == GalleonSdProtocol.ModeSoftware);

        rig.Engine.Stop();
        rig.Writer.Tick();
        rig.Writer.Tick();
        Assert.Equal(1, Commands(rig.Hid.Device, GalleonSdProtocol.CmdSet).Count(w => w[6] == GalleonSdProtocol.ModeHardware));
        rig.Hub.Dispose();
    }

    [Fact]
    public void Keyboard_writer_with_no_effect_never_touches_the_board()
    {
        var rig = new KeyboardRig();
        rig.Writer.Tick();
        Assert.Empty(Commands(rig.Hid.Device, GalleonSdProtocol.CmdSet));
        Assert.Empty(Commands(rig.Hid.Device, GalleonSdProtocol.CmdBlockFirst));
        rig.Hub.Dispose();
    }

    [Fact]
    public void Keyboard_writer_hands_back_when_every_zone_is_uncontrolled()
    {
        var rig = new KeyboardRig();
        rig.Engine.SetEffect(new PaintEffect());
        rig.Writer.Tick();

        rig.Store.Update(s => s.Devices.UncontrolledLightingDevices = new List<string> { rig.Hub.Attached!.DeviceId + GalleonSdLightingDeviceProvider.LedsSuffix });
        rig.Writer.Tick();

        Assert.Equal(GalleonSdProtocol.ModeHardware, Commands(rig.Hid.Device, GalleonSdProtocol.CmdSet).Last()[6]);
        rig.Hub.Dispose();
    }

    [Fact]
    public async Task Keyboard_writer_stop_hands_the_board_back()
    {
        var rig = new KeyboardRig();
        rig.Engine.SetEffect(new PaintEffect());
        rig.Writer.Tick();

        await rig.Writer.StopAsync(default);

        Assert.Equal(GalleonSdProtocol.ModeHardware, Commands(rig.Hid.Device, GalleonSdProtocol.CmdSet).Last()[6]);
        rig.Hub.Dispose();
    }

    private sealed class DialsRig
    {
        public FakeLampArrayHid Hid { get; } = new();
        public InMemoryConfigStore Store { get; } = new();
        public LightingEngine Engine { get; } = new();
        public GalleonSdDialsHub Hub { get; }
        public GalleonSdLightingDeviceProvider Provider { get; }
        public GalleonSdDialsFrameWriter Writer { get; }

        public DialsRig()
        {
            Hub = new GalleonSdDialsHub(Hid);
            Hub.Reconcile(true);
            Provider = new GalleonSdLightingDeviceProvider(new GalleonSdHub(new KeyboardHid()), Store, new Np50IdentifyTracker(), Hub);
            Light(Engine, Provider);
            Writer = new GalleonSdDialsFrameWriter(Engine, Hub, Store, new Np50IdentifyTracker());
        }
    }

    [Fact]
    public void Dials_writer_streams_while_an_effect_runs_and_restores_autonomous_mode_when_it_stops()
    {
        var rig = new DialsRig();
        rig.Hid.Device.Sets.Clear();
        rig.Engine.SetEffect(new PaintEffect());

        rig.Writer.Tick();
        Assert.Equal(new byte[] { FakeLampArrayDevice.Ctrl, 0 }, rig.Hid.Device.Sets[0]);
        Assert.Contains(rig.Hid.Device.Sets, s => s[0] == FakeLampArrayDevice.Multi || s[0] == FakeLampArrayDevice.Range);

        rig.Engine.Stop();
        rig.Writer.Tick();
        rig.Writer.Tick();
        Assert.Equal(1, rig.Hid.Device.Sets.Count(s => s[0] == FakeLampArrayDevice.Ctrl && s[1] == 1));
    }

    [Fact]
    public async Task Dials_writer_hands_back_when_uncontrolled_and_on_stop()
    {
        var rig = new DialsRig();
        rig.Engine.SetEffect(new PaintEffect());
        rig.Writer.Tick();

        rig.Store.Update(s => s.Devices.UncontrolledLightingDevices = new List<string> { rig.Hub.Attached!.DeviceId + GalleonSdLightingDeviceProvider.LedsSuffix });
        rig.Writer.Tick();
        Assert.Equal(new byte[] { FakeLampArrayDevice.Ctrl, 1 }, rig.Hid.Device.Sets[^1]);

        rig.Store.Update(s => s.Devices.UncontrolledLightingDevices = new List<string>());
        rig.Writer.Tick();
        await rig.Writer.StopAsync(default);
        Assert.Equal(new byte[] { FakeLampArrayDevice.Ctrl, 1 }, rig.Hid.Device.Sets[^1]);
    }

    [Fact]
    public void Dial_card_is_its_own_zone_with_the_lamp_count_and_positions_from_the_device()
    {
        var rig = new DialsRig();

        var cards = rig.Provider.GetAll().Devices;

        var card = Assert.Single(cards);
        Assert.StartsWith(GalleonSdProtocol.DialsDeviceIdPrefix, card.Id);
        Assert.Equal(4, card.LedCount);
        Assert.Equal(GalleonSdProtocol.HandlerId, card.ControlHandlerId);
        var structure = Assert.Single(rig.Provider.GetStructures());
        Assert.Equal(new[] { 0f, 1f / 3, 2f / 3, 1f }, structure.Segments[0].DefaultU!, new FloatComparer());
    }

    private sealed class FloatComparer : IEqualityComparer<float>
    {
        public bool Equals(float a, float b) => Math.Abs(a - b) < 1e-4f;
        public int GetHashCode(float v) => 0;
    }

    private sealed class WorkerRig
    {
        public ScriptedUsb Usb { get; } = new();
        public FakeLampArrayHid Hid { get; } = new();
        public InMemoryConfigStore Store { get; } = new();
        public GalleonSdDialsHub Hub { get; }
        public GalleonSdLightingDeviceProvider Provider { get; }
        public int Changes { get; private set; }
        public long Now { get; set; } = 1_000_000;
        public GalleonSdConnectionWorker Worker { get; }

        public WorkerRig()
        {
            DeviceControlPolicy.ExperimentalUnlocked = true;
            Store.Update(s => s.Devices.NexusControlEnabled = new List<string> { GalleonSdProtocol.HandlerId });
            Hub = new GalleonSdDialsHub(Hid);
            Provider = new GalleonSdLightingDeviceProvider(new GalleonSdHub(new KeyboardHid()), Store, new Np50IdentifyTracker(), Hub);
            Provider.DevicesChanged += () => Changes++;
            Worker = new GalleonSdConnectionWorker(Hub, new HardwarePresence(Usb), new DeviceControlGate(Store), Provider, () => Now, GalleonSdProtocol.StreamDeckProductId, "streamdeck");
        }

        public void Plug() => Usb.Bus.Add(new UsbDeviceEntry { VendorId = 0x1B1C, ProductId = GalleonSdProtocol.StreamDeckProductId });
    }

    [Fact]
    public void Worker_ignores_the_keyboard_product_and_opens_the_dials_when_the_stream_deck_half_is_present()
    {
        var rig = new WorkerRig();
        rig.Usb.Bus.Add(new UsbDeviceEntry { VendorId = 0x1B1C, ProductId = GalleonSdProtocol.ProductId });
        rig.Worker.Tick();
        Assert.False(rig.Hub.IsConnected);

        rig.Plug();
        rig.Worker.Tick();
        rig.Worker.Tick();
        Assert.True(rig.Hub.IsConnected);
        Assert.Equal(1, rig.Changes);
    }

    [Fact]
    public void Worker_closes_the_dials_when_the_gate_turns_off_and_reopens_when_it_returns()
    {
        var rig = new WorkerRig();
        rig.Plug();
        rig.Worker.Tick();
        Assert.True(rig.Hub.IsConnected);

        rig.Store.Update(s => s.Devices.NexusControlDisabled = new List<string> { GalleonSdProtocol.HandlerId });
        rig.Worker.Tick();
        Assert.False(rig.Hub.IsConnected);
        Assert.Equal(2, rig.Changes);

        rig.Store.Update(s => s.Devices.NexusControlDisabled = new List<string>());
        rig.Worker.Tick();
        Assert.True(rig.Hub.IsConnected);
    }

    [Fact]
    public void Worker_retries_a_failed_open_on_a_backoff_not_every_tick()
    {
        var rig = new WorkerRig();
        rig.Hid.RefuseOpen = true;
        rig.Plug();
        rig.Worker.Tick();
        rig.Worker.Tick();
        Assert.Equal(0, rig.Hid.Opens);

        rig.Hid.RefuseOpen = false;
        rig.Worker.Tick();
        Assert.False(rig.Hub.IsConnected);

        rig.Now += GalleonSdConnectionWorker.OpenRetryMs + 1;
        rig.Worker.Tick();
        Assert.True(rig.Hub.IsConnected);
    }

    [Fact]
    public void Dials_stand_down_while_the_nexus_stream_deck_driver_is_on_and_resume_when_it_is_off()
    {
        var rig = new WorkerRig();
        rig.Plug();
        rig.Worker.Tick();
        Assert.True(rig.Hub.IsConnected);
        rig.Hub.WriteFrame(Enumerable.Repeat(new RgbColor(5, 5, 5), 4).ToArray());
        rig.Hid.Device.Sets.Clear();

        rig.Store.Update(s => s.Devices.NexusControlEnabled = new List<string> { GalleonSdProtocol.HandlerId, "streamdeck" });
        rig.Worker.Tick();

        Assert.False(rig.Hub.IsConnected);
        Assert.Equal(new byte[] { FakeLampArrayDevice.Ctrl, 1 }, Assert.Single(rig.Hid.Device.Sets));
        Assert.True(rig.Hid.Device.Disposed);
        rig.Worker.Tick();
        Assert.False(rig.Hub.IsConnected);

        rig.Store.Update(s => s.Devices.NexusControlEnabled = new List<string> { GalleonSdProtocol.HandlerId });
        rig.Worker.Tick();
        Assert.True(rig.Hub.IsConnected);
    }

    [Fact]
    public void The_elgato_app_running_does_not_stand_the_dials_down_while_nexus_stream_deck_is_off()
    {
        var rig = new WorkerRig();
        rig.Store.Update(s => s.Devices.NexusControlDisabled = new List<string> { "streamdeck" });
        rig.Plug();
        rig.Worker.Tick();
        Assert.True(rig.Hub.IsConnected);
    }
}
