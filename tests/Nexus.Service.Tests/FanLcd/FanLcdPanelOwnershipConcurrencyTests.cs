using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Peripherals.FanLcd;
using Xunit;

namespace Nexus.Service.Tests.FanLcd;

public class FanLcdPanelOwnershipConcurrencyTests
{
    [Fact]
    public async Task Screens_taken_and_released_on_different_threads_do_not_corrupt_the_shared_tables()
    {
        var own = new FanLcdPanelOwnership();
        var failures = new List<Exception>();
        var frame = new byte[] { 1 };

        // Each screen holds its own lock, so these loops write the shared tables with no common lock.
        var workers = new List<Task>();
        for (var t = 0; t < 4; t++)
        {
            var serial = $"screen-{t}";
            workers.Add(Task.Run(() =>
            {
                try
                {
                    for (var i = 0; i < 20_000; i++)
                    {
                        own.Set(serial, true);
                        own.Push(serial, frame, () => true);
                        own.Set(serial, false);
                    }
                }
                catch (Exception ex)
                {
                    lock (failures) { failures.Add(ex); }
                }
            }));
        }
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Empty(failures);
        Assert.False(own.Push("screen-0", () => true));
    }
}
