using Nexus.Service.Helper;
using Xunit;

namespace Nexus.Service.Tests.Hotkeys;

public sealed class ConsolePreferenceTests
{
    [Fact]
    public void Prefers_the_console_helper_and_does_not_look_for_another()
    {
        var anyCalls = 0;
        var picked = ConsolePreference.Pick("console", () => { anyCalls++; return "other"; });
        Assert.Equal("console", picked);
        Assert.Equal(0, anyCalls);
    }

    [Fact]
    public void Falls_back_to_any_helper_when_the_console_has_none()
    {
        Assert.Equal("rdp", ConsolePreference.Pick<string>(null, () => "rdp"));
    }

    [Fact]
    public void Returns_null_when_no_helper_exists()
    {
        Assert.Null(ConsolePreference.Pick<string>(null, () => null));
    }
}
