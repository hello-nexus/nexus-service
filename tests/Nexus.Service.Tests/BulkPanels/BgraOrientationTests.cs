using Nexus.Service.Panel.Streams;
using Nexus.Service.Peripherals.PixelFormats;
using Xunit;

namespace Nexus.Service.Tests.BulkPanels;

public class BgraOrientationTests
{
    // A 2x2 frame whose pixels carry their own index in every channel, so a transform is
    // readable as the order the indices come back in.
    private static byte[] Indexed(int width, int height)
    {
        var frame = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            frame[i * 4] = frame[(i * 4) + 1] = frame[(i * 4) + 2] = frame[(i * 4) + 3] = (byte)i;
        }
        return frame;
    }

    private static int[] Order(byte[] frame) =>
        System.Linq.Enumerable.Range(0, frame.Length / 4).Select(i => (int)frame[i * 4]).ToArray();

    [Theory]
    [InlineData(0, false, new[] { 0, 1, 2, 3 })]
    [InlineData(180, false, new[] { 3, 2, 1, 0 })]   // 180: reverse
    [InlineData(0, true, new[] { 1, 0, 3, 2 })]      // mirror: reverse within each row
    [InlineData(180, true, new[] { 2, 3, 0, 1 })]    // both: reverse row order only
    public void Transforms_compose_as_a_row_and_column_flip(int rotation, bool mirror, int[] expected)
    {
        var src = Indexed(2, 2);
        var dest = new byte[src.Length];

        BgraOrientation.Apply(src, 2, 2, rotation, mirror, dest);

        Assert.Equal(expected, Order(dest));
    }

    [Fact]
    public void Identity_is_only_the_untransformed_case()
    {
        Assert.True(BgraOrientation.IsIdentity(0, false));
        Assert.False(BgraOrientation.IsIdentity(180, false));
        Assert.False(BgraOrientation.IsIdentity(90, false));
        Assert.False(BgraOrientation.IsIdentity(0, true));
        // Flip plus mirror is a vertical flip, not a no-op.
        Assert.False(BgraOrientation.IsIdentity(180, true));
    }

    [Fact]
    public void Applying_a_transform_twice_returns_the_original()
    {
        var src = Indexed(4, 3);
        var once = new byte[src.Length];
        var twice = new byte[src.Length];

        BgraOrientation.Apply(src, 4, 3, rotation: 180, mirror: true, once);
        BgraOrientation.Apply(once, 4, 3, rotation: 180, mirror: true, twice);

        Assert.Equal(src, twice);
    }


    // Regression: the cache gate was `now - _readMs >= Ttl` seeded with long.MinValue,
    // which overflows negative, so the source was never read and every panel rendered
    // unturned however the record was set.
    [Fact]
    public void Filter_reads_its_source_on_the_very_first_frame()
    {
        var frame = Indexed(2, 2);
        var filter = new PanelOrientationFilter();
        int reads = 0;
        filter.Bind(() => { reads++; return (Rotation: 180, Mirror: false); });

        var oriented = filter.Apply(frame, 2, 2).ToArray();

        Assert.Equal(1, reads);
        Assert.Equal(new[] { 3, 2, 1, 0 }, Order(oriented));
    }

    [Fact]
    public void Filter_passes_the_frame_through_untouched_when_nothing_is_set()
    {
        var frame = Indexed(2, 2);
        var filter = new PanelOrientationFilter();
        filter.Bind(() => (Rotation: 0, Mirror: false));

        Assert.True(filter.Apply(frame, 2, 2).SequenceEqual(frame));
    }

    [Fact]
    public void Filter_without_a_source_is_a_passthrough()
    {
        var frame = Indexed(2, 2);

        Assert.True(new PanelOrientationFilter().Apply(frame, 2, 2).SequenceEqual(frame));
    }

    // A 3x3 frame with no symmetry, each pixel carrying its row-major index.
    private static readonly int[] Square = [0, 1, 2, 3, 4, 5, 6, 7, 8];

    // Each case is the oriented frame for a mirror-then-clockwise-turn of the 3x3 above, spelled out by hand.
    [Theory]
    [InlineData(0, false, new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 })]
    [InlineData(90, false, new[] { 6, 3, 0, 7, 4, 1, 8, 5, 2 })]
    [InlineData(180, false, new[] { 8, 7, 6, 5, 4, 3, 2, 1, 0 })]
    [InlineData(270, false, new[] { 2, 5, 8, 1, 4, 7, 0, 3, 6 })]
    [InlineData(0, true, new[] { 2, 1, 0, 5, 4, 3, 8, 7, 6 })]
    [InlineData(90, true, new[] { 8, 5, 2, 7, 4, 1, 6, 3, 0 })]
    [InlineData(180, true, new[] { 6, 7, 8, 3, 4, 5, 0, 1, 2 })]
    [InlineData(270, true, new[] { 0, 3, 6, 1, 4, 7, 2, 5, 8 })]
    public void Every_rotation_and_mirror_lands_each_pixel_where_the_turn_puts_it(int rotation, bool mirror, int[] expected)
    {
        var src = Indexed(3, 3);
        var dest = new byte[src.Length];

        BgraOrientation.Apply(src, 3, 3, rotation, mirror, dest);

        Assert.Equal(expected, Order(dest));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(90, false)]
    [InlineData(180, false)]
    [InlineData(270, false)]
    [InlineData(0, true)]
    [InlineData(90, true)]
    [InlineData(180, true)]
    [InlineData(270, true)]
    public void A_touch_maps_back_to_the_source_pixel_that_was_drawn_there(int rotation, bool mirror)
    {
        var src = Indexed(3, 3);
        var dest = new byte[src.Length];
        BgraOrientation.Apply(src, 3, 3, rotation, mirror, dest);
        var shown = Order(dest);

        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 3; x++)
            {
                var (sx, sy) = BgraOrientation.MapToSource(x, y, 3, 3, rotation, mirror);
                Assert.Equal(Square[(sy * 3) + sx], shown[(y * 3) + x]);
            }
        }
    }

    [Theory]
    [InlineData(90, 0)]
    [InlineData(270, 180)]
    [InlineData(180, 180)]
    [InlineData(0, 0)]
    public void A_rectangle_keeps_only_the_half_turn_component(int rotation, int expected)
    {
        Assert.Equal(expected, BgraOrientation.Effective(rotation, 4, 3));
    }

    [Fact]
    public void A_rectangle_is_never_quarter_turned()
    {
        var src = Indexed(4, 3);
        var dest = new byte[src.Length];

        BgraOrientation.Apply(src, 4, 3, 90, mirror: false, dest);

        Assert.Equal(Order(src), Order(dest));
        Assert.Equal((1, 2), BgraOrientation.MapToSource(1, 2, 4, 3, 90, mirror: false));
    }

    [Fact]
    public void The_filter_quarter_turns_a_square_frame()
    {
        var filter = new PanelOrientationFilter();
        filter.Bind(() => (Rotation: 90, Mirror: false));

        var oriented = filter.Apply(Indexed(3, 3), 3, 3).ToArray();

        Assert.Equal(new[] { 6, 3, 0, 7, 4, 1, 8, 5, 2 }, Order(oriented));
    }
}
