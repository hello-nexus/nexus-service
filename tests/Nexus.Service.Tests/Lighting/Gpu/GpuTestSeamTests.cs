using Nexus.Service.Lighting.Engine.Gpu;
using Xunit;

namespace Nexus.Service.Tests.Lighting.Gpu;

/// <summary>
/// The seam only reads its variables in a DEV_TOOLS build, so these cover the
/// parsers rather than the environment. An unrecognised value must be inert:
/// a typo may not turn a customer's GPU off.
/// </summary>
public class GpuTestSeamTests
{
    // The mode enum is internal, so the expectation travels as its name.
    [Theory]
    [InlineData("fail", "Fail")]
    [InlineData("HANG", "Hang")]
    [InlineData(" crash ", "Crash")]
    [InlineData("crash-early", "CrashEarly")]
    [InlineData("context-error", "ContextError")]
    public void RecognisedModesParse(string value, string expected)
    {
        Assert.Equal(expected, GpuTestSeam.ParseMode(value).ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("success")]
    [InlineData("true")]
    [InlineData("1")]
    public void EverythingElseIsInert(string? value)
    {
        Assert.Equal("None", GpuTestSeam.ParseMode(value).ToString());
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("0", 0)]
    [InlineData(" 12 ", 12)]
    public void CountsParse(string value, int expected)
    {
        Assert.Equal(expected, GpuTestSeam.ParseCount(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("two")]
    public void UnusableCountsAreNull(string? value)
    {
        Assert.Null(GpuTestSeam.ParseCount(value));
    }
}
