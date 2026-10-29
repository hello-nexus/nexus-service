using Nexus.Service.Helper;
using Xunit;

namespace Nexus.Service.Tests.Helper;

public class WindowHookCountersTests
{
    [Fact]
    public void Count_SeparatesTheThreeEventTypes_AndIgnoresOthers()
    {
        var c = new WindowHookCounters();
        c.Count(WindowHookCounters.EventObjectDestroy);
        c.Count(WindowHookCounters.EventObjectDestroy);
        c.Count(WindowHookCounters.EventObjectCloaked);
        c.Count(WindowHookCounters.EventObjectUncloaked);
        c.Count(0x1234);
        Assert.Equal((2L, 1L, 1L), c.Totals);
    }
}
