using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Devices;
using Nexus.Service.Peripherals.FanLcd;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.FanLcd;

public class FanLcdStaticRotationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fanlcd-rot-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private sealed class Screens : IFanLcdScreens
    {
        public object Gate { get; } = new();
        public List<byte> Rotations { get; } = new();
        public List<byte[]> Pushes { get; } = new();
        public string HandlerId => "fake-fans";
        public string LogTag => "fake-fans";
        public IReadOnlyList<string> AttachedSerials() => new[] { "a" };
        public Dictionary<string, LianLiWirelessScreenSettings> SettingsOf(DevicesSettings devices) => devices.TlLcdScreens;
        public void PrepareFrames() { }
        public void SyncBrightness(string serial, LianLiWirelessScreenSettings settings, ref int applied, ref bool lockSlept) { }
        public bool SetRotation(string serial, byte rotation)
        {
            lock (Gate) { Rotations.Add(rotation); }
            return true;
        }
        public bool PushImage(string serial, byte[] jpeg)
        {
            lock (Gate) { Pushes.Add(jpeg); }
            return true;
        }
        public void Release(string serial) { }
        public void SetPanelOwned(string serial, bool owned) { }
        public bool PushPanelFrame(string serial, byte[] jpeg) => false;
        public string PanelFamily => "fake-lcd";
        public int PanelFps => 12;
        public string PanelName(string serial) => serial;
        public int PushCount { get { lock (Gate) { return Pushes.Count; } } }
    }

    [Fact]
    public async Task A_still_picture_is_sent_again_when_the_rotation_changes()
    {
        var library = new Slv3LcdMediaLibrary(Path.Combine(_dir, "media"));
        var frame = new byte[] { 0xFF, 0xD8, 1, 2, 3, 0xFF, 0xD9 };
        Directory.CreateDirectory(Path.Combine(_dir, "media", "pic"));
        File.WriteAllBytes(Path.Combine(_dir, "media", "pic", "frame-0000.jpg"), frame);
        library.SaveMeta(new Slv3LcdMediaItem { Id = "pic", Name = "pic", Kind = "image", Frames = 1 });
        var store = new InMemoryConfigStore();
        store.Update(s => s.Devices.TlLcdScreens["a"] = new LianLiWirelessScreenSettings { ContentType = "image", MediaId = "pic", Rotation = 1 });
        var screens = new Screens();
        var worker = new FanLcdStreamingWorker(screens, library, new Slv3LcdSensorReader(null!), store, new DeviceControlGate(store));
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitFor(() => screens.PushCount >= 1);
            await Task.Delay(1500);
            Assert.Equal(1, screens.PushCount);

            store.Update(s => s.Devices.TlLcdScreens["a"].Rotation = 2);
            await WaitFor(() => screens.PushCount >= 2);
            Assert.Equal(new byte[] { 1, 2 }, screens.Rotations.ToArray());
            Assert.All(screens.Pushes, p => Assert.Equal(frame, p));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50);
        }
        Assert.True(condition());
    }
}
