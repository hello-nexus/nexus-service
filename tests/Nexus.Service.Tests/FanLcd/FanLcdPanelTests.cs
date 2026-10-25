using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Peripherals.FanLcd;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.FanLcd;

public class FanLcdPanelTests
{
    private sealed class FakeScreens : IFanLcdScreens
    {
        public List<string> Attached { get; } = new();
        public List<(string Serial, int Bytes)> Pushes { get; } = new();
        public string HandlerId => "fake-fans";
        public string LogTag => "fake-fans";
        public IReadOnlyList<string> AttachedSerials() => Attached.ToList();
        public Dictionary<string, LianLiWirelessScreenSettings> SettingsOf(DevicesSettings devices) => devices.TlLcdScreens;
        public void PrepareFrames() { }
        public void SyncBrightness(string serial, LianLiWirelessScreenSettings settings, ref int applied, ref bool lockSlept) { }
        public bool SetRotation(string serial, byte rotation) => true;
        public bool PushImage(string serial, byte[] jpeg)
        {
            Pushes.Add((serial, jpeg.Length));
            return true;
        }
        public void Release(string serial) { }
        private readonly FanLcdPanelOwnership _ownership = new();
        public void SetPanelOwned(string serial, bool owned) => _ownership.Set(serial, owned);
        public bool PushPanelFrame(string serial, byte[] jpeg) => _ownership.Push(serial, () => PushImage(serial, jpeg));
        public string PanelFamily => "fake-lcd";
        public int PanelFps => 12;
        public int MaxFps(string serial) => 30;
        public string PanelName(string serial) => $"Fake {serial}";
    }

