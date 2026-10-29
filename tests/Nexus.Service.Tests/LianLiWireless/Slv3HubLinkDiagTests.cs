using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.LianLiWireless;
using Xunit;

namespace Nexus.Service.Tests.LianLiWireless;

public partial class Slv3HubTests
{
    private static List<string> DrainDiag(Slv3Hub hub)
    {
        var lines = new List<string>();
        hub.Diag.Drain(lines.Add);
        return lines;
    }

    [Fact]
    public void LinkDiag_a_stable_chain_logs_only_summaries_through_the_hub()
    {
        var clock = new ManualClock();
        var (hub, net, _, _) = CreateConnectedHub(clock.NowMs);
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        DrainDiag(hub);

        for (var i = 0; i < 100; i++)
        {
            Assert.True(hub.DriveTick());
            clock.AdvanceMs(336);
        }

        var lines = DrainDiag(hub);
        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.Contains(" SUM " + Convert.ToHexString(FanMac) + " ", l));
    }

    [Fact]
    public void LinkDiag_a_chain_leaving_the_list_dumps_at_five_seconds_then_on_drop_then_on_return()
    {
        var clock = new ManualClock();
        var (hub, net, _, _) = CreateConnectedHub(clock.NowMs);
        var fan = new SimulatedFan { Mac = FanMac };
        net.Fans.Add(fan);
        Assert.True(hub.DriveTick());
        DrainDiag(hub);

        net.Fans.Clear();
        for (var i = 0; i < 20; i++)
        {
            clock.AdvanceMs(500);
            Assert.True(hub.DriveTick());
        }
        var early = DrainDiag(hub);
        Assert.Single(early, l => l.Contains(" DUMP ") && l.Contains("reason=unseen5s"));
        Assert.Contains(early, l => l.Contains(" RX ") && l.Contains(" empty "));

        clock.AdvanceMs(31_000);
        Assert.True(hub.DriveTick());
        Assert.Single(DrainDiag(hub), l => l.Contains(" DUMP ") && l.Contains("reason=drop"));

        net.Fans.Add(fan);
        clock.AdvanceMs(4_000);
        Assert.True(hub.DriveTick());
        Assert.Single(DrainDiag(hub), l => l.Contains(" DUMP ") && l.Contains("reason=reappeared"));
    }

    [Fact]
    public void LinkDiag_records_usb_presence_and_lock_events()
    {
        var lockState = new Nexus.Service.Lighting.LcdLockState();
        var (hub, _, _, _) = CreateConnectedHub(lockState: lockState);

        lockState.Set(true);
        lockState.Set(false);
        hub.OnSystemResumed();

        var lines = DrainDiag(hub);
        Assert.Contains(lines, l => l.Contains("EVT usb tx=present rx=present"));
        Assert.Contains(lines, l => l.EndsWith("EVT connected"));
        Assert.Contains(lines, l => l.EndsWith("EVT lock"));
        Assert.Contains(lines, l => l.EndsWith("EVT unlock"));
        Assert.Contains(lines, l => l.EndsWith("EVT resume"));
    }

    [Theory]
    [InlineData(false, false, "reason=tx-fail")]
    [InlineData(true, false, "reason=tx-timeout")]
    [InlineData(false, true, "reason=not-prepared")]
    public async System.Threading.Tasks.Task LinkDiag_failed_uploads_carry_their_reason(bool wedged, bool unbound, string expected)
    {
        var (hub, net, _, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = unbound ? new byte[6] : net.MasterMac, RxType = 3 });
        Assert.True(hub.DriveTick());
        DrainDiag(hub);

        net.TxWedged = wedged;
        net.TxFailFast = !wedged;
        var frames = new byte[30 * 40 * 3];
        Assert.False(await hub.SendRgbWindowAsync(Convert.ToHexString(FanMac), frames, 40, 30, 53, 100, windowId: 5));
        hub.Diag.OnDropped(Convert.ToHexString(FanMac), 1);

        Assert.Contains(DrainDiag(hub), l => l.Contains(" UP ") && l.Contains("FAIL") && l.Contains(expected) && l.Contains("window=5"));
    }
}
