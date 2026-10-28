using System.IO;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Peripherals.SteelSeries;
using Xunit;

namespace Nexus.Service.Tests.SteelSeries;

public class ApexOledPanelDiscoveryTests
{
    [Fact]
    public void Nothing_attached_is_an_empty_pass()
    {
        Assert.Empty(new ApexOledPanelDiscovery(new ApexOledStreamTransportTests.FakeHid()).Discover());
    }

    [Fact]
    public void Only_the_screen_interface_counts_as_a_panel()
    {
        var hid = new ApexOledStreamTransportTests.FakeHid();
        hid.Add(0x1628, usagePage: 0x0001);

        Assert.Empty(new ApexOledPanelDiscovery(hid).Discover());
    }

    [Theory]
    [InlineData(0x1628, "SteelSeries Apex Pro TKL 2023")]
    [InlineData(0x1642, "SteelSeries Apex Pro TKL Gen 3")]
    public void An_attached_keyboard_is_one_native_size_streamed_panel(int pid, string name)
    {
        var hid = new ApexOledStreamTransportTests.FakeHid();
        hid.Add(pid);

        var info = Assert.Single(new ApexOledPanelDiscovery(hid).Discover());
        var profile = info.Profile;

        Assert.Equal(name, profile.DisplayName);
        Assert.Equal(PanelSurfaces.LcdWide, profile.Surface);
        Assert.Equal(ApexOledPanelDiscovery.Id, profile.Family);
        Assert.Equal((128, 40), (profile.CssWidth, profile.CssHeight));
        Assert.Equal(1.0, profile.Dpr);
        Assert.Equal(StreamCodec.RawBgra, profile.Codec);
        Assert.True(profile.MultiInstance);
        Assert.Contains(profile.Fps, StreamFpsCap.Allowed);
        Assert.Equal(ApexOledMono.FrameLength, StreamedPanelProfile.FramePixels(profile.CssWidth, 1.0) * StreamedPanelProfile.FramePixels(profile.CssHeight, 1.0) * 4);
    }

    [Fact]
    public void The_transport_follows_the_model_in_the_serial()
    {
        var hid = new ApexOledStreamTransportTests.FakeHid();
        hid.Add(0x1642);
        var discovery = new ApexOledPanelDiscovery(hid);
        var info = Assert.Single(discovery.Discover());

        using var transport = discovery.CreateTransport(info);
        transport.Open();

        Assert.True(transport.IsOpen);
        Assert.Equal(info.Serial, transport.Serial);
    }

    [Fact]
    public void A_foreign_serial_is_refused()
    {
        var discovery = new ApexOledPanelDiscovery(new ApexOledStreamTransportTests.FakeHid());
        var info = new StreamedPanelDeviceInfo
        {
            Serial = "nope",
            Profile = new StreamedPanelProfile { Kind = "x", DisplayName = "x", Surface = PanelSurfaces.LcdWide, CssWidth = 1, CssHeight = 1 },
        };

        Assert.Throws<IOException>(() => discovery.CreateTransport(info));
    }
}
