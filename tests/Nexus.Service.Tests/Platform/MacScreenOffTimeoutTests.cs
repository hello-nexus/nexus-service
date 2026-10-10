using System.Diagnostics;
using System.Text.RegularExpressions;
using Nexus.Service.Platform.Mac;

namespace Nexus.Service.Tests.Platform;

public sealed class MacScreenOffTimeoutTests
{
    private static string Pmset(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("/usr/bin/pmset", args) { RedirectStandardOutput = true })!;
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return text;
    }

    [MacOnlyFact]
    public void Matches_the_displaysleep_minutes_pmset_reports_for_the_source_in_use()
    {
        var source = Regex.Match(Pmset("-g ps"), @"drawing from '([^']+)'");
        Assert.True(source.Success);
        // "pmset -g custom" lists each source's settings under "<source>:".
        var section = Regex.Match(Pmset("-g custom"), Regex.Escape(source.Groups[1].Value) + @":\s*\n((?:[ \t]+.*\n?)*)");
        Assert.True(section.Success);
        var minutes = Regex.Match(section.Groups[1].Value, @"displaysleep\s+(\d+)");
        Assert.True(minutes.Success);

        Assert.Equal(int.Parse(minutes.Groups[1].Value) * 60, MacScreenOffTimeout.Read());
    }
}
