using Nexus.Service.Lifecycle;
using Xunit;

namespace Nexus.Service.Tests.Lifecycle;

public class UpdaterSplashArgsTests
{
    [Theory]
    [InlineData("--updater-window", "3.1.3", "3.1.4-beta.1", true, "3.1.3", "3.1.4-beta.1")]
    [InlineData("--UPDATER-WINDOW", "1", "2", true, "1", "2")]
    [InlineData("--updater-window", null, null, true, "", "")]
    [InlineData("--helper", "1", "2", false, "", "")]
    public void TryParse_matches_flag_and_versions(string flag, string? from, string? to, bool ok, string expFrom, string expTo)
    {
        var args = new[] { flag, from, to }.Where(a => a is not null).Select(a => a!).ToArray();
        Assert.Equal(ok, UpdaterSplashArgs.TryParse(args, out var f, out var t));
        Assert.Equal(expFrom, f);
        Assert.Equal(expTo, t);
    }

    [Fact]
    public void TryParse_rejects_empty_args()
    {
        Assert.False(UpdaterSplashArgs.TryParse([], out _, out _));
    }

    [Theory]
    [InlineData("3.1.3", "3.1.4", "3.1.3 → 3.1.4")]
    [InlineData("", "3.1.4", "3.1.4")]
    [InlineData("3.1.3", "", "3.1.3")]
    public void VersionLine_omits_the_arrow_when_a_side_is_unknown(string from, string to, string expected)
    {
        Assert.Equal(expected, UpdaterSplashArgs.VersionLine(from, to));
    }
}
