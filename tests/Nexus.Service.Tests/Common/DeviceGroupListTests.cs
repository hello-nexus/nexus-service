using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Common;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Common;

public class DeviceGroupListTests
{
    private static DeviceGroup Group(string id, string name, params string[] members)
        => new() { Id = id, Name = name, Members = new List<string>(members) };

    [Fact]
    public void Sanitize_KeepsOrderMembershipAndName()
    {
        var groups = DeviceGroupList.Sanitize(new[]
        {
            Group("g1", "  Desk  ", "openrgb-2", "keeb:tkl-1"),
            Group("g2", "Case", "np50:AABB:1"),
        });

        Assert.Equal(new[] { "g1", "g2" }, groups.Select(g => g.Id));
        Assert.Equal("Desk", groups[0].Name);
        Assert.Equal(new[] { "openrgb-2", "keeb:tkl-1" }, groups[0].Members);
    }

    [Fact]
    public void Sanitize_DropsEverythingPastTheCap()
    {
        var many = Enumerable.Range(0, DeviceGroupList.MaxGroups + 4)
            .Select(i => Group($"g{i}", $"G{i}", $"card-{i}"))
            .ToArray();

        var groups = DeviceGroupList.Sanitize(many);

        Assert.Equal(DeviceGroupList.MaxGroups, groups.Count);
        Assert.Equal("g0", groups[0].Id);
    }

    [Fact]
    public void Sanitize_LeavesAMemberInTheFirstGroupThatClaimsIt()
    {
        var groups = DeviceGroupList.Sanitize(new[]
        {
            Group("g1", "Desk", "openrgb-2"),
            Group("g2", "Case", "openrgb-2", "np50:AABB:1"),
        });

        Assert.Equal(new[] { "openrgb-2" }, groups[0].Members);
        Assert.Equal(new[] { "np50:AABB:1" }, groups[1].Members);
    }

    [Fact]
    public void Sanitize_KeepsAnEmptyGroup()
    {
        // A group is created empty and stays that way until a card is dragged
        // in, so dropping empty ones would delete every new group on save.
        var groups = DeviceGroupList.Sanitize(new[]
        {
            Group("g1", "Desk", "openrgb-2"),
            Group("g2", "Empty"),
        });

        Assert.Equal(new[] { "g1", "g2" }, groups.Select(g => g.Id));
        Assert.Empty(groups[1].Members);
    }

    [Fact]
    public void Sanitize_DropsBlankAndDuplicateGroupIds()
    {
        var groups = DeviceGroupList.Sanitize(new[]
        {
            Group("", "No id", "a"),
            Group("g1", "Desk", "b"),
            Group("g1", "Same id", "c"),
        });

        Assert.Single(groups);
        Assert.Equal("g1", groups[0].Id);
        Assert.Equal(new[] { "b" }, groups[0].Members);
    }

    [Fact]
    public void Sanitize_TrimsANameToTheFieldCap()
    {
        var groups = DeviceGroupList.Sanitize(new[] { Group("g1", new string('x', 40), "a") });

        Assert.Equal(DeviceGroupList.MaxNameLength, groups[0].Name.Length);
    }

    [Fact]
    public void Sanitize_TreatsNullAsEmpty()
    {
        Assert.Empty(DeviceGroupList.Sanitize(null));
    }

    [Fact]
    public void Sanitize_KeepsTheRailAnchorThatHoldsAnEmptiedGroupInPlace()
    {
        var groups = DeviceGroupList.Sanitize(new[]
        {
            new DeviceGroup { Id = "g1", Name = "Desk", Members = new List<string>(), After = "  openrgb-2  " },
        });

        Assert.Equal("openrgb-2", groups[0].After);
    }

    [Fact]
    public void Sanitize_KeepsAnAbsentAnchorAbsent()
    {
        // "" pins a group to the top of the rail, so defaulting a missing anchor
        // to it sent every newly created group there on its first save.
        var groups = DeviceGroupList.Sanitize(new[] { Group("g1", "Desk", "a") });

        Assert.Null(groups[0].After);
    }

