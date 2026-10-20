#if WINDOWS
using System.Collections.Generic;
using Nexus.Service.Peripherals.LianLiWireless;

namespace Nexus.Service.Tests.LianLiWireless;

public class Slv3LcdTransportTests
{
    [Fact]
    public void A_failing_ack_read_waits_longer_each_time_up_to_a_second()
    {
        var waits = new List<int>();
        var ms = 0;
        for (var i = 0; i < 10; i++)
        {
            ms = Slv3LcdTransport.NextDrainRetryMs(ms);
            waits.Add(ms);
        }
        Assert.Equal(new[] { 10, 20, 40, 80, 160, 320, 640, 1000, 1000, 1000 }, waits);
    }
}
#endif
