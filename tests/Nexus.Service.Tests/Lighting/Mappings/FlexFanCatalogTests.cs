using System.Linq;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Peripherals.LianLiFlex;

namespace Nexus.Service.Tests.Lighting.Mappings;

public class FlexFanCatalogTests
{
    // The catalog grid is 200 cells, so a cell center sits within half a cell of the exact position.
    private const double Tol = 0.004;

    [Theory]
    [InlineData("product:lianli-lian-li-tl-lcd-flex", FlexFamily.TlFlex, 26)]
    [InlineData("product:lianli-lian-li-sl-inf-lcd-flex", FlexFamily.SlInfFlex, 44)]
    public void Flex_fan_entry_follows_the_wire_order_layout(string key, FlexFamily family, int leds)
    {
        var artifact = BuiltInMappingsCatalog.Find(key);
        Assert.NotNull(artifact);
        var zone = Assert.Single(artifact!.Zones);
        Assert.Equal(leds, zone.LedCount);
        Assert.Equal(leds, zone.Leds.Count);
        for (var i = 0; i < leds; i++)
        {
            var led = zone.Leds[i];
            var expected = FlexFanLayout.Position(family, i);
            Assert.Equal(i, led.I);
            Assert.InRange(led.U, expected.U - Tol, expected.U + Tol);
            Assert.InRange(led.V, expected.V - Tol, expected.V + Tol);
        }
    }

    [Fact]
    public void Flex_fans_are_listed_as_fans_with_their_led_counts()
    {
        var rows = BuiltInMappingsCatalog.Search("lcd flex", "Fan", 20);
        Assert.Contains(rows, r => r.Key == "product:lianli-lian-li-tl-lcd-flex" && r.LedCount == 26);
        Assert.Contains(rows, r => r.Key == "product:lianli-lian-li-sl-inf-lcd-flex" && r.LedCount == 44);
    }
}
