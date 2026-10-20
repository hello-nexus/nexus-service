using System.Collections.Generic;
using Nexus.Service.Peripherals.FanLcd;
using Xunit;

namespace Nexus.Service.Tests.FanLcd;

public class FanLcdPanelRepushTests
{
    [Fact]
    public void Repush_sends_the_last_panel_frame_while_the_panel_owns_the_screen()
    {
        var own = new FanLcdPanelOwnership();
        var sent = new List<byte[]>();
        own.Set("a", true);
        var first = new byte[] { 1 };
        var second = new byte[] { 2 };
        Assert.True(own.Push("a", first, () => true));
        Assert.True(own.Push("a", second, () => true));

        Assert.True(own.Repush("a", j => { sent.Add(j); return true; }));
        Assert.Equal(new[] { second }, sent);
    }

    [Fact]
    public void Repush_sends_nothing_before_a_frame_or_after_the_screen_is_taken_back()
    {
        var own = new FanLcdPanelOwnership();
        var sent = 0;
        own.Set("a", true);
        Assert.False(own.Repush("a", _ => { sent++; return true; }));

        own.Push("a", new byte[] { 1 }, () => true);
        own.Set("a", false);
        Assert.False(own.Repush("a", _ => { sent++; return true; }));
        own.Set("a", true);
        Assert.False(own.Repush("a", _ => { sent++; return true; }));
        Assert.Equal(0, sent);
    }

    [Fact]
    public void A_frame_that_failed_to_send_is_not_remembered()
    {
        var own = new FanLcdPanelOwnership();
        own.Set("a", true);
        Assert.True(own.Push("a", new byte[] { 1 }, () => true));
        Assert.False(own.Push("a", new byte[] { 9 }, () => false));

        byte[]? repushed = null;
        own.Repush("a", j => { repushed = j; return true; });
        Assert.Equal(new byte[] { 1 }, repushed);
    }

    [Fact]
    public void Screens_are_independent()
    {
        var own = new FanLcdPanelOwnership();
        own.Set("a", true);
        own.Set("b", true);
        own.Push("a", new byte[] { 1 }, () => true);
        Assert.False(own.Repush("b", _ => true));
    }
}
