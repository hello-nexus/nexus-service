using Nexus.Service.Platform.Linux;
using Xunit;

namespace Nexus.Service.Tests.Platform;

public class LinuxSessionAudioWrapTests
{
    [Fact]
    public void RootDaemonWithSession_WrapsThroughSetpriv()
    {
        var r = LinuxSession.WrapAudioClient(true, 1000, 1001, "wpctl", ["get-volume", "@DEFAULT_AUDIO_SINK@"]);

        Assert.NotNull(r);
        Assert.Equal("setpriv", r.Value.File);
        Assert.Equal(
            ["--reuid", "1000", "--regid", "1001", "--init-groups", "--", "wpctl", "get-volume", "@DEFAULT_AUDIO_SINK@"],
            r.Value.Args);
    }

    [Fact]
    public void RootDaemonWithoutSession_Skips()
    {
        Assert.Null(LinuxSession.WrapAudioClient(true, null, null, "pactl", ["list", "sinks"]));
    }

    [Fact]
    public void NonRootRun_PassesThroughUnchanged()
    {
        var r = LinuxSession.WrapAudioClient(false, null, null, "pactl", ["list", "sinks"]);

        Assert.NotNull(r);
        Assert.Equal("pactl", r.Value.File);
        Assert.Equal(["list", "sinks"], r.Value.Args);
    }
}

public class ShellExecutorAudioSkipTests
{
    [Fact]
    public void RunAudio_WhenWrapSkips_ReturnsEmptyWithoutSpawning()
    {
        var called = false;
        var result = Nexus.Service.Platform.ShellExecutor.RunAudio(
            (f, a) => { called = true; return null; }, "wpctl", ["get-volume"]);

        Assert.True(called);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void RunAudioExit_WhenWrapSkips_ReturnsMinusOne()
    {
        Assert.Equal(-1, Nexus.Service.Platform.ShellExecutor.RunAudioExit(
            (f, a) => null, "pactl", 1000, ["list"]));
    }
}
