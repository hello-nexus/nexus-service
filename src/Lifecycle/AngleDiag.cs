#if WINDOWS
using System.Diagnostics;
using System.Globalization;
using Nexus.Service.Platform;

namespace Nexus.Service.Lifecycle;

/// <summary>Service side of the ANGLE diagnostic: launches the probe child once and logs [gpu-check] CPU samples early in the run.</summary>
internal static class AngleDiag
{
    private static readonly TimeSpan ProbeDelay = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan ProbeKillAfter = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);
    private const int CheckSamples = 12;
    private static int _started;

    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }
        _ = Task.Run(RunProbeAsync);
        _ = Task.Run(RunGpuCheckAsync);
    }

    private static async Task RunProbeAsync()
    {
        try
        {
            await Task.Delay(ProbeDelay).ConfigureAwait(false);
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                return;
            }
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--angle-probe");
            using var p = Process.Start(psi);
            if (p is null)
            {
                ServiceLog.Warn("[angle-probe] child did not start");
                return;
            }
            ServiceLog.Info($"[angle-probe] child started pid={p.Id}, results in nexus-angle-probe.log");
            using var cts = new CancellationTokenSource(ProbeKillAfter);
            try
            {
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                ServiceLog.Info($"[angle-probe] child exit={p.ExitCode}");
            }
            catch (OperationCanceledException)
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                }
                ServiceLog.Warn("[angle-probe] child killed after the time limit");
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[angle-probe] launch failed: {ex.Message}");
        }
    }

    private static async Task RunGpuCheckAsync()
    {
        try
        {
            var ci = CultureInfo.InvariantCulture;
            var prev = new Dictionary<uint, long>();
            _ = AngleDiagThreads.Sample(prev, 1);
            using var self = Process.GetCurrentProcess();
            var lastCpu = self.TotalProcessorTime;
            var wall = Stopwatch.StartNew();
            var last = TimeSpan.Zero;
            using var timer = new PeriodicTimer(CheckInterval);
            for (var i = 1; i <= CheckSamples && await timer.WaitForNextTickAsync().ConfigureAwait(false); i++)
            {
                var secs = (wall.Elapsed - last).TotalSeconds;
                last = wall.Elapsed;
                self.Refresh();
                var cpu = self.TotalProcessorTime;
                var pct = (cpu - lastCpu).TotalSeconds / secs * 100.0;
                lastCpu = cpu;
                var rows = AngleDiagThreads.Sample(prev, secs);
                var modules = AngleDiagThreads.LoadedIntelModules();
                ServiceLog.Info($"[gpu-check] t={(int)wall.Elapsed.TotalSeconds}s proc={pct.ToString("0.0", ci)}%core "
                    + $"icd-loaded={(AngleDiagThreads.IcdLoaded(modules) ? "yes" : "no")} intel-modules=[{string.Join(", ", modules)}] "
                    + $"top-threads:{AngleDiagThreads.Top(rows, 5)}");
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[gpu-check] stopped: {ex.Message}");
        }
    }
}
#endif
