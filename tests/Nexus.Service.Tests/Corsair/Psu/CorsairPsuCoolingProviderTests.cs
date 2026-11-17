using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nexus.Service.Cooling;
using Nexus.Service.Peripherals.Hyte.MiniHub;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Plugins;
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

    private static CorsairPsuCoolingProvider Make(
        FakePsu psu, Func<bool>? ready = null, long start = 0, Func<float?>? temp = null, int gateWaitMs = 1_500)
    {
        var now = start;
        IReadOnlyList<PsuTemperature> Read() => temp?.Invoke() is { } t ? new[] { new PsuTemperature("PSU VRM", t) } : Array.Empty<PsuTemperature>();
        return new CorsairPsuCoolingProvider(psu, ready ?? (() => true), () => now += 20_000, Read, gateWaitMs);
    }

    private sealed class NoPorts : INp50PortDiscovery
    {
        public IReadOnlyList<Np50PortInfo> Discover() => Array.Empty<Np50PortInfo>();
    }

    private sealed class Board : IFanControlProvider
    {
        public int Resumes;
        public IReadOnlyList<Nexus.Service.Models.Cooling.FanChannel> GetFanChannels() => Array.Empty<Nexus.Service.Models.Cooling.FanChannel>();
        public IReadOnlyList<Nexus.Service.Models.Cooling.TemperatureSource> GetTemperatureSources() => Array.Empty<Nexus.Service.Models.Cooling.TemperatureSource>();
        public float? ReadTemperature(string sensorId) => null;
        public int SetFanSpeed(string channelId, int dutyPercent) => dutyPercent;
        public void DriveFanSpeed(string channelId, int dutyPercent) { }
        public void ReleaseFan(string channelId) { }
        public void ReleaseAll() { }
        public void RetakeControlAfterResume() => Resumes++;
        public System.Threading.Tasks.Task<IReadOnlyList<Nexus.Service.Models.Cooling.FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds, IProgress<Nexus.Service.Models.Cooling.FanCalibrationProgress> progress, CancellationToken ct) =>
            System.Threading.Tasks.Task.FromResult<IReadOnlyList<Nexus.Service.Models.Cooling.FanCalibration>>(Array.Empty<Nexus.Service.Models.Cooling.FanCalibration>());
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
    [Fact]
    public void Composite_forwards_resume_to_the_psu_provider_and_the_next_write_redoes_the_handshake()
    {
        var psu = new FakePsu();
        var provider = Make(psu);
        var board = new Board();
        var noPorts = new NoPorts();
        var composite = new CompositeFanControlProvider(
            board,
            new Np50CoolingProvider(new Np50Hub(noPorts, _ => null!)),
            new MiniHubCoolingProvider(new MiniHubHub(noPorts, _ => null!)),
            new PluginProviderRegistry(),
            new CompositeFanControlProvider.FanSource(CorsairPsuCoolingProvider.IsCorsairPsuId, provider));
        composite.SetFanSpeed(Id, 60);

        psu.Sent.Clear();
        composite.RetakeControlAfterResume();
        composite.DriveFanSpeed(Id, 61);

        Assert.Equal(1, board.Resumes);
        Assert.Equal(0xFE, psu.Sent[0][0]);
        var writes = psu.Writes.ToList();
        Assert.Equal(0xF0, writes[0][1]);
        Assert.Equal(new byte[] { 0x02, 0x3B, 61 }, writes[1][..3]);
    }

    [Fact]
    public void A_new_hid_path_resets_the_session_and_the_start_check_runs_on_the_new_path()
    {
        var psu = new FakePsu();
        var provider = Make(psu);
        provider.SetFanSpeed(Id, 60);
        psu.Present = new CorsairPsuInfo(0x1C1F, "psu-2", null);
        psu.Mode = 1;
        Assert.Single(provider.GetFanChannels());
        Assert.Equal(0, psu.Mode);
        Assert.Equal("Auto", provider.GetFanChannels()[0].Mode, ignoreCase: true);
    }

    [Fact]
    public void A_failed_restore_is_retried_until_it_reads_back_as_hardware_mode()
    {
        var psu = new FakePsu();
        var provider = Make(psu);
        provider.SetFanSpeed(Id, 60);
        psu.IgnoreModeWrites = true;
        provider.ReleaseAll();
        Assert.Equal(1, psu.Mode);

        psu.IgnoreModeWrites = false;
        provider.GetFanChannels();
        Assert.Equal(0, psu.Mode);
        psu.Sent.Clear();
        provider.GetFanChannels();
        Assert.Empty(psu.HardwareModeWrites);
    }

    [Fact]
    public void Release_that_cannot_get_the_bus_is_applied_once_the_bus_is_free()
    {
        var psu = new FakePsu();
        var provider = Make(psu, gateWaitMs: 20);
        provider.SetFanSpeed(Id, 60);

        using var held = new ManualResetEventSlim();
        using var done = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            lock (CorsairPsuBus.Gate)
            {
                held.Set();
                done.Wait();
            }
        });
        holder.Start();
        held.Wait();
        var started = DateTime.UtcNow;
        provider.ReleaseAll();
        provider.Dispose();
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
        Assert.Equal(1, psu.Mode);
        done.Set();
        holder.Join();

        var second = Make(psu);
        second.SetFanSpeed(Id, 60);
        second.ReleaseAll();
        Assert.Equal(0, psu.Mode);
    }

    [Fact]
    public void Heat_returns_the_fan_to_the_psu_and_holds_writes_until_it_cools()
    {
        var psu = new FakePsu();
        float? temp = 50;
        var provider = Make(psu, temp: () => temp);
        provider.SetFanSpeed(Id, 60);
        Assert.Equal(1, psu.Mode);

        temp = 76;
        provider.GetFanChannels();
        Assert.Equal(0, psu.Mode);

        psu.Sent.Clear();
        provider.SetFanSpeed(Id, 60);
        Assert.Empty(psu.Writes);

        temp = 70;
        provider.SetFanSpeed(Id, 60);
        Assert.Empty(psu.Writes);

        temp = 64;
        provider.SetFanSpeed(Id, 60);
        Assert.Equal(1, psu.Mode);
    }

    [Fact]
    public void Missing_temperature_never_blocks_control()
    {
        var psu = new FakePsu();
        var provider = Make(psu, temp: () => null);
        provider.SetFanSpeed(Id, 60);
        Assert.Equal(1, psu.Mode);
    }

    [Fact]
    public void The_provider_lists_no_temperature_sources_of_its_own()
    {
        var psu = new FakePsu();
        var provider = new CorsairPsuCoolingProvider(
            psu, () => true, () => 0, () => new[] { new PsuTemperature("PSU VRM", 41f) });

        Assert.Empty(provider.GetTemperatureSources());
        Assert.Null(provider.ReadTemperature("corsair-psu:1:temp:psuvrm"));
    }
}
