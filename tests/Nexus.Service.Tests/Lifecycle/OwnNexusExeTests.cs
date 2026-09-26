using Nexus.Service.Lifecycle;
using Xunit;

namespace Nexus.Service.Tests.Lifecycle;

public class OwnNexusExeTests
{
    private static readonly string?[] Own = { @"C:\Program Files\Nexus\Nexus.exe", null, "" };

    [Theory]
    [InlineData(@"C:\Program Files\Nexus\Nexus.exe")]
    [InlineData(@"c:\program files\nexus\NEXUS.EXE")]
    [InlineData(@"""C:\Program Files\Nexus\Nexus.exe""")]
    [InlineData("C:/Program Files/Nexus/Nexus.exe")]
    public void IsOwn_MatchesOurExeAcrossCaseQuotesAndSlashes(string path)
    {
        Assert.True(OwnNexusExe.IsOwn(path, Own));
    }

    [Theory]
    [InlineData(@"C:\Program Files (x86)\OtherVendor\Nexus.exe")]
    [InlineData(@"C:\Program Files\Nexus\tools\Nexus.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void IsOwn_RejectsAnyOtherFileAndEmptyPaths(string? path)
    {
        Assert.False(OwnNexusExe.IsOwn(path, Own));
    }

    [Fact]
    public void IsForeignImage_OnlyForAReadableOtherPath()
    {
        Assert.True(OwnNexusExe.IsForeignImage(@"C:\Program Files (x86)\OtherVendor\Nexus.exe", Own));
        Assert.False(OwnNexusExe.IsForeignImage(@"C:\Program Files\Nexus\Nexus.exe", Own));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsForeignImage_UnreadablePathIsTreatedAsOurs(string? path)
    {
        Assert.False(OwnNexusExe.IsForeignImage(path, Own));
    }

    [Theory]
    [InlineData(@"""C:\Program Files\Nexus\Nexus.exe"" --helper", true)]
    [InlineData(@"""C:\Program Files\Nexus\Nexus.exe"" --tray", true)]
    [InlineData(@"C:\Program Files\Nexus\Nexus.exe --helper", true)]
    [InlineData(@"""C:\Program Files (x86)\OtherVendor\Nexus.exe"" autostart", false)]
    [InlineData(@"C:\Program Files (x86)\OtherVendor\Nexus.exe autostart", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsOwnCommand_ReadsTheExeOutOfARunCommandLine(string? command, bool expected)
    {
        Assert.Equal(expected, OwnNexusExe.IsOwnCommand(command, Own));
    }
}
