using System.Collections.Generic;
using Nexus.Service.Platform.Linux;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Session selection against loginctl's own vocabulary. The fixtures are the
/// exact rows a Bazzite/Plasma box reported after a suspend/resume: the user's
/// Wayland session is <c>online</c>, not <c>active</c>, and selecting on
/// <c>Active=yes</c> left the root daemon with no session at all.
/// </summary>
public class LinuxSessionTests
{
    private static (string, Dictionary<string, string>) Session(
        string id, string type, string cls, string state, string user, string? display = null)
    {
        var props = new Dictionary<string, string>
        {
            ["Type"] = type, ["Class"] = cls, ["State"] = state, ["User"] = user,
        };
        if (display is not null) props["Display"] = display;
        return (id, props);
    }

    [Fact]
    public void SelectSession_AcceptsAnOnlineUserWaylandSession()
    {
        var sessions = new List<(string, Dictionary<string, string>)>
        {
            Session("164", "unspecified", "manager-early", "active", "970"),
            Session("183", "tty", "user", "active", "1000"),
            Session("2", "wayland", "user", "online", "1000"),
            Session("3", "unspecified", "manager", "active", "1000"),
            Session("c15", "unspecified", "background", "closing", "1000"),
            Session("c2", "unspecified", "background", "active", "970"),
        };

        var chosen = LinuxSession.SelectSession(sessions);

        Assert.Equal("2", chosen?.Id);
    }

    [Fact]
    public void SelectSession_PrefersActiveOverOnline()
    {
        var sessions = new List<(string, Dictionary<string, string>)>
        {
            Session("2", "wayland", "user", "online", "1000"),
            Session("5", "x11", "user", "active", "1001", ":0"),
        };

        Assert.Equal("5", LinuxSession.SelectSession(sessions)?.Id);
    }

    [Fact]
    public void SelectSession_SkipsGreeterClosingAndSystemUsers()
    {
        var sessions = new List<(string, Dictionary<string, string>)>
        {
            Session("c1", "wayland", "greeter", "active", "970"),
            Session("7", "wayland", "user", "active", "970"),
            Session("8", "wayland", "user", "closing", "1000"),
            Session("9", "tty", "user", "active", "1000"),
        };

        Assert.Null(LinuxSession.SelectSession(sessions));
    }
}
