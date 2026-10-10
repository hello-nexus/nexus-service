using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Xunit;

namespace Nexus.Service.Tests.Cooling;

public class FanControlBlockTests
{
    [Fact]
    public void Reports_a_block_appearing_and_clearing_once_each()
    {
        var changes = 0;
        var block = new FanControlBlock(() => changes++);

        block.Set(null);
        Assert.Equal(0, changes);

        block.Set(FanControlBlocks.AmdAutoTuning);
        block.Set(FanControlBlocks.AmdAutoTuning);
        Assert.Equal(1, changes);
        Assert.Equal(FanControlBlocks.AmdAutoTuning, block.Value);

        block.Set(null);
        block.Set(null);
        Assert.Equal(2, changes);
        Assert.Null(block.Value);
    }
}
