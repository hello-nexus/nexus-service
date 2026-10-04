using Nexus.Service.Lighting.Scene;

namespace Nexus.Service.Tests.Lighting.Scene;

public class SceneImportTests
{
    private static SceneObject Case(string id, string source, params string[] anchors) => new()
    {
        Id = id,
        Kind = "case",
        Source = source,
        Position = [620, 0, -120],
        Yaw = 30,
        Anchors = anchors.Select(a => new SceneAnchor { Id = a, Kind = "fan" }).ToList(),
    };

    private static SceneBinding Bind(string device, string obj, string anchor) =>
        new() { DeviceId = device, Targets = { new SceneTarget { ObjectId = obj, AnchorId = anchor } } };

    [Fact]
    public void Reimport_keeps_a_moved_object_in_place_and_drops_placements_on_vanished_spots()
    {
        var doc = new LightingSceneDoc
        {
            Objects = { Case("case", "build", "fan:front:120:0", "fan:top:120:0") },
            Bindings = { Bind("fan-1", "case", "fan:front:120:0"), Bind("fan-2", "case", "fan:top:120:0") },
        };
        var imported = Case("case", "build", "fan:front:120:0");
        imported.Position = [0, 0, 0];
        imported.Yaw = 0;

        var merged = SceneImport.Merge(doc, [imported], new HashSet<string> { "case" });

        var pc = merged.Objects.Single();
        Assert.Equal(620f, pc.Position[0]);
        Assert.Equal(30f, pc.Yaw);
        Assert.Equal("fan-1", merged.Bindings.Single().DeviceId);
    }

    [Fact]
    public void Each_object_draws_from_the_model_only_when_it_has_a_shape_there()
    {
        var keyboard = new SceneObject { Id = "keyboard", Kind = "keyboard" };
        var merged = SceneImport.Merge(new LightingSceneDoc(), [Case("case", "build"), keyboard], new HashSet<string> { "keyboard" });

        Assert.False(merged.Objects.Single(o => o.Id == "case").HasModel);
        Assert.True(merged.Objects.Single(o => o.Id == "keyboard").HasModel);
        Assert.All(merged.Objects, o => Assert.Equal("build", o.Source));
    }

    [Fact]
    public void Objects_placed_by_hand_survive_an_import()
    {
        var doc = new LightingSceneDoc { Objects = { Case("old-case", "user", "fan:front:120:0") }, Bindings = { Bind("fan-1", "old-case", "fan:front:120:0") } };
        var merged = SceneImport.Merge(doc, [Case("case", "build")], new HashSet<string>());
        Assert.Contains(merged.Objects, o => o.Id == "old-case");
        Assert.Single(merged.Bindings);
    }
}
