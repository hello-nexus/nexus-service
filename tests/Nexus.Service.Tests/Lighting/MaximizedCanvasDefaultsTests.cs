using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Models.Devices;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting;

/// <summary>
/// Only an install whose settings.json the store created defaults unplaced
/// devices to the whole canvas; any pre-existing document keeps the grid.
/// </summary>
public sealed class MaximizedCanvasDefaultsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public MaximizedCanvasDefaultsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-maxcanvas-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Fresh_install_sets_the_flag_and_it_survives_a_reopen()
    {
        using (var store = new JsonConfigStore(_path))
        {
            Assert.True(store.Load().MaximizedCanvasDefaults);
            store.FlushNow();
        }

        using var reopened = new JsonConfigStore(_path);
        Assert.True(reopened.Load().MaximizedCanvasDefaults);
    }

    [Fact]
    public void Existing_current_schema_file_without_the_flag_keeps_it_off()
    {
        File.WriteAllText(_path, $$"""{ "schemaVersion": {{NexusSettings.CurrentSchemaVersion}} }""");

        using var store = new JsonConfigStore(_path);
        Assert.False(store.Load().MaximizedCanvasDefaults);
    }

    [Fact]
    public void Existing_older_schema_file_keeps_it_off_through_migration()
    {
        File.WriteAllText(_path, """{ "schemaVersion": 11 }""");

        using var store = new JsonConfigStore(_path);
        Assert.False(store.Load().MaximizedCanvasDefaults);
    }

    [Fact]
    public void Corrupt_file_keeps_it_off()
    {
        File.WriteAllText(_path, "{ this is not valid json ");

        using var store = new JsonConfigStore(_path);
        Assert.False(store.Load().MaximizedCanvasDefaults);
    }

    [Fact]
    public void Flagged_install_puts_unplaced_cards_on_the_whole_canvas_and_leaves_placed_ones()
    {
        var settings = new NexusSettings { MaximizedCanvasDefaults = true };
        settings.Lighting.DeviceLayouts["placed"] = new DeviceLayout { X = 5, Y = 6, W = 70, H = 80 };
        var devices = Cards("placed", "a", "b");
        devices[0].CanvasX = 5; devices[0].CanvasY = 6; devices[0].CanvasW = 70; devices[0].CanvasH = 80;

        CompositeLightingDeviceProvider.ApplyDefaultLayouts(devices, settings);

        Assert.Equal((5f, 6f, 70f, 80f), Rect(devices[0]));
        Assert.Equal((0f, 0f, 1000f, 600f), Rect(devices[1]));
        Assert.Equal((0f, 0f, 1000f, 600f), Rect(devices[2]));
    }

    [Fact]
    public void Unflagged_install_keeps_grid_slots()
    {
        var settings = new NexusSettings();
        var devices = Cards("a", "b", "c");

        CompositeLightingDeviceProvider.ApplyDefaultLayouts(devices, settings);

        for (var i = 0; i < devices.Count; i++)
        {
            Assert.Equal(CanvasGridLayout.Slot(i, devices.Count), Rect(devices[i]));
        }
    }

    [Fact]
    public void MaximizeUnplaced_skips_placed_frames()
    {
        var placed = new DeviceFrame(0, "placed", 4, x: 10, y: 20, w: 140, h: 120);
        var unplaced = new DeviceFrame(1, "unplaced", 4, x: 10, y: 20, w: 140, h: 120);

        CanvasGridLayout.MaximizeUnplaced(
            new[] { placed, unplaced }, new Dictionary<string, DeviceLayout> { ["placed"] = new DeviceLayout() });

        Assert.Equal((10f, 20f, 140f, 120f), (placed.X, placed.Y, placed.W, placed.H));
        Assert.Equal((0f, 0f, 1000f, 600f), (unplaced.X, unplaced.Y, unplaced.W, unplaced.H));
    }

    private static List<LightingDevice> Cards(params string[] ids) =>
        ids.Select(id => new LightingDevice { Id = id, LedCount = 4 }).ToList();

    private static (float, float, float, float) Rect(LightingDevice d) => (d.CanvasX, d.CanvasY, d.CanvasW, d.CanvasH);
}
