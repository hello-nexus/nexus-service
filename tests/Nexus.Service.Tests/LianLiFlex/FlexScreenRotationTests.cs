using Nexus.Service.Peripherals.LianLiFlex;
using Xunit;

namespace Nexus.Service.Tests.LianLiFlex;

public class FlexScreenRotationTests
{
    [Theory]
    [InlineData(FlexFamily.TlFlex, 1, 0, 0)]
    [InlineData(FlexFamily.TlFlex, 11, 1, 1)]
    [InlineData(FlexFamily.SlInfFlex, 1, 0, 0)]
    [InlineData(FlexFamily.SlInfFlex, 1, 3, 3)]
    [InlineData(FlexFamily.SlInfFlex, 11, 0, 2)]
    [InlineData(FlexFamily.SlInfFlex, 11, 1, 3)]
    [InlineData(FlexFamily.SlInfFlex, 11, 2, 0)]
    [InlineData(FlexFamily.SlInfFlex, 11, 3, 1)]
    public void Only_a_right_attached_sl_inf_turns_the_rotation_a_half_turn(FlexFamily family, int fanNum, int stored, int expected)
    {
        Assert.Equal((byte)expected, FlexScreens.HardwareRotationFor(family, fanNum, (byte)stored));
    }
}
