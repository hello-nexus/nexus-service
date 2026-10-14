using Nexus.Service.QSeries;
using Xunit;

namespace Nexus.Service.Tests.QSeries;

/// <summary>A failing automatic qshell install backs off instead of rerunning adb install every minute.</summary>
public class QshellAutoInstallTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 8)]
    public void Spacing_doubles_per_attempt_and_caps(int attempts, int minutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(minutes), QSeriesPortWatcher.QshellAutoInstallSpacing(attempts));
    }
}
