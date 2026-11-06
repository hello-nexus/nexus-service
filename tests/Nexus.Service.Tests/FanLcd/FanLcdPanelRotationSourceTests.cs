using Nexus.Service.Peripherals.FanLcd;
using Xunit;

namespace Nexus.Service.Tests.FanLcd;

public class FanLcdPanelRotationSourceTests
{
    [Fact]
    public void A_miss_is_not_cached_and_the_next_call_finds_the_record()
    {
        string? id = null;
        var reads = 0;
        var source = new FanLcdPanelRotationSource(_ => { reads++; return id; }, _ => 90, () => 0);

        Assert.Equal(0, source.RotationOf("a"));
        id = "p1";
        Assert.Equal(90, source.RotationOf("a"));
        Assert.Equal(2, reads);
    }

    [Fact]
    public void A_failed_read_keeps_the_last_id_and_rotation()
    {
        string? id = "p1";
        int? rotation = 180;
        long now = 0;
        var source = new FanLcdPanelRotationSource(_ => id, _ => rotation, () => now);
        Assert.Equal(180, source.RotationOf("a"));

        // The store read fails (empty) after the id lapsed, and the record cannot be read.
        now = FanLcdPanelRotationSource.IdTtlMs + 1;
        id = null;
        rotation = null;

        Assert.Equal(180, source.RotationOf("a"));
    }

    [Fact]
    public void A_found_id_is_trusted_until_it_lapses_and_a_changed_rotation_is_followed()
    {
        var reads = 0;
        var rotation = 90;
        long now = 0;
        var source = new FanLcdPanelRotationSource(_ => { reads++; return "p1"; }, _ => rotation, () => now);

        Assert.Equal(90, source.RotationOf("a"));
        rotation = 270;
        Assert.Equal(270, source.RotationOf("a"));
        Assert.Equal(1, reads);

        now = FanLcdPanelRotationSource.IdTtlMs;
        source.RotationOf("a");
        Assert.Equal(2, reads);
    }
}
