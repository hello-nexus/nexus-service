using System;
using System.Diagnostics;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting;

public class OpenRgbGracefulStopTests
{
    [Fact]
    public void Request_ends_a_unix_process_through_sigterm()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var proc = Process.Start(new ProcessStartInfo("sleep", "30") { UseShellExecute = false })!;
        try
        {
            Assert.True(OpenRgbGracefulStop.TryRequest(proc.Id));
            Assert.True(proc.WaitForExit(3000));
        }
        finally
        {
            if (!proc.HasExited)
            {
                proc.Kill();
            }
        }
    }

    [Fact]
    public void Request_reports_false_when_no_process_listens()
    {
        // No daemon owns this pid, so neither the signal nor the named event can be delivered.
        Assert.False(OpenRgbGracefulStop.TryRequest(int.MaxValue));
    }
}
