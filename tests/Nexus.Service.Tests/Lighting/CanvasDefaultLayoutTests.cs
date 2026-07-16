using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting;

/// <summary>
/// Invariant: every default canvas-frame slot must land inside the 1000×600
/// unit canvas with at least PAD=12 inset from every edge. The web-side drag
/// clamp uses exactly that PAD, so a default position outside it snaps on the
/// user's first interaction and the persisted layout drifts. These tests guard
/// against the regression the previous "newly-attached device disappears off
/// the bottom" bug was about.
/// </summary>
public class CanvasDefaultLayoutTests
{
    private const float CanvasW = 1000f;
    private const float CanvasH = 600f;
    private const float Pad = 12f;

    [Theory]
    [InlineData(0)]
    [InlineData(17)]   // last slot before wrap (Cols=3 × Rows=6 = 18)
    [InlineData(18)]   // wraps back to slot 0
    [InlineData(35)]
    [InlineData(99)]
    public void DefaultCardLayout_stays_inside_drag_clamp(int slot)
    {
        var (x, y, w, h) = OpenRgbLightingDeviceProvider.DefaultCardLayout(slot);
        AssertInsideCanvas(x, y, w, h);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]    // last slot before wrap (Cols=2 × Rows=3 = 6)
    [InlineData(6)]    // wraps
    [InlineData(11)]
    [InlineData(50)]
    public void DefaultStripLayout_stays_inside_drag_clamp(int slot)
    {
        var (x, y, w, h) = OpenRgbLightingDeviceProvider.DefaultStripLayout(slot);
        AssertInsideCanvas(x, y, w, h);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]    // last slot before wrap (Cols=4 × Rows=2 = 8)
    [InlineData(8)]    // wraps
    [InlineData(15)]
    [InlineData(99)]
    public void DefaultNp50Layout_stays_inside_drag_clamp(int slot)
    {
        var (x, y, w, h) = Np50LightingDeviceProvider.DefaultNp50Layout(slot);
        AssertInsideCanvas(x, y, w, h);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]    // last slot before wrap (Cols=4 × Rows=2 = 8)
    [InlineData(8)]    // wraps
    [InlineData(15)]
    [InlineData(99)]
    public void DefaultMiniHubLayout_stays_inside_drag_clamp(int slot)
    {
        var (x, y, w, h) = MiniHubLightingDeviceProvider.DefaultMiniHubLayout(slot);
        AssertInsideCanvas(x, y, w, h);
    }

    private static void AssertInsideCanvas(float x, float y, float w, float h)
    {
        Assert.InRange(x, Pad, CanvasW - Pad - w);
        Assert.InRange(y, Pad, CanvasH - Pad - h);
    }

    // Unified-grid invariants: every slot of every reasonable totalCount must (a) stay
    // inside the drag-legal canvas and (b) not overlap any other slot's rect for the
    // same totalCount. Covers the user's "stack evenly, NEVER outside the canvas" rule.
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(12)]
    [InlineData(18)]
    [InlineData(30)]
    [InlineData(50)]
    [InlineData(100)]
    public void CanvasGridLayout_slots_stay_inside_canvas(int totalCount)
    {
        for (var i = 0; i < totalCount; i++)
        {
            var (x, y, w, h) = Nexus.Service.Lighting.CanvasGridLayout.Slot(i, totalCount);
            AssertInsideCanvas(x, y, w, h);
        }
    }

    /// <summary>
    /// The UI draws a device's name at its card's center, so cards sharing a row
    /// centre line put every name on one line and long names collide. Models a
    /// long name ("B850I AORUS PRO - ARGB_V2_1" measures ~250 units) at each
    /// card's center and requires the default layout to keep them apart, so a
    /// layout reset lands on a readable canvas.
    ///
    /// Counts up to the cutoff only: at 29 the grid widens to 8 columns, cellW
    /// drops to 122, and same-parity columns two apart sit 244 units apart -
    /// closer than a label is wide, which no vertical offset can fix. The canvas
    /// de-collides labels at render time, which is what covers that tail.
    /// Counts below 7 are omitted: their cells are wider than a label, so they
    /// pass with or without the stagger and would not catch a regression.
    /// </summary>
    [Theory]
    [InlineData(7)]
    [InlineData(12)]
    [InlineData(18)]
    [InlineData(28)]
    public void CanvasGridLayout_default_slots_keep_device_names_apart(int totalCount)
    {
        const float labelW = 250f;
        const float labelH = 22f;
        var labels = new (float x, float y)[totalCount];
        for (var i = 0; i < totalCount; i++)
        {
            var (x, y, w, h) = CanvasGridLayout.Slot(i, totalCount);
            labels[i] = (x + w * 0.5f, y + h * 0.5f);
        }
        for (var i = 0; i < totalCount; i++)
        for (var j = i + 1; j < totalCount; j++)
        {
            var a = labels[i];
            var b = labels[j];
            var overlap = MathF.Abs(a.x - b.x) < labelW && MathF.Abs(a.y - b.y) < labelH;
            Assert.False(overlap,
                $"name {i} at {a} overlaps name {j} at {b} at totalCount={totalCount}");
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(12)]
    [InlineData(30)]
    public void CanvasGridLayout_slots_do_not_overlap(int totalCount)
    {
        var rects = new (float x, float y, float w, float h)[totalCount];
        for (var i = 0; i < totalCount; i++)
            rects[i] = Nexus.Service.Lighting.CanvasGridLayout.Slot(i, totalCount);
        for (var i = 0; i < totalCount; i++)
        for (var j = i + 1; j < totalCount; j++)
        {
            var a = rects[i];
            var b = rects[j];
            var overlap = !(a.x + a.w <= b.x || b.x + b.w <= a.x || a.y + a.h <= b.y || b.y + b.h <= a.y);
            Assert.False(overlap, $"slot {i} {a} overlaps slot {j} {b} at totalCount={totalCount}");
        }
    }
}
