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

    [Theory]
    [InlineData("nicol", "T1")]
    [InlineData("Bruno", "BRUNO-PC")]
    public void KeepsTheBareNameForALocalUserWithoutASessionAccount(string username, string machine)
    {
        var principal = UserSessionTaskXml.TaskPrincipal(username, machine, () => null);

        Assert.Equal((username, username), principal);
    }

    [Fact]
    public void UsesTheSessionSidForAnEntraUser()
    {
        var principal = UserSessionTaskXml.TaskPrincipal(
            "jdoe", "LAPTOP-1", () => ("S-1-12-1-3564427996-1263477795-192989575-2144979830", "AzureAD\\JaneDoe", "jdoe"),
            "AzureAD");

        Assert.Equal(("S-1-12-1-3564427996-1263477795-192989575-2144979830", "AzureAD\\JaneDoe"), principal);
    }

    [Fact]
    public void QualifiesWithTheWtsDomainWhenTheSessionAccountIsUnavailable()
    {
        var principal = UserSessionTaskXml.TaskPrincipal("jdoe", "LAPTOP-1", () => null, "AzureAD");

        Assert.Equal(("AzureAD\\jdoe", "AzureAD\\jdoe"), principal);
    }

    [Fact]
    public void DoesNotDoubleQualifyAUserNameThatAlreadyHasADomain()
    {
        var principal = UserSessionTaskXml.TaskPrincipal("CORP\\USER", "USER", () => null, "CORP");

        Assert.Equal(("CORP\\USER", "CORP\\USER"), principal);
    }

    [Theory]
    [InlineData("USER", "USER")]
    [InlineData("user", "USER")]
    public void UsesTheSessionAccountWhenTheUserIsNamedLikeTheComputer(string username, string machine)
    {
        var principal = UserSessionTaskXml.TaskPrincipal(
            username, machine, () => ("S-1-12-1-1-2-3-4", "AzureAD\\USER", "USER"));

        Assert.Equal(("S-1-12-1-1-2-3-4", "AzureAD\\USER"), principal);
    }

    [Fact]
    public void KeepsTheSidWhenTheAccountNameDoesNotResolve()
    {
        var principal = UserSessionTaskXml.TaskPrincipal(
            "USER", "USER", () => ("S-1-5-21-1-2-3-1001", null, "USER"));

        Assert.Equal(("S-1-5-21-1-2-3-1001", "USER\\USER"), principal);
    }

    // The caller read the user name earlier; a session switched since then must
    // not hand the task another user's account.
    [Fact]
    public void IgnoresASessionAccountThatBelongsToAnotherUser()
    {
        var principal = UserSessionTaskXml.TaskPrincipal(
            "USER", "USER", () => ("S-1-5-21-1-2-3-1002", "USER\\bruno", "bruno"));

        Assert.Equal(("USER\\USER", "USER\\USER"), principal);
    }

    [Fact]
    public void QualifiesWithTheComputerWhenTheSessionAccountIsUnavailable()
    {
        var principal = UserSessionTaskXml.TaskPrincipal("user", "USER", () => null);

        Assert.Equal(("USER\\user", "USER\\user"), principal);
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
