using System.Text.Json;
using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests.Routes;

public class PcSetupSaveLogTests
{
    private static JsonElement? Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Aio_lists_pump_and_radiator_in_millimeters()
    {
        var layout = Json("""{"boxes":[{"slot":"cooler","role":"part","size":[700,700,500]},{"slot":"cooler","role":"part","size":[3600,520,1200],"dims":[{"key":"radiator","mm":360}]},{"slot":"gpu","role":"part","size":[1,1,1]}]}""");
        Assert.Equal("pump 70x70x50 + rad 360x52x120", PcSetupSaveLog.CoolerSummary(layout));
    }

    [Fact]
    public void Tower_lists_one_cooler_box()
    {
        var layout = Json("""{"boxes":[{"slot":"cooler","role":"part","size":[1050,1260,1550],"dims":[{"key":"height","mm":155}]}]}""");
        Assert.Equal("cooler 105x126x155", PcSetupSaveLog.CoolerSummary(layout));
    }

    [Theory]
    [InlineData("""[1,2]""", "cooler none")]
    [InlineData("""{"boxes":5}""", "cooler none")]
    [InlineData("""{"boxes":[{"slot":"cooler","role":"part","size":["a",1,2]},7]}""", "cooler none")]
    public void Malformed_layout_never_throws(string json, string expected) =>
        Assert.Equal(expected, PcSetupSaveLog.CoolerSummary(Json(json)));

    [Fact]
    public void Null_layout_reads_as_none() => Assert.Equal("cooler none", PcSetupSaveLog.CoolerSummary(null));

    [Fact]
    public void Confirm_keys_are_counted_and_listed()
    {
        var parts = Json("""{"devices":{"confirm:a":1,"cooler-confirm:b":2,"other":3}}""");
        Assert.Equal("2[confirm:a,cooler-confirm:b]", PcSetupSaveLog.ConfirmSummary(parts));
        Assert.Equal("0", PcSetupSaveLog.ConfirmSummary(Json("""{"devices":[1]}""")));
        Assert.Equal("0", PcSetupSaveLog.ConfirmSummary(null));
    }
}
