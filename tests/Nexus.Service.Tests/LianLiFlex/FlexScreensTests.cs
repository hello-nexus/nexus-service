using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.LianLiFlex;
using Nexus.Service.Peripherals.LianLiWireless;
using Xunit;

namespace Nexus.Service.Tests.LianLiFlex;

public class FlexScreensTests
{
    private sealed class FakeLcd : ISlv3LcdTransport
    {
        public List<string> Calls { get; } = new();
        public bool IsOpen => true;
        public string PortName => "p";
        public bool PushImage(byte[] jpeg) { Calls.Add("push"); return true; }
        public bool SetBrightness(byte value) { Calls.Add("bright"); return true; }
        public bool SetRotation(byte value) { Calls.Add("rot"); return true; }
        public bool TryGetPosition(out byte groupIndex) { groupIndex = 0; return false; }
        public void Dispose() { }
    }

    private sealed class Ports : ISlv3LcdDiscovery
    {
        public IReadOnlyList<Slv3LcdPortInfo> Discover() => new[] { new Slv3LcdPortInfo { PortName = "p", Serial = "5C00C251854D2B79" } };
    }

    private static (FlexScreens Screens, FlexLcdHub Lcd, FakeLcd Fake, FlexClock Clock, FlexHub Hub) Create()
    {
        var fake = new FakeLcd();
        var lcd = new FlexLcdHub(new Slv3LcdHub(new Ports(), _ => fake));
        lcd.Hub.UpdateScreens(new[] { new Slv3LcdScreenInfo { Serial = "5C00C251854D2B79" } });
        var discovery = new FakeFlexDiscovery();
        discovery.Ports.Add(FakeFlexDiscovery.Tl());
        var clock = new FlexClock();
        var tx = new FakeFlexTransport();
        var hub = new FlexHub(discovery, _ => tx, clock.Func, null, _ => { });
        hub.Refresh();
        return (new FlexScreens(lcd, hub, clock.Func), lcd, fake, clock, hub);
    }

    [Fact]
    public void A_restarting_screen_is_hidden_and_untouched_until_the_quiet_time_passes()
    {
        var (screens, _, fake, clock, hub) = Create();
        // the unit's screen serial in the fake discovery is mixed case; Restart finds it by unit
        Assert.True(screens.Restart("6D1007F517E1"));
        Assert.Empty(screens.AttachedSerials());
        Assert.False(screens.PushImage("5C00C251854D2B79", new byte[] { 1 }));
        Assert.False(screens.SetRotation("5C00C251854D2B79", 1));
        Assert.Empty(fake.Calls);
        Assert.True(hub.Units[0].LcdRestarting);
    }

    [Fact]
    public void After_the_quiet_time_the_screen_is_listed_again_and_takes_writes()
    {
        var (screens, lcd, fake, clock, _) = Create();
        lcd.Hub.UpdateScreens(new[] { new Slv3LcdScreenInfo { Serial = "5C00C251854D2B79" } });
        Assert.True(screens.Restart("6D1007F517E1"));
        Assert.Empty(screens.AttachedSerials());
        clock.Now += FlexScreens.RestartQuietMs + 1;
        Assert.Equal("5C00C251854D2B79", Assert.Single(screens.AttachedSerials()));
        Assert.False(screens.Restart("000000000000"));
        Assert.NotNull(fake);
    }
}
