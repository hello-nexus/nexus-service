using Nexus.Service.Peripherals.CorsairLink;
using Xunit;

namespace Nexus.Service.Tests.CorsairLink;

public class CorsairLinkFloorChannelsTests
{
    [Fact]
    public void A_fully_black_frame_is_sent_as_true_zeros()
    {
        var dst = new byte[] { 9, 9, 9, 9, 9, 9 };

        CorsairLinkHub.FloorChannels(new byte[6], dst);

        Assert.All(dst, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Black_pixels_in_a_lit_frame_are_floored_to_one()
    {
        var dst = new byte[6];

        CorsairLinkHub.FloorChannels(new byte[] { 0, 0, 0, 200, 0, 5 }, dst);

        Assert.Equal(new byte[] { 1, 1, 1, 200, 1, 5 }, dst);
    }
}
