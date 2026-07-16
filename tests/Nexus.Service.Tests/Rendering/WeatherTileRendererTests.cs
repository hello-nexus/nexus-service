using Nexus.Service.Rendering;
using Xunit;

namespace Nexus.Service.Tests.Rendering;

public class WeatherTileRendererTests
{
    [Theory]
    [InlineData(0, 72)]
    [InlineData(2, 80)]
    [InlineData(63, 96)]
    [InlineData(73, 72)]
    [InlineData(-1, 80)]
    public void Render_produces_a_square_non_empty_image_for_every_condition_group(int weatherCode, int pixelSize)
    {
        var input = new WeatherTileInput
        {
            TemperatureText = "72°",
            LocationLabel = "San Francisco",
            WeatherCode = weatherCode,
        };

        using var image = WeatherTileRenderer.Render(input, pixelSize);

        Assert.Equal(pixelSize, image.Width);
        Assert.Equal(pixelSize, image.Height);
    }

    [Fact]
    public void Render_resolvesTheLucideIcon_forEveryConditionGroup()
    {
        // Every mapped WMO group must resolve an embedded lucide glyph (a
        // missing resource would silently drop the icon), plus the unknown
        // fallback. Renders larger than background => the glyph drew.
        foreach (var code in new[] { 0, 1, 2, 3, 45, 51, 61, 71, 80, 95, -1 })
        {
            var input = new WeatherTileInput { WeatherCode = code };
            using var image = WeatherTileRenderer.Render(input, 96);
            var lit = 0;
            image.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < accessor.Height; y++)
                {
                    foreach (ref var px in accessor.GetRowSpan(y))
                    {
                        if (px.R > 40 || px.G > 40 || px.B > 40) { lit++; }
                    }
                }
            });
            Assert.True(lit > 0, $"weather code {code} rendered no glyph");
        }
    }

    [Fact]
    public void Render_withEmptyTextFields_stillProducesAnImageWithoutThrowing()
    {
        var input = new WeatherTileInput();

        using var image = WeatherTileRenderer.Render(input, 72);

        Assert.Equal(72, image.Width);
        Assert.Equal(72, image.Height);
    }
}