    [Fact]
    public void Sanitize_KeepsAnExplicitTopAnchor()
    {
        var groups = DeviceGroupList.Sanitize(new[]
        {
            new DeviceGroup { Id = "g1", Name = "Desk", Members = new List<string> { "a" }, After = "" },
        });

        Assert.Equal("", groups[0].After);
    }

    [Fact]
    public void Sanitize_KeepsTheParentAndBlanksAnEmptyOrSelfReferencingOne()
    {
        var groups = DeviceGroupList.Sanitize(new[]
        {
            new DeviceGroup { Id = "g1", Name = "Desk", Members = new List<string> { "a" }, Parent = " mb:openrgb-1 " },
            new DeviceGroup { Id = "g2", Name = "Inner", Members = new List<string> { "b" }, Parent = "g1" },
            new DeviceGroup { Id = "g3", Name = "Root", Members = new List<string> { "c" }, Parent = "" },
            new DeviceGroup { Id = "g4", Name = "Loop", Members = new List<string> { "d" }, Parent = "g4" },
        });

        Assert.Equal("mb:openrgb-1", groups[0].Parent);
        Assert.Equal("g1", groups[1].Parent);
        Assert.Null(groups[2].Parent);
        Assert.Null(groups[3].Parent);
    }

    [Fact]
    public void SanitizeStacks_DissolvesShortStacksAndDropsTheRailFields()
    {
        var stacks = DeviceGroupList.SanitizeStacks(new[]
        {
            new DeviceGroup { Id = "l1", Name = "Keeb", Members = new List<string> { "a", "b" }, After = "x", Parent = "y" },
            Group("l2", "Pair", "b", "c"),
            Group("l3", "Lone", "d"),
        });

        Assert.Single(stacks);
        Assert.Equal("l1", stacks[0].Id);
        Assert.Null(stacks[0].After);
        Assert.Null(stacks[0].Parent);
    }

    [Fact]
    public void SanitizeStacks_KeepsAKnownLayoutAndDropsTheRest()
    {
        var stacks = DeviceGroupList.SanitizeStacks(new[]
        {
            new DeviceGroup { Id = "l1", Members = new List<string> { "a", "b" }, Layout = " parallel " },
            new DeviceGroup { Id = "l2", Members = new List<string> { "c", "d" }, Layout = "diagonal" },
            new DeviceGroup { Id = "l3", Members = new List<string> { "e", "f" } },
        });

        Assert.Equal("parallel", stacks[0].Layout);
        Assert.Null(stacks[1].Layout);
        Assert.Null(stacks[2].Layout);
    }

    [Fact]
    public void Sanitize_DropsTheLayoutOffARailGroup()
    {
        var group = new DeviceGroup { Id = "g1", Name = "G", Members = new List<string> { "a" }, Layout = "series" };

        var groups = DeviceGroupList.Sanitize(new[] { group });

        Assert.Null(groups[0].Layout);
    }

    [Fact]
    public void SanitizeStacks_HasNoCap()
    {
        var many = Enumerable.Range(0, DeviceGroupList.MaxGroups + 4)
            .Select(i => Group($"l{i}", $"L{i}", $"a-{i}", $"b-{i}"))
            .ToArray();

        Assert.Equal(many.Length, DeviceGroupList.SanitizeStacks(many).Count);
    }

    [Fact]
    public void Sanitize_UnplacesAParentThatNamesADroppedGroupOrLoops()
    {
        var many = Enumerable.Range(0, DeviceGroupList.MaxGroups)
            .Select(i => Group($"g{i}", $"G{i}", $"card-{i}"))
            .ToList();
        var past = new DeviceGroup { Id = "gx", Name = "Past the cap", Members = new List<string> { "x" } };
        many[0].Parent = "gx";      // names a group the cap drops
        many[1].Parent = "g2";      // loops through g2
        many[2].Parent = "g1";
        many[3].Parent = "mb:board"; // a hardware id, unknown to the service
        many.Add(past);

        var groups = DeviceGroupList.Sanitize(many);

        Assert.Null(groups[0].Parent);
        Assert.Null(groups[1].Parent);
        Assert.Equal("g1", groups[2].Parent);
        Assert.Equal("mb:board", groups[3].Parent);
    }
}
