using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Devices;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Peripherals.FanLcd;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.FanLcd;

/// <summary>A fan screen has a firmware rotation, so the panel's mount turn rides it instead of being turned in software.</summary>
public class FanLcdPanelRotationTests
{
    private sealed class Screens : IFanLcdScreens
    {
        private readonly object _gate = new();
        private readonly FanLcdPanelOwnership _ownership = new();
        private readonly List<byte> _rotations = new();
        private readonly List<byte[]> _frames = new();
        public string HandlerId => "fake-fans";
        public string LogTag => "fake-fans";
        public IReadOnlyList<string> AttachedSerials() => new[] { "a" };
        public Dictionary<string, LianLiWirelessScreenSettings> SettingsOf(DevicesSettings devices) => devices.TlLcdScreens;
        public void PrepareFrames() { }
        public void SyncBrightness(string serial, LianLiWirelessScreenSettings settings, ref int applied, ref bool lockSlept) { }
        public bool SetRotation(string serial, byte rotation)
        {
            lock (_gate) { _rotations.Add(rotation); }
            return true;
        }
        public bool PushImage(string serial, byte[] jpeg)
        {
            lock (_gate) { _frames.Add(jpeg); }
            return true;
        }
        public void Release(string serial) { }
        public void SetPanelOwned(string serial, bool owned) => _ownership.Set(serial, owned);
        public bool PushPanelFrame(string serial, byte[] jpeg) => _ownership.Push(serial, () => PushImage(serial, jpeg));
        public string PanelFamily => "fake-lcd";
        public int PanelFps => 12;
        public string PanelName(string serial) => serial;
        public byte[] Rotations { get { lock (_gate) { return _rotations.ToArray(); } } }
        public byte[][] Frames { get { lock (_gate) { return _frames.ToArray(); } } }
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 1, 1)]
    [InlineData(0, 2, 2)]
    [InlineData(0, 3, 3)]
    [InlineData(1, 2, 3)]
    [InlineData(3, 1, 0)]
    [InlineData(2, 2, 0)]
    public void The_firmware_turn_is_the_screens_setting_plus_the_panels(int setting, int panelTurns, int expected)
    {
        var screen = new LianLiWirelessScreenSettings { Rotation = (byte)setting };

        Assert.Equal(expected, FanLcdStreamingWorker.HardwareRotation(screen, panelTurns));
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50);
        }
        Assert.True(condition());
    }

    private static async Task<(Screens Screens, int[] Seen)> Run(bool panelMode, Func<int> panelRotation, Func<Screens, Task> act)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fanlcd-panelrot-" + Guid.NewGuid().ToString("N"));
        var store = new InMemoryConfigStore();
        store.Update(s => s.Devices.TlLcdScreens["a"] = panelMode
            ? new LianLiWirelessScreenSettings { NexusPanel = true, ContentType = "clock", Rotation = 1 }
            : new LianLiWirelessScreenSettings { ContentType = "off", Rotation = 1 });
        var screens = new Screens();
        var worker = new FanLcdStreamingWorker(
            screens, new Slv3LcdMediaLibrary(System.IO.Path.Combine(dir, "media")), new Slv3LcdSensorReader(null!), store, new DeviceControlGate(store),
            _ => panelRotation());
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await act(screens);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { }
        }
        return (screens, screens.Rotations.Select(r => (int)r).ToArray());
    }

    [Fact]
    public async Task A_screen_showing_its_panel_follows_the_records_rotation_in_firmware()
    {
        var rotation = 90;
        var (_, seen) = await Run(panelMode: true, () => rotation, async screens =>
        {
            await WaitFor(() => screens.Rotations.Length >= 1);
            rotation = 180;
            await WaitFor(() => screens.Rotations.Length >= 2);
        });

        // The screen's own setting (1 quarter turn) plus the record's: 90 then 180 degrees.
        Assert.Equal(new[] { 2, 3 }, seen.Take(2));
    }

    [Fact]
    public async Task A_screen_showing_other_content_ignores_the_panel_records_rotation()
    {
        var (_, seen) = await Run(panelMode: false, () => 180, async screens =>
        {
            await WaitFor(() => screens.Rotations.Length >= 1);
            await Task.Delay(300);
        });

        Assert.Equal(new[] { 1 }, seen);
    }

    // Left half red, right half blue in BGRA: a flip, a turn and a mirror each give a different picture.
    private static byte[] Halves()
    {
        const int size = FanLcdContent.Size;
        var frame = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                var i = ((y * size) + x) * 4;
                var left = x < size / 2;
                frame[i] = left ? (byte)0 : (byte)255;
                frame[i + 2] = left ? (byte)255 : (byte)0;
                frame[i + 3] = 255;
            }
        }
        return frame;
    }

    private static byte[] PushedWith(int rotation, bool mirror)
    {
        var screens = new Screens();
        screens.SetPanelOwned("a", true);
        using var transport = new FanLcdPanelTransport(screens, "a", "fake-lcd:a");
        transport.BindOrientation(() => (rotation, mirror));
        transport.Open();

        transport.Write(Halves());

        return Assert.Single(screens.Frames);
    }

    [Fact]
    public void The_panel_transport_leaves_the_turn_to_the_firmware_and_applies_only_the_mirror()
    {
        var upright = PushedWith(0, false);

        Assert.Equal(upright, PushedWith(90, false));
        Assert.Equal(upright, PushedWith(180, false));
        Assert.Equal(upright, PushedWith(270, false));
        Assert.NotEqual(upright, PushedWith(0, true));
        Assert.Equal(PushedWith(0, true), PushedWith(180, true));
    }
}
