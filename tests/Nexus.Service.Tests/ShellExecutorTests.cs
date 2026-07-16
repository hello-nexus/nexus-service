using Nexus.Service.Platform;
using Xunit;

namespace Nexus.Service.Tests;

public class ShellExecutorTests
{
    [Fact]
    public void Run_returns_empty_string_when_binary_missing()
    {
        var output = ShellExecutor.Run("/no/such/binary/here", 1000);
        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public void Run_captures_stdout_from_a_real_binary()
    {
        // /bin/echo exists on macOS and Linux. Skip on Windows where the path
        // doesn't exist.
        if (OperatingSystem.IsWindows())
            return;

        var output = ShellExecutor.Run("/bin/echo", 2000, "hello-shell");
        Assert.Contains("hello-shell", output);
    }

    [Fact]
    public void Run_with_timeout_kills_long_running_process()
    {
        if (OperatingSystem.IsWindows())
            return;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var output = ShellExecutor.Run("/bin/sleep", 500, "10");
        sw.Stop();
        // Should return well before 10s; allow some headroom for kill latency.
        Assert.True(sw.ElapsedMilliseconds < 5000);
    }

    [Fact]
    public void RunWithStdinExit_returns_negative_one_when_binary_missing()
    {
        var exitCode = ShellExecutor.RunWithStdinExit("/no/such/binary/here", "input", 1000, out var stderr);
        Assert.Equal(-1, exitCode);
    }

    [Fact]
    public void RunWithStdinExit_reports_the_real_process_exit_code()
    {
        if (OperatingSystem.IsWindows())
            return;

        // Unlike RunWithStdin (which drops the exit code silently), this must
        // surface a non-zero exit rather than reporting success.
        var exitCode = ShellExecutor.RunWithStdinExit("/bin/sh", "ignored", 2000, out var stderr, "-c", "exit 3");
        Assert.Equal(3, exitCode);
    }

    [Fact]
    public void RunWithStdinExit_zero_exit_on_a_command_that_consumes_stdin_cleanly()
    {
        if (OperatingSystem.IsWindows())
            return;

        var exitCode = ShellExecutor.RunWithStdinExit("/bin/cat", "hello-stdin", 2000, out var stderr);
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
    }
}
