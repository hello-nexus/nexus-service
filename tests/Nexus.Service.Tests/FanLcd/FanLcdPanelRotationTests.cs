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

/// <summary>A fan screen keeps its firmware rotation as a device-page setting; the panel only flips and mirrors the frame in software.</summary>
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

    private static (byte[] Frame, Screens Screens) PushedWith(int rotation, bool mirror)
    {
        var screens = new Screens();
        screens.SetPanelOwned("a", true);
        using var transport = new FanLcdPanelTransport(screens, "a", "fake-lcd:a");
        transport.BindOrientation(() => (rotation, mirror));
        transport.Open();

        transport.Write(Halves());

        return (Assert.Single(screens.Frames), screens);
    }

    // The same pixels the old Flip180 path produced: the BGRA frame turned in software before encoding.
    private static byte[] OldFlipPath(bool flip, bool mirror)
    {
        var frame = Halves();
        var turned = new byte[frame.Length];
        Nexus.Service.Peripherals.PixelFormats.BgraOrientation.Apply(frame, FanLcdContent.Size, FanLcdContent.Size, flip ? 180 : 0, mirror, turned);
        using var encoder = new Nexus.Service.Peripherals.JpegPanels.BgraJpegEncoder(FanLcdContent.Size, FanLcdContent.Size);
        return encoder.Encode(turned).ToArray();
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(180, false, true)]
    [InlineData(0, true, false)]
    [InlineData(180, true, true)]
    public void A_half_turn_and_mirror_are_applied_in_software_exactly_as_the_old_flip_was(int rotation, bool mirror, bool flip)
    {
        Assert.Equal(OldFlipPath(flip, mirror), PushedWith(rotation, mirror).Frame);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    public void Quarter_turns_are_not_taken_and_no_firmware_rotation_is_written_from_the_panel_path(int rotation)
    {
        var (frame, screens) = PushedWith(rotation, false);

        Assert.Equal(OldFlipPath(false, false), frame);
        Assert.Empty(screens.Rotations);
    }
}
