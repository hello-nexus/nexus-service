using Nexus.Service.Cooling;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Cooling;

public class CurveTickSnapshotTests
{
    [Fact]
    public void Outputs_snapshot_is_unaffected_by_later_edits()
    {
        var curve = new CurveDocument();
        curve.Outputs.Add(new CurveOutputDocument { Id = "a" });
        curve.Outputs.Add(new CurveOutputDocument { Id = "b" });

        var snapshot = CoolingSnapshots.Outputs(curve);
        curve.Outputs.RemoveAll(o => o.Id == "a");
        curve.Outputs.Add(new CurveOutputDocument { Id = "c" });

        Assert.Equal(new[] { "a", "b" }, snapshot.Select(o => o.Id));
    }

    [Fact]
    public void Enumerating_the_snapshot_tolerates_edits_to_the_live_list()
    {
        var curve = new CurveDocument();
        for (var i = 0; i < 4; i++)
        {
            curve.Outputs.Add(new CurveOutputDocument { Id = $"o{i}" });
        }

        foreach (var output in CoolingSnapshots.Outputs(curve))
        {
            curve.Outputs.RemoveAll(o => o.Id == output.Id);
        }

        Assert.Empty(curve.Outputs);
    }

    [Fact]
    public void EvaluateGraph_does_not_reorder_the_live_points()
    {
        var graph = new GraphCurveData
        {
            Points =
            {
                new GraphPoint { Temp = 80, Speed = 100 },
                new GraphPoint { Temp = 30, Speed = 20 },
            },
        };

        var speed = CurveEngine.EvaluateGraph(graph, 55f);

        Assert.Equal(60, speed);
        Assert.Equal(80, graph.Points[0].Temp);
    }
}
