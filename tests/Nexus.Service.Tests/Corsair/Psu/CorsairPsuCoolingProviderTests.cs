using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Peripherals.Corsair.Psu;
using Xunit;

namespace Nexus.Service.Tests.Corsair.Psu;

public class CorsairPsuCoolingProviderTests
{
    private const string Id = "corsair-psu:1";

    private sealed class FakePsu : ICorsairPsuTransport
    {
        public CorsairPsuInfo? Present { get; set; } = new(0x1C1F, "psu", null);
        public byte Mode { get; set; }
        public bool Silent { get; set; }
        public bool WrongEcho { get; set; }
        public byte? ModeReadBack { get; set; }
        public bool IgnoreModeWrites { get; set; }
        public List<byte[]> Sent { get; } = new();

        public CorsairPsuInfo? Find() => Present;

        public byte[]? Exchange(CorsairPsuInfo device, ReadOnlySpan<byte> frame)
        {
            var copy = frame.ToArray();
            Sent.Add(copy);
            if (Silent || Present is null) return null;
            var reply = new byte[64];
            if (copy[0] == 0xFE) return reply;
            reply[0] = copy[0];
            reply[1] = WrongEcho ? (byte)0xFE : copy[1];
            if (copy[1] == 0xF0)
            {
                if (copy[0] == 0x02 && !IgnoreModeWrites) Mode = copy[2];
                reply[2] = ModeReadBack ?? Mode;
            }
            return reply;
        }

        public IEnumerable<byte[]> Writes => Sent.Where(f => f[0] == 0x02);
        public IEnumerable<byte[]> HardwareModeWrites => Writes.Where(f => f[1] == 0xF0 && f[2] == 0x00);
    }

    private static CorsairPsuCoolingProvider Make(FakePsu psu, Func<bool>? ready = null, long start = 0)
    {
        var now = start;
        return new CorsairPsuCoolingProvider(psu, ready ?? (() => true), () => now += 20_000);
    }

    [Fact]
    public void Channel_appears_with_floor_and_auto_mode()
    {
        var provider = Make(new FakePsu());
        var ch = Assert.Single(provider.GetFanChannels());
        Assert.Equal(Id, ch.Id);
        Assert.Equal(30, ch.MinDuty);
        Assert.Equal("Auto", ch.Mode, ignoreCase: true);
        Assert.Equal("Corsair HX1500i", ch.DeviceName);
    }

    [Fact]
    public void No_device_or_host_not_ready_yields_no_channels_and_no_traffic()
    {
        var absent = new FakePsu { Present = null };
        Assert.Empty(Make(absent).GetFanChannels());

        var psu = new FakePsu();
        Assert.Empty(Make(psu, () => false).GetFanChannels());
        Assert.Empty(psu.Sent);
    }

    [Fact]
    public void First_contact_in_software_mode_is_handed_back_to_hardware()
    {
        var psu = new FakePsu { Mode = 1 };
        Make(psu).GetFanChannels();
        Assert.Equal(0, psu.Mode);
        Assert.Single(psu.HardwareModeWrites.ToList());
    }

