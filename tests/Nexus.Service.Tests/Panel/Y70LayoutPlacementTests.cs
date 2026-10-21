using System.Collections.Generic;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Xunit;

namespace Nexus.Service.Tests.Panel;

public sealed class Y70LayoutPlacementTests
{
    [Fact]
    public void SpanForSize_FourByOneIsOneRowTall()
    {
        Assert.Equal((4, 1), Y70LayoutPlacement.SpanForSize("4x1"));
    }

    [Fact]
    public void Append_FourByOneStripsStackOnConsecutiveRows()
    {
        var pages = new List<PanelPageDto>();
        var first = new PanelWidgetDto { Id = "a", Type = "deck", Size = "4x1" };
        var second = new PanelWidgetDto { Id = "b", Type = "deck", Size = "4x1" };

        Assert.True(Y70LayoutPlacement.Append(pages, first, "4x1"));
        Assert.True(Y70LayoutPlacement.Append(pages, second, "4x1"));

        Assert.Single(pages);
        Assert.Equal((0, 0), (first.Col, first.Row));
        Assert.Equal((0, 1), (second.Col, second.Row));
    }

    [Fact]
    public void Append_TwoByTwoAfterAStripKeepsTheCoarseRowGrid()
    {
        var pages = new List<PanelPageDto>();
        var strip = new PanelWidgetDto { Id = "a", Type = "deck", Size = "4x1" };
        var tile = new PanelWidgetDto { Id = "b", Type = "clock", Size = "2x2" };

        Y70LayoutPlacement.Append(pages, strip, "4x1");
        Y70LayoutPlacement.Append(pages, tile, "2x2");

        Assert.Equal((0, 2), (tile.Col, tile.Row));
    }

    [Theory]
    [InlineData("1x1", 1, 1)]
    [InlineData("2x2", 2, 2)]
    [InlineData("2x4", 2, 4)]
    [InlineData("4x1", 4, 1)]
    [InlineData("4x2", 4, 2)]
    [InlineData("4x4", 4, 4)]
    [InlineData("2x2round", 2, 2)]
    [InlineData("4x8", 4, 8)]
    [InlineData("6x4", 4, 4)]
    [InlineData("8x8", 4, 8)]
    [InlineData("2x40", 4, 4)]
    [InlineData("garbage", 4, 4)]
    [InlineData("", 4, 4)]
    [InlineData("0x4", 4, 4)]
    [InlineData("-2x4", 4, 4)]
    [InlineData("4x", 4, 4)]
    [InlineData("4x4x4", 4, 4)]
    [InlineData(" 4x8", 4, 4)]
    [InlineData("+4x8", 4, 4)]
    [InlineData("04x8", 4, 4)]
    [InlineData("2x32", 2, 16)]
    [InlineData("2x33", 4, 4)]
    public void SpanForSize_ParsesAndClamps(string size, int cols, int rows)
    {
        Assert.Equal((cols, rows), Y70LayoutPlacement.SpanForSize(size));
    }

    [Fact]
    public void Append_NextToStoredFourByEightDoesNotOverlap()
    {
        var big = new PanelWidgetDto { Id = "a", Type = "webview", Size = "4x8", Col = 0, Row = 0 };
        var pages = new List<PanelPageDto> { new() { Id = "p", Widgets = { big } } };
        var tile = new PanelWidgetDto { Id = "b", Type = "clock", Size = "2x2" };

        Assert.True(Y70LayoutPlacement.Append(pages, tile, "2x2"));

        Assert.Single(pages);
        Assert.True(tile.Row >= 8);
    }
}
