using Nexus.Service.Lifecycle;
using Xunit;

namespace Nexus.Service.Tests.Lifecycle;

public class SystemdUnitTests
{
    [Fact]
    public void Unified_cgroup_of_the_unit_matches()
    {
        Assert.True(SystemdUnit.IsNexusUnit(new[] { "0::/system.slice/nexus.service" }));
    }

    [Fact]
    public void Hybrid_cgroup_lines_match_through_the_systemd_controller_line()
    {
        var lines = new[]
        {
            "12:pids:/system.slice/nexus.service",
            "1:name=systemd:/system.slice/nexus.service",
            "0::/system.slice/nexus.service",
        };
        Assert.True(SystemdUnit.IsNexusUnit(lines));
    }

    [Fact]
    public void A_user_session_unit_of_the_same_name_matches()
    {
        Assert.True(SystemdUnit.IsNexusUnit(new[] { "0::/user.slice/user-1000.slice/user@1000.service/app.slice/nexus.service" }));
    }

    [Theory]
    [InlineData("0::/user.slice/user-1000.slice/user@1000.service/app.slice/app-gnome-gnome\\x2dterminal-4242.scope")]
    [InlineData("0::/user.slice/user-1000.slice/session-3.scope")]
    [InlineData("0::/system.slice/not-nexus.service")]
    [InlineData("0::/system.slice/nexus.service.d")]
    [InlineData("0::/")]
    [InlineData("garbage")]
    [InlineData("")]
    public void A_terminal_a_session_or_another_unit_does_not_match(string line)
    {
        Assert.False(SystemdUnit.IsNexusUnit(new[] { line }));
    }

    [Fact]
    public void A_terminal_that_inherited_the_environment_of_a_user_service_does_not_match()
    {
        var lines = new[]
        {
            "1:name=systemd:/user.slice/user-1000.slice/user@1000.service/app.slice/app-org.gnome.Terminal.slice/vte-spawn-1.scope",
            "0::/user.slice/user-1000.slice/user@1000.service/app.slice/app-org.gnome.Terminal.slice/vte-spawn-1.scope",
        };
        Assert.False(SystemdUnit.IsNexusUnit(lines));
    }
}
