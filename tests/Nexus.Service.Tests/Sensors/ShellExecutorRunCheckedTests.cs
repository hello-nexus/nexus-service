using System;
using Nexus.Service.Platform;
using Xunit;

namespace Nexus.Service.Tests.Sensors;

public class ShellExecutorRunCheckedTests
{
    [Fact]
    public void A_clean_exit_is_ok()
    {
        if (OperatingSystem.IsWindows())
            return;
        var (output, ok) = ShellExecutor.RunChecked("/bin/sh", 5000, "-c", "echo hi");
        Assert.True(ok);
        Assert.Equal("hi", output.Trim());
    }

    [Fact]
    public void A_non_zero_exit_is_not_ok_even_with_output()
    {
        if (OperatingSystem.IsWindows())
            return;
        var (output, ok) = ShellExecutor.RunChecked("/bin/sh", 5000, "-c", "echo partial; exit 3");
        Assert.False(ok);
        Assert.Equal("partial", output.Trim());
    }

    [Fact]
    public void A_timeout_is_not_ok_and_keeps_only_what_was_printed()
    {
        if (OperatingSystem.IsWindows())
            return;
        var (output, ok) = ShellExecutor.RunChecked("/bin/sh", 300, "-c", "echo partial; sleep 5");
        Assert.False(ok);
        Assert.Equal("partial", output.Trim());
    }
}
