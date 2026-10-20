using System.Linq;
using Nexus.Service.Peripherals.LianLiFlex;
using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests.LianLiFlex;

public class FlexSwatchCountTests
{
    [Theory]
    [InlineData(FlexFamily.TlFlex, "rainbow", 0)]
    [InlineData(FlexFamily.TlFlex, "runway", 2)]
    [InlineData(FlexFamily.TlFlex, "colorCycle", 3)]
    [InlineData(FlexFamily.TlFlex, "static", 4)]
    [InlineData(FlexFamily.SlInfFlex, "scan", 1)]
    [InlineData(FlexFamily.SlInfFlex, "taichi", 2)]
    [InlineData(FlexFamily.SlInfFlex, "wing", 4)]
    public void Modes_report_the_swatch_count_and_matching_default_list(FlexFamily family, string key, int count)
    {
        var mode = LianLiFlexRoutes.ModesOf(family).Single(m => m.Key == key);
        Assert.Equal(count, mode.ColorsMax);
        Assert.Equal(count, mode.DefaultColors.Length);
    }

    [Theory]
    [InlineData(FlexFamily.TlFlex)]
    [InlineData(FlexFamily.SlInfFlex)]
    public void Every_effect_has_a_count_within_the_swatch_cap(FlexFamily family)
    {
        foreach (var mode in LianLiFlexRoutes.ModesOf(family))
        {
            Assert.InRange(mode.ColorsMax, 0, 4);
            Assert.Equal(mode.ColorsMax, mode.DefaultColors.Length);
        }
    }

    [Theory]
    [InlineData("rainbow", 0)]
    [InlineData("static", 1)]
    [InlineData("runway", 2)]
    [InlineData("colorCycle", 3)]
    [InlineData("meteorShower", 4)]
    public void Sync_effects_use_their_own_counts(string key, int count) =>
        Assert.Equal(count, FlexSwatchCounts.OfSync(key));
}
