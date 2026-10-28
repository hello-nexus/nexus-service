using System;
using System.Diagnostics;
using Nexus.Service.Helper;
using Xunit;

namespace Nexus.Service.Tests.Helper;

public class ScreenTimeProcessNameTests
{
    [Theory]
    [InlineData(@"C:\Windows\System32\notepad.exe", "notepad")]
    [InlineData(@"C:\Windows\explorer.EXE", "explorer")]
    [InlineData(@"C:\Program Files\App\my.tool.exe", "my.tool")]
    [InlineData(@"C:\apps\legacy.bin", "legacy.bin")]
    [InlineData(@"C:\apps\noext", "noext")]
    [InlineData("bare.exe", "bare")]
    [InlineData(@"C:\apps\", "")]
    public void ProcessNameFromPath_MatchesProcessNameForm(string path, string expected)
    {
        Assert.Equal(expected, ScreenTimeProcessName.ProcessNameFromPath(path));
    }

    [Fact]
    public void ProcessNameFromPath_MatchesProcessNameForCurrentProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var self = Process.GetCurrentProcess();
        Assert.Equal(self.ProcessName, ScreenTimeProcessName.ProcessNameFromPath(Environment.ProcessPath!));
    }
}
