using System.Collections.Generic;
using Nexus.Service.Deck;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Deck;

public class RecentAppsTrackerTests
{
    [Fact]
    public void UpdateRing_NewEntry_InsertsAtFront()
    {
        var ring = new List<RecentApp> { new() { ProcessKey = "old", Name = "Old" } };

        var changed = RecentAppsTracker.UpdateRing(ring, new RecentApp { ProcessKey = "new", Name = "New" }, System.Array.Empty<string>());

        Assert.True(changed);
        Assert.Equal("new", ring[0].ProcessKey);
        Assert.Equal("old", ring[1].ProcessKey);
    }

    [Fact]
    public void UpdateRing_ExistingEntry_MovesToFrontAndDedupes()
    {
        var ring = new List<RecentApp>
        {
            new() { ProcessKey = "a", Name = "A" },
            new() { ProcessKey = "b", Name = "B" },
            new() { ProcessKey = "c", Name = "C" },
        };

        RecentAppsTracker.UpdateRing(ring, new RecentApp { ProcessKey = "c", Name = "C" }, System.Array.Empty<string>());

        Assert.Equal(3, ring.Count);
        Assert.Equal("c", ring[0].ProcessKey);
        Assert.Equal("a", ring[1].ProcessKey);
        Assert.Equal("b", ring[2].ProcessKey);
    }

    [Fact]
    public void UpdateRing_ExistingEntry_PreservesResolvedShortcutIdAndLabelWhenCandidateHasNone()
    {
        var ring = new List<RecentApp> { new() { ProcessKey = "taskmgr", Name = "Task Manager", ShortcutId = "shortcut-tm" } };

        // A focus event carries only the process name.
        RecentAppsTracker.UpdateRing(ring, new RecentApp { ProcessKey = "taskmgr", Name = "Taskmgr" }, System.Array.Empty<string>());

        Assert.Equal("shortcut-tm", ring[0].ShortcutId);
        Assert.Equal("Task Manager", ring[0].Name);
    }

    [Fact]
    public void UpdateRing_BuiltInDenylistEntry_Rejected()
    {
        var ring = new List<RecentApp>();

        var changed = RecentAppsTracker.UpdateRing(ring, new RecentApp { ProcessKey = "explorer", Name = "Explorer" }, System.Array.Empty<string>());

        Assert.False(changed);
        Assert.Empty(ring);
    }

    [Fact]
    public void UpdateRing_UserExcludedEntry_Rejected()
    {
        var ring = new List<RecentApp>();

        var changed = RecentAppsTracker.UpdateRing(ring, new RecentApp { ProcessKey = "discord", Name = "Discord" }, new[] { "discord" });

        Assert.False(changed);
        Assert.Empty(ring);
    }

    [Fact]
    public void UpdateRing_EmptyProcessKey_Rejected()
    {
        var ring = new List<RecentApp>();

        var changed = RecentAppsTracker.UpdateRing(ring, new RecentApp { ProcessKey = "", Name = "" }, System.Array.Empty<string>());

        Assert.False(changed);
        Assert.Empty(ring);
    }

    [Fact]
    public void UpdateRing_OverCap_DropsOldestTail()
    {
        var ring = new List<RecentApp>();
        for (var i = 0; i < RecentAppsTracker.RingCap; i++)
        {
            RecentAppsTracker.UpdateRing(ring, new RecentApp { ProcessKey = $"p{i}", Name = $"App {i}" }, System.Array.Empty<string>());
        }
        Assert.Equal(RecentAppsTracker.RingCap, ring.Count);

        RecentAppsTracker.UpdateRing(ring, new RecentApp { ProcessKey = "newest", Name = "Newest" }, System.Array.Empty<string>());

        Assert.Equal(RecentAppsTracker.RingCap, ring.Count);
        Assert.Equal("newest", ring[0].ProcessKey);
        Assert.DoesNotContain(ring, a => a.ProcessKey == "p0");
    }

    [Fact]
    public void BuildView_ZeroKeyGrid_ReturnsNoPages()
    {
        var ring = new List<RecentApp> { new() { ProcessKey = "a", Name = "A" } };

        var pages = RecentAppsTracker.BuildView(ring, null, 0, 3);

        Assert.Empty(pages);
    }
}
