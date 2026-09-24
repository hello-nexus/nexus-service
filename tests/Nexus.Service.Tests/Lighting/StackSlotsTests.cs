using System.Collections.Generic;
using Nexus.Service.Lighting;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Lighting;

public class StackSlotsTests
{
    [Fact]
    public void Overlap_AndSingletons_SampleTheWholeFrame()
    {
        Assert.Equal((10f, 20f, 300f, 100f), StackSlots.Slice(10, 20, 300, 100, new StackSlots.Slot("overlap", 1, 3)));
        Assert.Equal((10f, 20f, 300f, 100f), StackSlots.Slice(10, 20, 300, 100, new StackSlots.Slot("parallel", 0, 1)));
    }

    [Fact]
    public void Parallel_CutsEqualBandsTopToBottom()
    {
        Assert.Equal((0f, 0f, 300f, 50f), StackSlots.Slice(0, 0, 300, 100, new StackSlots.Slot("parallel", 0, 2)));
        Assert.Equal((0f, 50f, 300f, 50f), StackSlots.Slice(0, 0, 300, 100, new StackSlots.Slot("parallel", 1, 2)));
    }

    [Fact]
    public void Series_CutsEqualColumnsLeftToRight()
    {
        Assert.Equal((200f, 0f, 100f, 100f), StackSlots.Slice(0, 0, 300, 100, new StackSlots.Slot("series", 2, 3)));
    }

    [Fact]
    public void Index_KeysEveryMemberOfALaidOutStack_AndSkipsOverlap()
    {
        var slots = StackSlots.Index(new List<DeviceGroup>
        {
            new() { Id = "s1", Members = new List<string> { "a", "b", "c" }, Layout = "series" },
            new() { Id = "s2", Members = new List<string> { "d", "e" } },
        });

        Assert.Equal(3, slots.Count);
        Assert.Equal(new StackSlots.Slot("series", 1, 3), slots["b"]);
        Assert.False(slots.ContainsKey("d"));
    }
}
