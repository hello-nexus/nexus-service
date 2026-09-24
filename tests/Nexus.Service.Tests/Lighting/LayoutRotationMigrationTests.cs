using System.Collections.Generic;
using Nexus.Service.Lighting;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Lighting;

public class LayoutRotationMigrationTests
{
    [Fact]
    public void Apply_UnswapsQuarterTurnedLayoutsAboutTheirCentre_Once()
    {
        var lighting = new LightingSettings
        {
            DeviceLayouts = new Dictionary<string, DeviceLayout>
            {
                ["tall"] = new() { X = 300, Y = 100, W = 40, H = 250, Rotation = 90 },
                ["flat"] = new() { X = 10, Y = 20, W = 200, H = 50, Rotation = 180 },
            },
            LayoutPresets = new List<LayoutPreset>
            {
                new() { Id = "p", Layouts = new Dictionary<string, DeviceLayout> { ["tall"] = new() { X = 0, Y = 0, W = 40, H = 250, Rotation = 270 } } },
            },
        };

        LayoutRotationMigration.Apply(lighting);

        var tall = lighting.DeviceLayouts["tall"];
        Assert.Equal((195f, 205f, 250f, 40f), (tall.X, tall.Y, tall.W, tall.H));
        var flat = lighting.DeviceLayouts["flat"];
        Assert.Equal((10f, 20f, 200f, 50f), (flat.X, flat.Y, flat.W, flat.H));
        var preset = lighting.LayoutPresets[0].Layouts["tall"];
        Assert.Equal((-105f, 105f, 250f, 40f), (preset.X, preset.Y, preset.W, preset.H));
        Assert.True(lighting.FreeRotationLayouts);

        LayoutRotationMigration.Apply(lighting);
        Assert.Equal(250f, lighting.DeviceLayouts["tall"].W);
    }

    [Fact]
    public void Apply_LeavesAMigratedDocumentAlone()
    {
        var lighting = new LightingSettings
        {
            FreeRotationLayouts = true,
            DeviceLayouts = new Dictionary<string, DeviceLayout> { ["a"] = new() { W = 40, H = 250, Rotation = 90 } },
        };

        LayoutRotationMigration.Apply(lighting);

        Assert.Equal(40f, lighting.DeviceLayouts["a"].W);
    }
}