    [Fact]
    public void Write_sets_software_mode_then_a_clamped_duty()
    {
        var psu = new FakePsu();
        var provider = Make(psu);
        provider.GetFanChannels();
        psu.Sent.Clear();

        Assert.Equal(30, provider.SetFanSpeed(Id, 5));
        var writes = psu.Writes.ToList();
        Assert.Equal(new byte[] { 0x02, 0xF0, 0x01 }, writes[0][..3]);
        Assert.Equal(new byte[] { 0x02, 0x3B, 30 }, writes[1][..3]);
        Assert.Equal(1, psu.Mode);

        psu.Sent.Clear();
        provider.DriveFanSpeed(Id, 72);
        Assert.Equal(new byte[] { 0x02, 0x3B, 72 }, Assert.Single(psu.Writes.ToList())[..3]);
        Assert.Equal(72, provider.GetFanChannels()[0].DutyPercent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(29)]
    public void No_write_ever_goes_below_the_floor(int requested)
    {
        var psu = new FakePsu();
        var provider = Make(psu);
        provider.SetFanSpeed(Id, requested);
        Assert.All(psu.Writes.Where(f => f[1] == 0x3B), f => Assert.True(f[2] >= 30));
    }

    [Fact]
    public void ReleaseFan_and_ReleaseAll_write_hardware_mode_and_verify_it()
    {
        var psu = new FakePsu();
        var provider = Make(psu);
        provider.SetFanSpeed(Id, 60);
        provider.ReleaseFan(Id);
        Assert.Equal(0, psu.Mode);
        Assert.Single(psu.HardwareModeWrites.ToList());
        Assert.Equal("Auto", provider.GetFanChannels()[0].Mode, ignoreCase: true);

        provider.SetFanSpeed(Id, 60);
        psu.Sent.Clear();
        provider.ReleaseAll();
        Assert.Equal(0, psu.Mode);
        Assert.Single(psu.HardwareModeWrites.ToList());
    }

    [Fact]
    public void Release_without_control_sends_nothing()
    {
        var psu = new FakePsu();
        var provider = Make(psu);
        provider.GetFanChannels();
        psu.Sent.Clear();
        provider.ReleaseAll();
        Assert.Empty(psu.Sent);
    }

    [Fact]
    public void Dispose_restores_hardware_mode()
    {
        var psu = new FakePsu();
        var provider = Make(psu);
        provider.SetFanSpeed(Id, 60);
        provider.Dispose();
        Assert.Equal(0, psu.Mode);
        provider.SetFanSpeed(Id, 60);
        Assert.Equal(0, psu.Mode);
    }

    [Fact]
    public void Resume_reasserts_software_mode_before_the_next_duty()
    {
        var psu = new FakePsu();
        var provider = Make(psu);
        provider.SetFanSpeed(Id, 60);
        provider.RetakeControlAfterResume();
        psu.Sent.Clear();
        provider.DriveFanSpeed(Id, 61);
        var writes = psu.Writes.ToList();
        Assert.Equal(2, writes.Count);
        Assert.Equal(0xF0, writes[0][1]);
        provider.ReleaseAll();
        Assert.Equal(0, psu.Mode);
    }

    [Fact]
    public void Reply_that_does_not_echo_fails_closed_and_returns_to_hardware()
    {
        var psu = new FakePsu();
        var provider = Make(psu);
        provider.SetFanSpeed(Id, 60);
        psu.WrongEcho = true;
        provider.DriveFanSpeed(Id, 70);
        // fail-closed restore also saw a bad echo, so only the stop is observable
        psu.WrongEcho = false;
        psu.Sent.Clear();
        provider.DriveFanSpeed(Id, 80);
        Assert.DoesNotContain(psu.Writes, f => f[1] == 0x3B);
        Assert.True(provider.GetFanChannels()[0].ReadOnly);
    }

    [Fact]
    public void Failing_closed_while_in_software_mode_writes_hardware_mode()
    {
        var psu = new FakePsu();
        var provider = Make(psu);
        provider.SetFanSpeed(Id, 60);
        psu.Silent = false;
        psu.ModeReadBack = 7;
        provider.GetFanChannels();
        psu.ModeReadBack = null;
        // next RPM read replies normally, but force a bad one
        psu.WrongEcho = true;
        for (var i = 0; i < 3; i++) provider.GetFanChannels();
        psu.WrongEcho = false;
        Assert.Equal(0, psu.Mode);
        Assert.True(provider.GetFanChannels()[0].ReadOnly);
        psu.Sent.Clear();
        provider.SetFanSpeed(Id, 90);
        Assert.Empty(psu.Writes);
    }

    [Fact]
    public void Software_mode_that_does_not_read_back_fails_closed_without_a_duty_write()
    {
        var psu = new FakePsu { IgnoreModeWrites = true };
        var provider = Make(psu);
        provider.SetFanSpeed(Id, 60);
        Assert.DoesNotContain(psu.Writes, f => f[1] == 0x3B);
        Assert.True(provider.GetFanChannels()[0].ReadOnly);
    }

    [Fact]
    public void Unreadable_mode_at_start_fails_closed_without_any_write()
    {
        var psu = new FakePsu { ModeReadBack = 9 };
        var provider = Make(psu);
        provider.SetFanSpeed(Id, 60);
        Assert.Empty(psu.Writes);
    }

    [Fact]
    public void Silent_device_fails_closed_and_stops_writing()
    {
        var psu = new FakePsu { Silent = true };
        var provider = Make(psu);
        provider.SetFanSpeed(Id, 60);
        var sent = psu.Sent.Count;
        provider.SetFanSpeed(Id, 60);
        Assert.Equal(sent, psu.Sent.Count);
    }

    [Fact]
    public void Writes_before_the_sensor_library_has_opened_are_dropped()
    {
        var psu = new FakePsu();
        var provider = Make(psu, () => false);
        Assert.Equal(40, provider.SetFanSpeed(Id, 40));
        Assert.Empty(psu.Sent);
    }

    [Fact]
    public void Removal_clears_the_channel_and_a_replug_runs_the_start_check_again()
    {
        var psu = new FakePsu();
        var provider = Make(psu);
        provider.SetFanSpeed(Id, 60);
        psu.Present = null;
        Assert.Empty(provider.GetFanChannels());

        psu.Present = new CorsairPsuInfo(0x1C1F, "psu", null);
        psu.Mode = 1;
        Assert.Single(provider.GetFanChannels());
        Assert.Equal(0, psu.Mode);
    }

    [Fact]
    public void Foreign_ids_are_ignored()
    {
        var psu = new FakePsu();
        var provider = Make(psu);
        provider.SetFanSpeed("corsair:ch1", 50);
        provider.ReleaseFan("corsair:ch1");
        Assert.Empty(psu.Sent);
        Assert.True(provider.ReleaseRestoresHardwareControl(Id));
        Assert.False(provider.ReleaseRestoresHardwareControl("corsair:ch1"));
    }
}
