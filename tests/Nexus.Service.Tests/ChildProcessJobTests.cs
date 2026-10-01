using System.Diagnostics;
using Nexus.Service.Benchmarks;

namespace Nexus.Service.Tests;

public sealed class ChildProcessJobTests
{
    [WindowsOnlyFact]
    public void Assigned_process_joins_the_kill_on_close_job()
    {
        using var proc = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 20 127.0.0.1 >nul")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        try
        {
            ChildProcessJob.Assign(proc);
            Assert.True(ChildProcessJob.Contains(proc));
        }
        finally
        {
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }
}
