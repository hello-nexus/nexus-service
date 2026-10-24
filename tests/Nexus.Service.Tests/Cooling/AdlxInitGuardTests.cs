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

    private const string K = "1.5.0.124#100";
    private const string K2 = "1.5.0.124#200";

    private void Die(string key, string boot)
    {
        AdlxInitGuard.Arm(_dir, key, boot);
    }

    [Fact]
    public void A_first_start_is_not_latched() =>
        Assert.Equal(AdlxInitGuard.Latch.None, AdlxInitGuard.Latched(_dir, K, "b1"));

    [Fact]
    public void An_init_that_returned_leaves_no_latch()
    {
        AdlxInitGuard.Arm(_dir, K, "b1");
        AdlxInitGuard.Disarm(_dir);

        Assert.Equal(AdlxInitGuard.Latch.None, AdlxInitGuard.Latched(_dir, K, "b1"));
    }

    [Fact]
    public void A_first_death_latches_only_the_boot_it_happened_in()
    {
        Die(K, "b1");

        Assert.Equal(AdlxInitGuard.Latch.ThisBoot, AdlxInitGuard.Latched(_dir, K, "b1"));
        Assert.Equal(AdlxInitGuard.Latch.ThisBoot, AdlxInitGuard.Latched(_dir, K, "b1"));
        Assert.Equal(AdlxInitGuard.Latch.None, AdlxInitGuard.Latched(_dir, K, "b2"));
    }

    [Fact]
    public void A_second_death_under_the_same_key_persists_across_boots()
    {
        Die(K, "b1");
        AdlxInitGuard.Latched(_dir, K, "b1");
        Die(K, "b2");

        Assert.Equal(AdlxInitGuard.Latch.UntilDriverUpdate, AdlxInitGuard.Latched(_dir, K, "b2"));
        Assert.Equal(AdlxInitGuard.Latch.UntilDriverUpdate, AdlxInitGuard.Latched(_dir, K, "b3"));
    }

    [Fact]
    public void A_driver_change_clears_a_persistent_latch()
    {
        Die(K, "b1");
        AdlxInitGuard.Latched(_dir, K, "b1");
        Die(K, "b2");
        Assert.Equal(AdlxInitGuard.Latch.UntilDriverUpdate, AdlxInitGuard.Latched(_dir, K, "b2"));

        Assert.Equal(AdlxInitGuard.Latch.None, AdlxInitGuard.Latched(_dir, K2, "b3"));
        Assert.Equal(AdlxInitGuard.Latch.None, AdlxInitGuard.Latched(_dir, K, "b3"));
    }

    [Fact]
    public void A_death_under_a_new_driver_is_a_first_strike_again()
    {
        Die(K, "b1");
        AdlxInitGuard.Latched(_dir, K, "b1");
        Die(K, "b2");
        AdlxInitGuard.Latched(_dir, K, "b2");
        Die(K2, "b3");

        Assert.Equal(AdlxInitGuard.Latch.ThisBoot, AdlxInitGuard.Latched(_dir, K2, "b3"));
        Assert.Equal(AdlxInitGuard.Latch.None, AdlxInitGuard.Latched(_dir, K2, "b4"));
    }

    [Fact]
    public void An_old_format_latch_with_the_same_version_counts_as_the_first_strike()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "adlx-init-latched"), "1.5.0.124@b1");

        Assert.Equal(AdlxInitGuard.Latch.None, AdlxInitGuard.Latched(_dir, K, "b2"));
        Die(K, "b2");
        Assert.Equal(AdlxInitGuard.Latch.UntilDriverUpdate, AdlxInitGuard.Latched(_dir, K, "b2"));
    }

    [Fact]
    public void An_old_format_latch_still_holds_for_its_own_boot_and_a_new_version_clears_it()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "adlx-init-latched"), "1.5.0.124@b1");

        Assert.Equal(AdlxInitGuard.Latch.ThisBoot, AdlxInitGuard.Latched(_dir, K, "b1"));
        Assert.Equal(AdlxInitGuard.Latch.None, AdlxInitGuard.Latched(_dir, "1.6.0.10#100", "b1"));
    }

    [Fact]
    public void An_unreadable_key_never_latches_past_the_boot()
    {
        Die("", "b1");
        AdlxInitGuard.Latched(_dir, null, "b1");
        Die("", "b2");

        Assert.Equal(AdlxInitGuard.Latch.ThisBoot, AdlxInitGuard.Latched(_dir, null, "b2"));
        Assert.Equal(AdlxInitGuard.Latch.None, AdlxInitGuard.Latched(_dir, null, "b3"));
    }

    [Fact]
    public void An_unreadable_key_keeps_a_persistent_latch()
    {
        Die(K, "b1");
        AdlxInitGuard.Latched(_dir, K, "b1");
        Die(K, "b2");
        AdlxInitGuard.Latched(_dir, K, "b2");

        Assert.Equal(AdlxInitGuard.Latch.UntilDriverUpdate, AdlxInitGuard.Latched(_dir, null, "b3"));
        Assert.Equal(AdlxInitGuard.Latch.UntilDriverUpdate, AdlxInitGuard.Latched(_dir, K, "b3"));
    }

    [Fact]
    public void A_crash_under_an_unreadable_key_never_overwrites_a_persistent_latch()
    {
        Die(K, "b1");
        AdlxInitGuard.Latched(_dir, K, "b1");
        Die(K, "b2");
        AdlxInitGuard.Latched(_dir, K, "b2");
        Die("", "b3");

        Assert.Equal(AdlxInitGuard.Latch.UntilDriverUpdate, AdlxInitGuard.Latched(_dir, null, "b3"));
        Assert.Equal(AdlxInitGuard.Latch.UntilDriverUpdate, AdlxInitGuard.Latched(_dir, K, "b4"));
    }

    [Fact]
    public void A_crash_under_an_unreadable_key_never_overwrites_a_first_strike_either()
    {
        Die(K, "b1");
        AdlxInitGuard.Latched(_dir, K, "b1");
        Die("", "b2");
        Assert.Equal(AdlxInitGuard.Latch.ThisBoot, AdlxInitGuard.Latched(_dir, null, "b2"));

        // Still strike one under K: a second keyed death persists.
        Die(K, "b3");
        Assert.Equal(AdlxInitGuard.Latch.UntilDriverUpdate, AdlxInitGuard.Latched(_dir, K, "b3"));
    }

    [Fact]
    public void A_clean_stop_of_a_run_that_had_adlx_enabled_clears_a_first_strike()
    {
        Die(K, "b1");
        Assert.Equal(AdlxInitGuard.Latch.ThisBoot, AdlxInitGuard.Latched(_dir, K, "b1"));
        AdlxInitGuard.ResetScopes();

        // Next boot retries ADLX, runs enabled, stops cleanly.
        Assert.Equal(AdlxInitGuard.Latch.None, AdlxInitGuard.Latched(_dir, K, "b2"));
        AdlxInitGuard.Configure(_dir, K, "b2");
        AdlxInitGuard.Stop(_dir);
        AdlxInitGuard.ClearFirstStrike(_dir);
        AdlxInitGuard.ResetScopes();

        Die(K, "b3");
        Assert.Equal(AdlxInitGuard.Latch.ThisBoot, AdlxInitGuard.Latched(_dir, K, "b3"));
    }

    [Fact]
    public void A_clean_stop_never_clears_a_second_strike()
    {
        Die(K, "b1");
        AdlxInitGuard.Latched(_dir, K, "b1");
        Die(K, "b2");
        AdlxInitGuard.Latched(_dir, K, "b2");
        AdlxInitGuard.ResetScopes();
        AdlxInitGuard.Configure(_dir, K, "b3");
        AdlxInitGuard.Stop(_dir);
        AdlxInitGuard.ClearFirstStrike(_dir);

        Assert.Equal(AdlxInitGuard.Latch.UntilDriverUpdate, AdlxInitGuard.Latched(_dir, K, "b4"));
    }

    [Fact]
    public void A_clean_stop_of_a_run_latched_off_this_boot_keeps_the_first_strike()
    {
        Die(K, "b1");
        Assert.Equal(AdlxInitGuard.Latch.ThisBoot, AdlxInitGuard.Latched(_dir, K, "b1"));
        AdlxInitGuard.ResetScopes();
        // ADLX never enabled in this process, so no Configure call.
        AdlxInitGuard.Stop(_dir);
        AdlxInitGuard.ClearFirstStrike(_dir);
        AdlxInitGuard.ResetScopes();

        // The crash-every-boot case still reaches strike two.
        Die(K, "b2");
        Assert.Equal(AdlxInitGuard.Latch.UntilDriverUpdate, AdlxInitGuard.Latched(_dir, K, "b2"));
    }

    [Fact]
    public void A_latch_write_leaves_no_temp_file()
    {
        Die(K, "b1");
        AdlxInitGuard.Latched(_dir, K, "b1");

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void A_stop_alone_keeps_a_first_strike_until_teardown_finishes()
    {
        Die(K, "b1");
        AdlxInitGuard.Latched(_dir, K, "b1");
        AdlxInitGuard.ResetScopes();
        AdlxInitGuard.Configure(_dir, K, "b2");
        // The process dies in the fan handback after Stop, before ClearFirstStrike.
        AdlxInitGuard.Stop(_dir);
        AdlxInitGuard.ResetScopes();

        Die(K, "b3");
        Assert.Equal(AdlxInitGuard.Latch.UntilDriverUpdate, AdlxInitGuard.Latched(_dir, K, "b3"));
    }

    [Fact]
    public void A_run_under_an_unreadable_key_never_clears_a_keyed_first_strike()
    {
        Die(K, "b1");
        AdlxInitGuard.Latched(_dir, K, "b1");
        AdlxInitGuard.ResetScopes();
        AdlxInitGuard.Configure(_dir, null, "b2");
        AdlxInitGuard.Stop(_dir);
        AdlxInitGuard.ClearFirstStrike(_dir);
        AdlxInitGuard.ResetScopes();

        Die(K, "b3");
        Assert.Equal(AdlxInitGuard.Latch.UntilDriverUpdate, AdlxInitGuard.Latched(_dir, K, "b3"));
    }

    [Fact]
    public void A_death_whose_latch_cannot_be_written_holds_this_boot_and_keeps_the_mark()
    {
        Die(K, "b1");
        // A directory in the latch's place makes the write fail.
        Directory.CreateDirectory(Path.Combine(_dir, "adlx-init-latched"));

        Assert.Equal(AdlxInitGuard.Latch.ThisBoot, AdlxInitGuard.Latched(_dir, K, "b1"));
        Assert.True(File.Exists(Path.Combine(_dir, "adlx-init-crashguard")));
    }

    [Fact]
    public void BuildKey_uses_the_highest_driver_version_and_needs_both_parts()
    {
        Assert.Equal("1.4.0.121#9", AdlxInitGuard.BuildKey("1.4.0.121", new long[] { 9, 5, 0 }));
        Assert.Equal("1.4.0.121#9", AdlxInitGuard.BuildKey("1.4.0.121", new long[] { 5, 9 }));
        Assert.Null(AdlxInitGuard.BuildKey(null, new long[] { 5 }));
        Assert.Null(AdlxInitGuard.BuildKey("1.4.0.121", new long[] { 0 }));
        Assert.Null(AdlxInitGuard.BuildKey("1.4.0.121", Array.Empty<long>()));
    }

    [Fact]
    public void A_write_that_returned_leaves_no_latch()
    {
        AdlxInitGuard.Configure(_dir, "1.4.0.121#1", "b1");
        using (AdlxInitGuard.Scope()) { }

        Assert.Equal(AdlxInitGuard.Latch.None, AdlxInitGuard.Latched(_dir, "1.4.0.121#1", "b1"));
    }

    [Fact]
    public void A_run_that_died_inside_a_write_latches_the_same_key_and_not_a_new_one()
    {
        AdlxInitGuard.Configure(_dir, "1.4.0.121#1", "b1");
        // The scope is never disposed: the process died inside it.
        _ = AdlxInitGuard.Scope();
        Assert.True(File.Exists(Path.Combine(_dir, "adlx-init-crashguard")));
        AdlxInitGuard.ResetScopes();

        Assert.Equal(AdlxInitGuard.Latch.ThisBoot, AdlxInitGuard.Latched(_dir, "1.4.0.121#1", "b1"));
        Assert.Equal(AdlxInitGuard.Latch.None, AdlxInitGuard.Latched(_dir, "1.6.0.10#1", "b1"));
    }

    [Fact]
    public void A_nested_scope_keeps_the_outer_mark()
    {
        AdlxInitGuard.Configure(_dir, "1.4.0.121#1", "b1");
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
        Assert.Equal(AdlxInitGuard.Latch.None, AdlxInitGuard.LatchedOnce(_dir, "1.4.0.121#1", "b1"));
        AdlxInitGuard.Configure(_dir, "1.4.0.121#1", "b1");

        using (AdlxInitGuard.Scope())
        {
            Assert.Equal(AdlxInitGuard.Latch.None, AdlxInitGuard.LatchedOnce(_dir, "1.4.0.121#1", "b1"));
            Assert.True(File.Exists(Path.Combine(_dir, "adlx-init-crashguard")));
        }

        Assert.False(File.Exists(Path.Combine(_dir, "adlx-init-latched")));
    }
}
