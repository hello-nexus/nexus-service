using System.Diagnostics;
using System.Text;

namespace Nexus.Service.Platform;

/// <summary>
/// Synchronous and async helpers around Process.Start for shell-out style
/// providers (sysctl, ps, lsappinfo, system_profiler, ffmpeg probe, etc.).
/// Replaces the per-provider hand-rolled ProcessStartInfo + WaitForExit
/// boilerplate. Returns "" on any failure so callers can branch on
/// string.IsNullOrEmpty without a try/catch wrapper.
/// </summary>
public static class ShellExecutor
{
    public const int DefaultTimeoutMs = 5000;

    public static string Run(string fileName, params string[] args)
        => Run(fileName, DefaultTimeoutMs, args);

    public static string Run(string fileName, int timeoutMs, params string[] args)
    {
        try
        {
            var psi = BuildPsi(fileName, args);
            using var proc = Process.Start(psi);
            if (proc is null)
                return string.Empty;

            // Read stdout asynchronously while waiting for exit. Reading
            // synchronously here can block for the lifetime of the process if
            // it never writes / never closes stdout (e.g. /bin/sleep), making
            // the timeoutMs cap useless.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            if (!proc.WaitForExit(timeoutMs))
            {
                Console.Error.WriteLine($"[shell] timeout ({timeoutMs}ms) waiting for {fileName} {string.Join(' ', args)}");
                try { proc.Kill(entireProcessTree: true); } catch { }
            }
            try { return stdoutTask.GetAwaiter().GetResult(); }
            catch { return string.Empty; }
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Run a command for its exit status (not its output) - for fire-and-verify
    /// writes where success must be confirmed, not assumed. Returns the process
    /// exit code, or a negative value if it couldn't start / timed out. Drains
    /// stdout+stderr so the child never blocks on a full pipe.
    /// </summary>
    public static int RunExit(string fileName, int timeoutMs, params string[] args)
    {
        try
        {
            var psi = BuildPsi(fileName, args);
            psi.RedirectStandardError = true;
            using var proc = Process.Start(psi);
            if (proc is null)
                return -1;
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(timeoutMs))
            {
                Console.Error.WriteLine($"[shell] timeout ({timeoutMs}ms) waiting for {fileName} {string.Join(' ', args)}");
                try { proc.Kill(entireProcessTree: true); } catch { }
                return -2;
            }
            try { _ = outTask.GetAwaiter().GetResult(); _ = errTask.GetAwaiter().GetResult(); } catch { }
            return proc.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Run capturing both stdout and stderr; returns combined output. Useful
    /// for tools that print structured data on stderr (ffmpeg).
    /// </summary>
    public static string RunCombined(string fileName, int timeoutMs, params string[] args)
    {
        try
        {
            var psi = BuildPsi(fileName, args);
            using var proc = Process.Start(psi);
            if (proc is null)
                return string.Empty;

            var sb = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) sb.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) sb.AppendLine(e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            if (!proc.WaitForExit(timeoutMs))
            {
                Console.Error.WriteLine($"[shell] timeout ({timeoutMs}ms) waiting for {fileName} {string.Join(' ', args)}");
                try { proc.Kill(entireProcessTree: true); } catch { }
            }
            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    public static async Task<string> RunAsync(string fileName, int timeoutMs, CancellationToken ct, params string[] args)
    {
        try
        {
            var psi = BuildPsi(fileName, args);
            using var proc = Process.Start(psi);
            if (proc is null)
                return string.Empty;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            try
            {
                // Drain stderr concurrently - a chatty child (GTK warnings from
                // zenity) fills the 64KB pipe and blocks otherwise.
                _ = proc.StandardError.ReadToEndAsync(cts.Token);
                var stdout = await proc.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                return stdout;
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return string.Empty;
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Run with stdin piped from the given string (e.g. PowerShell -Command -).
    /// </summary>
    public static string RunWithStdin(string fileName, string stdin, int timeoutMs, params string[] args)
    {
        try
        {
            var psi = BuildPsi(fileName, args);
            psi.RedirectStandardInput = true;
            using var proc = Process.Start(psi);
            if (proc is null)
                return string.Empty;

            proc.StandardInput.Write(stdin);
            proc.StandardInput.Close();

            var stdout = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            }
            return stdout;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Run with stdin piped from the given string, returning the exit code
    /// for a fire-and-verify write where success must be confirmed rather
    /// than assumed from the process merely starting - unlike RunWithStdin,
    /// which drops a non-zero exit silently. Returns -1 if the process could
    /// not start (or threw), -2 on timeout; stderr carries the process's
    /// error output (or the exception message on the -1 path).
    /// </summary>
    public static int RunWithStdinExit(string fileName, string stdin, int timeoutMs, out string stderr, params string[] args)
    {
        stderr = string.Empty;
        try
        {
            var psi = BuildPsi(fileName, args);
            psi.RedirectStandardInput = true;
            using var proc = Process.Start(psi);
            if (proc is null)
                return -1;

            // A process that exits before consuming stdin (e.g. `sh -c 'exit 3'`)
            // closes the pipe, so the write faults with a broken pipe; that is not
            // a failure - fall through to read the real exit code.
            try
            {
                proc.StandardInput.Write(stdin);
                proc.StandardInput.Close();
            }
            catch (System.IO.IOException) { }

            var errTask = proc.StandardError.ReadToEndAsync();
            var outTask = proc.StandardOutput.ReadToEndAsync();
            if (!proc.WaitForExit(timeoutMs))
            {
                Console.Error.WriteLine($"[shell] timeout ({timeoutMs}ms) waiting for {fileName} {string.Join(' ', args)}");
                try { proc.Kill(entireProcessTree: true); } catch { }
                return -2;
            }
            try { stderr = errTask.GetAwaiter().GetResult(); } catch { }
            try { _ = outTask.GetAwaiter().GetResult(); } catch { }
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            stderr = ex.Message;
            return -1;
        }
    }

    private static ProcessStartInfo BuildPsi(string fileName, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        return psi;
    }
}
