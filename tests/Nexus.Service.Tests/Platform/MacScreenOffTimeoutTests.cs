using System.Diagnostics;
using System.Text.RegularExpressions;
using Nexus.Service.Platform.Mac;

namespace Nexus.Service.Tests.Platform;

public sealed class MacScreenOffTimeoutTests
{
    [MacOnlyFact]
    public void Matches_the_displaysleep_minutes_pmset_reports()
    {
        using var p = Process.Start(new ProcessStartInfo("/usr/bin/pmset", "-g") { RedirectStandardOutput = true })!;
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        var m = Regex.Match(text, @"displaysleep\s+(\d+)");
        Assert.True(m.Success);

        Assert.Equal(int.Parse(m.Groups[1].Value) * 60, MacScreenOffTimeout.Read());
    }
}
