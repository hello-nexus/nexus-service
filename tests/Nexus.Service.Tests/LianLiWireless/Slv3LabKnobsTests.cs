using Nexus.Service.Lighting;
using Xunit;

namespace Nexus.Service.Tests.LianLiWireless;

public class Slv3LabKnobsTests
{
    [Fact]
    public void Parse_reads_every_knob_and_clamps_ranges()
    {
        Assert.True(Slv3LabKnobs.TryParse(
            """{"resend":false,"forcedExtraUploads":99,"windowFrames":2,"stretch":9,"cycleMs":5,"targetMac":"64f271e566e1","livePath":true,"extraSameIndex":true}""",
            out var k));
        Assert.False(k.Resend);
        Assert.Equal(Slv3LabKnobs.MaxExtraUploads, k.ForcedExtraUploads);
        Assert.Equal(Slv3LabKnobs.MinWindowFrames, k.WindowFrames);
        Assert.Equal(6, k.Stretch);
        Assert.Equal(150, k.CycleMs);
        Assert.Equal("64F271E566E1", k.TargetMac);
        Assert.True(k.LivePath);
        Assert.True(k.ExtraSameIndex);
    }

    [Fact]
    public void Parse_empty_object_is_stock_and_garbage_is_rejected()
    {
        Assert.True(Slv3LabKnobs.TryParse("{}", out var k));
        Assert.Equal(Slv3LabKnobs.Default, k);
        Assert.False(Slv3LabKnobs.TryParse("{nope", out _));
        Assert.False(Slv3LabKnobs.TryParse("[1]", out _));
    }

    [Theory]
    [InlineData("", "64F271E566E1", true)]
    [InlineData("64F271E566E1", "64f271e566e1", true)]
    [InlineData("64F271E566E1", "884713506CE1", false)]
    public void Targets_matches_the_chain_or_everything_when_unset(string target, string mac, bool expected)
    {
        var k = new Slv3LabKnobs { TargetMac = target };
        Assert.Equal(expected, k.Targets(mac));
    }
}