    private static (FakeScreens Screens, InMemoryConfigStore Store) Setup(params (string Serial, string Content)[] screens)
    {
        var fake = new FakeScreens();
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            foreach (var (serial, content) in screens)
            {
                fake.Attached.Add(serial);
                s.Devices.TlLcdScreens[serial] = content == "panel"
                    ? new LianLiWirelessScreenSettings { NexusPanel = true, ContentType = "clock" }
                    : new LianLiWirelessScreenSettings { ContentType = content };
            }
        });
        return (fake, store);
    }

    [Fact]
    public void Only_screens_in_panel_mode_become_panels()
    {
        var (screens, store) = Setup(("a", "panel"), ("b", "off"), ("c", "clock"), ("d", "panel"));

        var panels = new FanLcdPanelDiscovery(screens, store).Discover();

        Assert.Equal(new[] { "fake-lcd:a", "fake-lcd:d" }, panels.Select(p => p.Serial).ToArray());
        Assert.All(panels, p =>
        {
            Assert.True(p.Profile.MultiInstance);
            Assert.Equal("fake-lcd", p.Profile.Family);
            Assert.Equal(PanelSurfaces.LcdRound, p.Profile.Surface);
            Assert.Equal(StreamCodec.RawBgra, p.Profile.Codec);
            Assert.Equal(12, p.Profile.Fps);
        });
        Assert.Equal("Fake a", panels[0].Profile.DisplayName);
    }

    [Fact]
    public void A_stored_legacy_panel_content_reads_as_the_switch_on_with_content_off()
    {
        var legacy = new LianLiWirelessScreenSettings { ContentType = "panel" };

        Assert.True(FanLcdContent.IsPanel(legacy));
        Assert.Equal("off", FanLcdContent.ContentOf(legacy));
        Assert.Equal(FanLcdContent.Panel, FanLcdContent.ModeOf(legacy));
    }

    [Fact]
    public void Switching_the_panel_off_brings_back_the_content_behind_it()
    {
        var screen = new LianLiWirelessScreenSettings { NexusPanel = true, ContentType = "clock" };
        Assert.Equal(FanLcdContent.Panel, FanLcdContent.ModeOf(screen));

        Nexus.Service.Routes.Slv3LcdRoutes.ApplyNexusPanel(screen, false);

        Assert.Equal("clock", FanLcdContent.ModeOf(screen));
    }

    [Fact]
    public void Turning_a_legacy_panel_off_leaves_the_screen_on_stock_firmware()
    {
        var screen = new LianLiWirelessScreenSettings { ContentType = "panel" };

        Nexus.Service.Routes.Slv3LcdRoutes.ApplyNexusPanel(screen, false);

        Assert.False(screen.NexusPanel);
        Assert.Equal("off", FanLcdContent.ModeOf(screen));
    }

    [Fact]
    public void Panel_is_no_longer_a_content_type()
    {
        Assert.Equal("invalid contentType", Nexus.Service.Routes.Slv3LcdRoutes.ValidateContent(
            new Nexus.Service.Routes.Slv3LcdContentRequest { Serial = "a", ContentType = "panel" }));
    }

    [Fact]
    public void Any_settings_edit_changes_the_pick_so_a_clip_or_wait_ends_early()
    {
        var screen = new LianLiWirelessScreenSettings { ContentType = "video", MediaId = "clip-a", Brightness = 80 };
        var pick = FanLcdStreamingWorker.PickOf(screen);
        Assert.Equal(pick, FanLcdStreamingWorker.PickOf(new LianLiWirelessScreenSettings { ContentType = "video", MediaId = "clip-a", Brightness = 80 }));

        Assert.NotEqual(pick, FanLcdStreamingWorker.PickOf(new LianLiWirelessScreenSettings { ContentType = "video", MediaId = "clip-b", Brightness = 80 }));
        Assert.NotEqual(pick, FanLcdStreamingWorker.PickOf(new LianLiWirelessScreenSettings { ContentType = "clock", Brightness = 80 }));
        Assert.NotEqual(pick, FanLcdStreamingWorker.PickOf(new LianLiWirelessScreenSettings { ContentType = "video", MediaId = "clip-a", Brightness = 30 }));
        Assert.NotEqual(pick, FanLcdStreamingWorker.PickOf(new LianLiWirelessScreenSettings { ContentType = "video", MediaId = "clip-a", Brightness = 80, Rotation = 2 }));
        Assert.NotEqual(pick, FanLcdStreamingWorker.PickOf(new LianLiWirelessScreenSettings { NexusPanel = true, ContentType = "video", MediaId = "clip-a", Brightness = 80 }));
    }

    [Fact]
    public void A_detached_screen_is_not_a_panel_even_in_panel_mode()
    {
        var (screens, store) = Setup(("a", "panel"));
        screens.Attached.Clear();

        Assert.Empty(new FanLcdPanelDiscovery(screens, store).Discover());
    }

    [Fact]
    public void Panel_frames_reach_the_screen_only_while_the_panel_owns_it()
    {
        var (screens, store) = Setup(("a", "panel"));
        var discovery = new FanLcdPanelDiscovery(screens, store);
        using var transport = discovery.CreateTransport(Assert.Single(discovery.Discover()));
        transport.Open();
        var frame = new byte[FanLcdContent.Size * FanLcdContent.Size * 4];

        // The content worker hands the screen over on its first pass in panel mode.
        transport.Write(frame);
        Assert.Empty(screens.Pushes);

        screens.SetPanelOwned("a", true);
        transport.Write(frame);
        Assert.Equal("a", Assert.Single(screens.Pushes).Serial);

        // A session lingers after the screen leaves panel mode; once taken back, its frames
        // are refused so they cannot overwrite the content the worker draws next.
        screens.SetPanelOwned("a", false);
        transport.Write(frame);
        Assert.Single(screens.Pushes);
    }

    [Fact]
    public void A_transport_for_another_familys_serial_is_refused()
    {
        var (screens, store) = Setup(("a", "panel"));

        Assert.Throws<System.IO.IOException>(() => new FanLcdPanelDiscovery(screens, store).CreateTransport(
            new StreamedPanelDeviceInfo
            {
                Serial = "other:a",
                Profile = new StreamedPanelProfile { Kind = "x", DisplayName = "x", Surface = PanelSurfaces.LcdRound, CssWidth = 400, CssHeight = 400 },
            }));
    }
}
