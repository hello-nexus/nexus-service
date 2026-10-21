using System.Linq;
using Nexus.Service.Panel;
using Xunit;

namespace Nexus.Service.Tests;

public class PanelLayoutDefaultsStableIdsTests
{
    private static string[] Ids(string seed)
    {
        var layout = PanelLayoutDefaults.WithStableIds(PanelLayoutDefaults.ForSurface("y70"), seed);
        return layout.Pages.Select(p => p.Id).Concat(layout.Pages.SelectMany(p => p.Widgets).Select(w => w.Id)).ToArray();
    }

    [Fact]
    public void TheStarterKeepsItsIdsAcrossReads()
    {
        Assert.Equal(Ids("panel-a"), Ids("panel-a"));
    }

    [Fact]
    public void EveryIdInOneStarterIsDistinct()
    {
        var ids = Ids("panel-a");
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void TwoPanelsDoNotShareIds()
    {
        Assert.Empty(Ids("panel-a").Intersect(Ids("panel-b")));
    }
}
