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
    public void Imported_case_takes_over_a_generic_case_and_its_shared_slot_placements()
    {
        var doc = new LightingSceneDoc
        {
            Objects = { Case("generic-case", "user", "fan:front:120:0", "fan:top:120:0"), new SceneObject { Id = "kb", Kind = "keyboard" } },
            Bindings = { Bind("fan-1", "generic-case", "fan:front:120:0"), Bind("fan-2", "generic-case", "fan:top:120:0"), Bind("kbd", "kb", "top") },
        };
        doc.Objects[1].Anchors.Add(new SceneAnchor { Id = "top" });
        var imported = Case("case", "build", "fan:front:120:0");
        imported.Position = [0, 0, 0];
        imported.Yaw = 0;

        var merged = SceneImport.Merge(doc, [imported], hasModel: true);

        Assert.Equal(["kb", "case"], merged.Objects.Select(o => o.Id));
        var pc = merged.Objects.Single(o => o.Id == "case");
        Assert.Equal(620f, pc.Position[0]);
        Assert.Equal(30f, pc.Yaw);
        Assert.True(pc.HasModel);
        Assert.Equal("case", merged.Bindings.Single(b => b.DeviceId == "fan-1").Targets[0].ObjectId);
        // The top slot is not in the import, so its placement goes.
        Assert.DoesNotContain(merged.Bindings, b => b.DeviceId == "fan-2");
        Assert.Contains(merged.Bindings, b => b.DeviceId == "kbd");
    }

    [Fact]
    public void Import_without_a_case_leaves_a_generic_case_alone()
    {
        var doc = new LightingSceneDoc { Objects = { Case("generic-case", "user", "fan:front:120:0") }, Bindings = { Bind("fan-1", "generic-case", "fan:front:120:0") } };
        var merged = SceneImport.Merge(doc, [new SceneObject { Id = "desk-strip", Kind = "strip" }], hasModel: false);
        Assert.Contains(merged.Objects, o => o.Id == "generic-case");
        Assert.Single(merged.Bindings);
    }
}
