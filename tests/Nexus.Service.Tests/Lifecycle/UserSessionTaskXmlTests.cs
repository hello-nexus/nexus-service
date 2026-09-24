using Nexus.Service.Lifecycle;
using Xunit;

namespace Nexus.Service.Tests.Lifecycle;

public class UserSessionTaskXmlTests
{
    [Theory]
    [InlineData("taskmgr.exe", "taskmgr.exe", "")]
    [InlineData("\"C:\\Program Files\\Nexus\\Nexus.exe\" --helper", "C:\\Program Files\\Nexus\\Nexus.exe", "--helper")]
    [InlineData("\"C:\\Program Files\\Nexus\\Nexus.exe\"", "C:\\Program Files\\Nexus\\Nexus.exe", "")]
    [InlineData("rundll32.exe user32.dll,LockWorkStation", "rundll32.exe", "user32.dll,LockWorkStation")]
    public void SplitsCommandIntoExeAndArgs(string command, string exe, string args)
    {
        var split = UserSessionTaskXml.SplitCommand(command);

        Assert.Equal(exe, split.Exe);
        Assert.Equal(args, split.Args);
    }

    // The whole point of the XML form: a task with no trigger runs only from an
    // explicit schtasks /Run, so a leftover task can never fire on a wall clock.
    [Fact]
    public void EmitsNoTrigger()
    {
        var xml = UserSessionTaskXml.Build("Bruno", "taskmgr.exe");

        Assert.Contains("<Triggers />", xml);
        Assert.DoesNotContain("<CalendarTrigger", xml);
        Assert.DoesNotContain("<TimeTrigger", xml);
        Assert.DoesNotContain("<BootTrigger", xml);
        Assert.DoesNotContain("<LogonTrigger", xml);
    }

    // InteractiveToken is the XML equivalent of schtasks /IT, and is what puts
    // the process in the console user's session.
    [Fact]
    public void RunsAsTheUserWithAnInteractiveToken()
    {
        var xml = UserSessionTaskXml.Build("Bruno", "taskmgr.exe");

        Assert.Contains("<UserId>Bruno</UserId>", xml);
        Assert.Contains("<LogonType>InteractiveToken</LogonType>", xml);
        Assert.Contains("<RunLevel>LeastPrivilege</RunLevel>", xml);
    }

    [Fact]
    public void ElevatedAsksForTheHighestAvailableToken()
    {
        var xml = UserSessionTaskXml.Build("Bruno", "taskmgr.exe", elevated: true);

        Assert.Contains("<RunLevel>HighestAvailable</RunLevel>", xml);
        Assert.Contains("<LogonType>InteractiveToken</LogonType>", xml);
        Assert.Contains("<Triggers />", xml);
    }

    [Fact]
    public void SplitsQuotedExePathIntoCommandAndArguments()
    {
        var xml = UserSessionTaskXml.Build("Bruno", "\"C:\\Program Files\\Nexus\\Nexus.exe\" --helper");

        Assert.Contains("<Command>C:\\Program Files\\Nexus\\Nexus.exe</Command>", xml);
        Assert.Contains("<Arguments>--helper</Arguments>", xml);
    }

    [Fact]
    public void OmitsArgumentsWhenThereAreNone()
    {
        var xml = UserSessionTaskXml.Build("Bruno", "taskmgr.exe");

        Assert.DoesNotContain("<Arguments>", xml);
    }

    // A username or path carrying XML metacharacters must not break the
    // document; schtasks rejects malformed XML outright.
    [Fact]
    public void EscapesXmlMetacharacters()
    {
        var xml = UserSessionTaskXml.Build("DOMAIN\\O'Brien & Sons", "\"C:\\a<b>c\\x.exe\" --q=\"1\"");

        Assert.Contains("<UserId>DOMAIN\\O&apos;Brien &amp; Sons</UserId>", xml);
        Assert.Contains("&lt;b&gt;", xml);
        Assert.DoesNotContain("<b>", xml);
    }

    // Sigma's "Suspicious Schtasks Schedule Types" matches ' ONCE ' on the
    // command line. The XML form carries no schedule token at all.
    [Fact]
    public void CarriesNoScheduleType()
    {
        var xml = UserSessionTaskXml.Build("Bruno", "taskmgr.exe");

        Assert.DoesNotContain("ONCE", xml);
        Assert.DoesNotContain("ONLOGON", xml);
        Assert.DoesNotContain("ONSTART", xml);
        Assert.DoesNotContain("ONIDLE", xml);
    }
}
