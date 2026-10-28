using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Nexus.Service.Helper;
using Xunit;

namespace Nexus.Service.Tests.Helper;

public class HelperPollerDiagnosticsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_Empty_ForBlank(string? value)
    {
        Assert.Empty(HelperPollerDiagnostics.Parse(value));
    }

    [Fact]
    public void Parse_SplitsOnCommaSpaceAndSemicolon()
    {
        var set = HelperPollerDiagnostics.Parse("windowset, screentime;audio watchdog lockinput");
        Assert.Equal(HelperPollerDiagnostics.Known.Length, set.Count);
        foreach (var known in HelperPollerDiagnostics.Known)
        {
            Assert.Contains(known, set);
        }
    }

    [Fact]
    public void Parse_IsCaseInsensitive()
    {
        var set = HelperPollerDiagnostics.Parse("WindowSet");
        Assert.Contains(HelperPollerDiagnostics.WindowSet, set);
    }

    // A typo must survive parsing so the startup log line shows it verbatim,
    // rather than reporting "all pollers enabled" for a list the user believes
    // took effect.
    [Fact]
    public void Parse_KeepsUnknownNames()
    {
        var set = HelperPollerDiagnostics.Parse("windowsett");
        Assert.Contains("windowsett", set);
        Assert.DoesNotContain(HelperPollerDiagnostics.WindowSet, set);
    }

    [Fact]
    public void FormatStartupLine_ReportsNoneDisabled()
    {
        Assert.Equal("[helper-perf] all pollers enabled",
            HelperPollerDiagnostics.FormatStartupLine(HelperPollerDiagnostics.Parse(null)));
    }

    [Fact]
    public void FormatStartupLine_ListsDisabled()
    {
        var line = HelperPollerDiagnostics.FormatStartupLine(HelperPollerDiagnostics.Parse("audio"));
        Assert.Equal("[helper-perf] disabled: audio", line);
    }

    // A typo must not read as "that poller was ruled out"; the line has to say
    // the name matched nothing and list what would have.
    [Fact]
    public void FormatStartupLine_CallsOutUnrecognizedNames()
    {
        var line = HelperPollerDiagnostics.FormatStartupLine(HelperPollerDiagnostics.Parse("windowsett"));
        Assert.Contains("all pollers enabled", line);
        Assert.Contains("unrecognized, ignored: windowsett", line);
        Assert.Contains("known: windowset,screentime,audio,watchdog", line);
    }

    [Fact]
    public void Parse_KeepsEverySubSwitch_AndTheyAreDistinctFromWindowset()
    {
        var set = HelperPollerDiagnostics.Parse(string.Join(",", HelperPollerDiagnostics.SubSwitches));
        Assert.Equal(HelperPollerDiagnostics.SubSwitches.Length, set.Count);
        Assert.DoesNotContain(HelperPollerDiagnostics.WindowSet, set);
        Assert.Contains(HelperPollerDiagnostics.WindowSetDwm, set);
    }

    [Fact]
    public void Configure_BumpsTheChangeCount()
    {
        var before = HelperPollerDiagnostics.ChangeCount;
        HelperPollerDiagnostics.Configure("audio", null);
        Assert.True(HelperPollerDiagnostics.ChangeCount > before);
        HelperPollerDiagnostics.Configure(null, null);
    }

    [Fact]
    public void FormatStartupLine_NamesActiveSubSwitchesAsRecognized()
    {
        var line = HelperPollerDiagnostics.FormatStartupLine(
            HelperPollerDiagnostics.Parse("windowset-hooks windowset-trickle"));
        Assert.Contains("disabled:", line);
        Assert.Contains("windowset-hooks", line);
        Assert.Contains("windowset-trickle", line);
        Assert.DoesNotContain("unrecognized", line);
    }

    [Fact]
    public void FormatStartupLine_ListsSubSwitchesInTheTypoHint()
    {
        var line = HelperPollerDiagnostics.FormatStartupLine(HelperPollerDiagnostics.Parse("windowset-dwn"));
        Assert.Contains("unrecognized, ignored: windowset-dwn", line);
        Assert.Contains("sub-switches: windowset-hooks,windowset-dwm,windowset-fallback,windowset-trickle,windowset-enum", line);
    }

    [Fact]
    public void FormatStartupLine_SeparatesRecognizedFromTypos()
    {
        var line = HelperPollerDiagnostics.FormatStartupLine(HelperPollerDiagnostics.Parse("audio,windowsett"));
        Assert.Contains("disabled: audio", line);
        Assert.Contains("unrecognized, ignored: windowsett", line);
    }

    // The parsed list is re-emitted into the scheduled-task XML the LocalSystem
    // service writes, so a token carrying markup must never survive parsing.
    [Theory]
    [InlineData("audio\"/><Exec>evil</Exec>")]
    [InlineData("win&dowset")]
    [InlineData("../../etc")]
    public void Parse_DropsTokensWithUnsafeCharacters(string value)
    {
        Assert.Empty(HelperPollerDiagnostics.Parse(value));
    }

    [Fact]
    public void Parse_KeepsSafeTokenWhenAnUnsafeOneIsPresent()
    {
        var set = HelperPollerDiagnostics.Parse("audio,<script>");
        Assert.Single(set);
        Assert.Contains(HelperPollerDiagnostics.Audio, set);
    }

    // No log spam: a stuck poller trips every tick, and one line every few
    // seconds for hours would bury the rest of the file.
    [Fact]
    public void TryFormatSlowPass_EmitsOnce_ThenSuppressesAndCounts()
    {
        var poller = "spamtest-" + Guid.NewGuid().ToString("N");
        Assert.True(HelperPollerDiagnostics.TryFormatSlowPass(poller, 250, 400, out var first));
        Assert.Contains("pass=250.0ms", first);
        Assert.DoesNotContain("more in the last", first);

        for (var i = 0; i < 20; i++)
        {
            Assert.False(HelperPollerDiagnostics.TryFormatSlowPass(poller, 250, 400, out var line));
            Assert.Equal("", line);
        }
    }

    [Fact]
    public void TryFormatSlowPass_PutsDetailBeforeTheSuppressedSuffix()
    {
        var poller = "detail-" + Guid.NewGuid();
        Assert.True(HelperPollerDiagnostics.TryFormatSlowPass(poller, 250, 400, out _, "dwm=3 fallbacks=1"));
        Assert.False(HelperPollerDiagnostics.TryFormatSlowPass(poller, 250, 400, out _, "dwm=3 fallbacks=1"));
        HelperPollerDiagnostics.TryFormatSlowPass(poller, 250, 400, out _, "dwm=3 fallbacks=1");
        Assert.Equal("[helper-perf] x pass=1.0ms items=2 dwm=3 fallbacks=1", HelperPollerDiagnostics.FormatSlowPass("x", 1.0, 2, "dwm=3 fallbacks=1"));
    }

    [Fact]
    public void FormatSlowPass_CarriesPollerDurationAndCount()
    {
        Assert.Equal("[helper-perf] windowset pass=412.5ms items=317",
            HelperPollerDiagnostics.FormatSlowPass(HelperPollerDiagnostics.WindowSet, 412.53, 317));
    }

    [Fact]
    public void Configure_ThenIsDisabled_MatchesList()
    {
        try
        {
            HelperPollerDiagnostics.Configure("windowset", filePath: null);
            Assert.True(HelperPollerDiagnostics.IsDisabled(HelperPollerDiagnostics.WindowSet));
            Assert.False(HelperPollerDiagnostics.IsDisabled(HelperPollerDiagnostics.Audio));
        }
        finally
        {
            HelperPollerDiagnostics.Configure(null, filePath: null);
        }
    }

    [Fact]
    public void Configure_ReadsSwitchFile_WhenNoEnvValue()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllText(path, "audio\n");
            HelperPollerDiagnostics.Configure(null, path);
            Assert.True(HelperPollerDiagnostics.IsDisabled(HelperPollerDiagnostics.Audio));
        }
        finally
        {
            HelperPollerDiagnostics.Configure(null, filePath: null);
            try { File.Delete(path); } catch { }
        }
    }

    // The whole point of the switch file: the reporter edits it and the poller
    // stops without restarting anything.
    [Fact]
    public async Task SwitchFile_IsRePickedUp_WithoutReconfigure()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            HelperPollerDiagnostics.Configure(null, path);
            Assert.False(HelperPollerDiagnostics.IsDisabled(HelperPollerDiagnostics.WindowSet));

            File.WriteAllText(path, "windowset");
            await Task.Delay(2200);
            Assert.True(HelperPollerDiagnostics.IsDisabled(HelperPollerDiagnostics.WindowSet));

            File.Delete(path);
            await Task.Delay(2200);
            Assert.False(HelperPollerDiagnostics.IsDisabled(HelperPollerDiagnostics.WindowSet));
        }
        finally
        {
            HelperPollerDiagnostics.Configure(null, filePath: null);
            try { File.Delete(path); } catch { }
        }
    }

    // The switch-set change has to actually reach a log sink. Asserting only on
    // the formatted string is what let a dead sink ship: the helper short-circuits
    // before ServiceLog.Initialize, so ServiceLog silently dropped every line and
    // no format assertion could tell.
    [Fact]
    public async Task SwitchFileChange_ReachesTheLogSink()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var seen = new List<string>();
        try
        {
            HelperPollerDiagnostics.Log = line => { lock (seen) { seen.Add(line); } };
            HelperPollerDiagnostics.Configure(null, path);

            File.WriteAllText(path, "audio");
            await Task.Delay(2200);
            Assert.True(HelperPollerDiagnostics.IsDisabled(HelperPollerDiagnostics.Audio));

            lock (seen)
            {
                Assert.Contains(seen, l => l.Contains("disabled: audio"));
            }
        }
        finally
        {
            HelperPollerDiagnostics.Log = null;
            HelperPollerDiagnostics.Configure(null, filePath: null);
            try { File.Delete(path); } catch { }
        }
    }

    // An env/argv value must win outright, so a stale file on disk cannot
    // quietly re-enable something the operator pinned off.
    [Fact]
    public async Task EnvValue_PinsSet_AndIgnoresSwitchFile()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllText(path, "audio");
            HelperPollerDiagnostics.Configure("windowset", path);
            Assert.True(HelperPollerDiagnostics.IsDisabled(HelperPollerDiagnostics.WindowSet));
            await Task.Delay(2200);
            Assert.False(HelperPollerDiagnostics.IsDisabled(HelperPollerDiagnostics.Audio));
        }
        finally
        {
            HelperPollerDiagnostics.Configure(null, filePath: null);
            try { File.Delete(path); } catch { }
        }
    }
}
