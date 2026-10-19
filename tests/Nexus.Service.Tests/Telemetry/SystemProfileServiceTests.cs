using Nexus.Service.Devices.Handlers;
using Nexus.Service.Models.Devices;
using Nexus.Service.Telemetry;
using Nexus.Service.Tests.Cloud;
using Xunit;

namespace Nexus.Service.Tests.Telemetry;

public class SystemProfileServiceTests
{
    [Fact]
    public void LatchY70Seen_sets_the_flag_when_the_y70_is_connected()
    {
        var store = new InMemoryConfigStore();

        SystemProfileService.LatchY70Seen(store, new[] { new DeviceListItem { Id = Y70Handler.HandlerId, Connected = true } });

        Assert.True(store.Load().Telemetry.FleetY70Seen);
    }

    [Fact]
    public void LatchY70Seen_ignores_other_devices()
    {
        var store = new InMemoryConfigStore();

        SystemProfileService.LatchY70Seen(store, new[] { new DeviceListItem { Id = "qseries", Connected = true } });

        Assert.False(store.Load().Telemetry.FleetY70Seen);
    }

    [Fact]
    public void LatchY70Seen_never_clears_a_set_flag()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Telemetry.FleetY70Seen = true);

        SystemProfileService.LatchY70Seen(store, System.Array.Empty<DeviceListItem>());

        Assert.True(store.Load().Telemetry.FleetY70Seen);
    }

    [Fact]
    public void Profile_signature_survives_a_restart_so_an_unchanged_profile_is_not_resent()
    {
        var store = new InMemoryConfigStore();
        var t0 = new System.DateTimeOffset(2026, 1, 1, 0, 0, 0, System.TimeSpan.Zero);

        Assert.Equal(ProfileSend.Changed, SystemProfileService.TryClaimProfileSend(store, "version=1|cpu=A", t0));

        // A restarted process has no in-memory state; only the store carries over.
        Assert.Equal(ProfileSend.None, SystemProfileService.TryClaimProfileSend(store, "version=1|cpu=A", t0.AddMinutes(2)));
        Assert.Equal(ProfileSend.None, SystemProfileService.TryClaimProfileSend(store, "version=1|cpu=A", t0.AddHours(20)));
    }

    [Fact]
    public void Changed_profile_is_sent_once_the_minimum_interval_has_passed()
    {
        var store = new InMemoryConfigStore();
        var t0 = new System.DateTimeOffset(2026, 1, 1, 0, 0, 0, System.TimeSpan.Zero);
        Assert.Equal(ProfileSend.Changed, SystemProfileService.TryClaimProfileSend(store, "cpu=A|devices=x", t0));

        // A hot-plugged device flips the signature inside the interval: held.
        Assert.Equal(ProfileSend.None, SystemProfileService.TryClaimProfileSend(store, "cpu=A|devices=x,y", t0.AddMinutes(30)));
        // Still changed after the interval: sent, and recorded.
        Assert.Equal(ProfileSend.Changed, SystemProfileService.TryClaimProfileSend(store, "cpu=A|devices=x,y", t0.Add(SystemProfileService.ProfileMinResend)));
        Assert.Equal(ProfileSend.None, SystemProfileService.TryClaimProfileSend(store, "cpu=A|devices=x,y", t0.Add(SystemProfileService.ProfileMinResend).AddMinutes(1)));
    }

    [Fact]
    public void Unchanged_profile_is_resent_after_the_refresh_interval_so_a_lost_send_heals()
    {
        var store = new InMemoryConfigStore();
        var t0 = new System.DateTimeOffset(2026, 1, 1, 0, 0, 0, System.TimeSpan.Zero);
        Assert.Equal(ProfileSend.Changed, SystemProfileService.TryClaimProfileSend(store, "cpu=A", t0));

        Assert.Equal(ProfileSend.None, SystemProfileService.TryClaimProfileSend(store, "cpu=A", t0.AddHours(23)));
        Assert.Equal(ProfileSend.Refresh, SystemProfileService.TryClaimProfileSend(store, "cpu=A", t0.Add(SystemProfileService.ProfileRefresh)));
        Assert.Equal(ProfileSend.None, SystemProfileService.TryClaimProfileSend(store, "cpu=A", t0.Add(SystemProfileService.ProfileRefresh).AddHours(1)));
    }
}
