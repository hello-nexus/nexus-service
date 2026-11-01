using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Peripherals.Hyte.Np50;
using Xunit;

namespace Nexus.Service.Tests.Np50;

public class Np50ChannelInfoConcurrencyTests
{
    private static byte[] TwoFans()
    {
        var resp = new byte[12 * 3];
        resp[0] = 0xFF; resp[1] = 0xCC; resp[2] = 0x01; resp[3] = 0x02; resp[5] = 62;
        resp[14] = 0x02; resp[15] = 0x02; resp[17] = 62;
        return resp;
    }

    [Fact]
    public async Task Readers_enumerating_a_port_while_the_heartbeat_reparses_it_never_fault_or_see_a_partial_list()
    {
        var port = new Np50Port { Index = 1 };
        var resp = TwoFans();
        Np50Protocol.ParseChannelInfo(resp, port);
        using var stop = new CancellationTokenSource();
        var parser = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                Np50Protocol.ParseChannelInfo(resp, port);
            }
        });

        var seen = new HashSet<int>();
        try
        {
            for (var i = 0; i < 200_000; i++)
            {
                var count = 0;
                foreach (var dev in port.Devices)
                {
                    count++;
                    Assert.Equal("LS30", dev.Model);
                }
                seen.Add(count);
            }
        }
        finally
        {
            stop.Cancel();
            await parser;
        }

        Assert.Equal(new[] { 2 }, seen);
    }
}
