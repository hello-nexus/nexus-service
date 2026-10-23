using System;
using System.IO;
using Nexus.Service.Cooling;
using Xunit;

namespace Nexus.Service.Tests.Cooling;

public sealed class AdlxInitGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"adlx-guard-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        AdlxInitGuard.ResetScopes();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void A_first_start_is_not_latched() =>
        Assert.False(AdlxInitGuard.Latched(_dir, "1.5.0.124@157"));

    [Fact]
    public void An_init_that_returned_leaves_no_latch()
    {
        AdlxInitGuard.Arm(_dir, "1.5.0.124@157");
        AdlxInitGuard.Disarm(_dir);

        Assert.False(AdlxInitGuard.Latched(_dir, "1.5.0.124@157"));
    }

    [Fact]
    public void A_run_that_died_inside_init_latches_every_later_start_with_the_same_key()
    {
        AdlxInitGuard.Arm(_dir, "1.5.0.124@157");

        Assert.True(AdlxInitGuard.Latched(_dir, "1.5.0.124@157"));
        Assert.True(AdlxInitGuard.Latched(_dir, "1.5.0.124@157"));
    }

    [Theory]
    [InlineData("1.5.0.124@158")] // reboot
    [InlineData("1.6.0.10@157")]  // driver update
    public void A_new_key_clears_the_latch(string next)
    {
        AdlxInitGuard.Arm(_dir, "1.5.0.124@157");
        Assert.True(AdlxInitGuard.Latched(_dir, "1.5.0.124@157"));

        Assert.False(AdlxInitGuard.Latched(_dir, next));
        Assert.False(AdlxInitGuard.Latched(_dir, "1.5.0.124@157"));
    }

    [Fact]
    public void A_write_that_returned_leaves_no_latch()
    {
        AdlxInitGuard.Configure(_dir, "1.4.0.121@9");
        using (AdlxInitGuard.Scope()) { }

        Assert.False(AdlxInitGuard.Latched(_dir, "1.4.0.121@9"));
    }

    [Fact]
    public void A_run_that_died_inside_a_write_latches_the_same_key_and_not_a_new_one()
    {
        AdlxInitGuard.Configure(_dir, "1.4.0.121@9");
        // The scope is never disposed: the process died inside it.
        _ = AdlxInitGuard.Scope();
        Assert.True(File.Exists(Path.Combine(_dir, "adlx-init-crashguard")));
        AdlxInitGuard.ResetScopes();

        Assert.True(AdlxInitGuard.Latched(_dir, "1.4.0.121@9"));
        Assert.False(AdlxInitGuard.Latched(_dir, "1.4.0.121@10"));
    }

    [Fact]
    public void A_nested_scope_keeps_the_outer_mark()
    {
        AdlxInitGuard.Configure(_dir, "1.4.0.121@9");
        using (AdlxInitGuard.Scope())
        {
            using (AdlxInitGuard.Scope()) { }
            Assert.True(File.Exists(Path.Combine(_dir, "adlx-init-crashguard")));
        }
        Assert.False(File.Exists(Path.Combine(_dir, "adlx-init-crashguard")));
    }

    [Fact]
    public void Rediscovery_during_a_live_scope_does_not_turn_its_mark_into_a_latch()
    {
        Assert.False(AdlxInitGuard.LatchedOnce(_dir, "1.4.0.121@9"));
        AdlxInitGuard.Configure(_dir, "1.4.0.121@9");

        using (AdlxInitGuard.Scope())
        {
            Assert.False(AdlxInitGuard.LatchedOnce(_dir, "1.4.0.121@9"));
            Assert.True(File.Exists(Path.Combine(_dir, "adlx-init-crashguard")));
        }

        Assert.False(File.Exists(Path.Combine(_dir, "adlx-init-latched")));
    }
}
