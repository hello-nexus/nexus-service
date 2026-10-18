using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.JpegPanels;
using Xunit;

namespace Nexus.Service.Tests.JpegPanels;

/// <summary>
/// The 360R pump and fan control over a fake HID transport: packet bytes, the product gate, attach
/// and lock ordering, the keepalive status readout and the release writes. It cannot say what the
/// pump or fans do with the bytes.
/// </summary>
public class HydroShiftCoolingTests
{
    private const int ReportLength = 1024;
    private const int Pid360S = 0x7398;
    private const int Pid360R = 0x7399;
    private const int Pid360Tl = 0x739A;

    private sealed class Rig
    {
        public JpegPanelHub Hub { get; set; } = null!;
        public HydroShiftAio Aio { get; set; } = null!;
        public FakeDevice Device { get; set; } = null!;
        public long Now;

        /// <summary>Runs the session's keepalive tick at a later time.</summary>
        public void Tick(long advanceMs = 1000)
        {
            Now += advanceMs;
            Hub.Tick();
        }
    }

    private static Rig Attached(int pid = Pid360R)
    {
        var model = JpegPanelModel.HydroShiftLcd with
        {
            Handshake = new LianLiAioHandshake("lianli-hydroshift-lcd", 24, true, (_, _) => { }, () => 0),
        };
        var rig = new Rig { Hub = new JpegPanelHub(model), Device = new FakeDevice(pid) };
        rig.Aio = new HydroShiftAio(rig.Hub, () => rig.Now);
        rig.Hub.Clock = () => rig.Now;
        Assert.True(rig.Hub.Attach(rig.Device));
        rig.Device.Writes.Clear();
        return rig;
    }

    private static byte[] StatusReply(int fanRpm, int pumpRpm) =>
        new byte[] { 0x01, 0x81, 0, 0, 0, 7, (byte)(fanRpm >> 8), (byte)fanRpm, (byte)(pumpRpm >> 8), (byte)pumpRpm, 1, 31, 4 };

    private static List<byte[]> Cmds(FakeDevice device, byte cmd) =>
        device.Writes.Where(w => w[0] == 0x01 && w[1] == cmd).ToList();

    [Fact]
    public void Pump_and_fan_writes_are_report_a_commands_8A_and_8B_with_a_two_byte_payload()
    {
        var report = new byte[ReportLength];

        LianLiAioProtocol.FillHydroShiftPumpPwm(report, 0, 60);
        Assert.Equal(new byte[] { 0x01, 0x8A, 0, 0, 0, 2, 0x00, 60 }, report[..8]);
        Assert.All(report[8..], b => Assert.Equal(0, b));

        LianLiAioProtocol.FillHydroShiftPumpPwm(report, LianLiAioProtocol.PumpSourceMotherboard, 1);
        Assert.Equal(new byte[] { 0x01, 0x8A, 0, 0, 0, 2, 0x01, 1 }, report[..8]);

        LianLiAioProtocol.FillHydroShiftFanPwm(report, 100);
        Assert.Equal(new byte[] { 0x01, 0x8B, 0, 0, 0, 2, 0x00, 95 }, report[..8]);
    }

    [Theory]
    [InlineData(100, 95)]
    [InlineData(55, 53)]
    [InlineData(11, 12)]
    [InlineData(10, 12)]
    [InlineData(9, 0)]
    [InlineData(0, 0)]
    public void The_360R_fan_remap_maps_10_to_100_onto_12_to_95_and_a_stop_below_10(int duty, int pwm)
    {
        Assert.Equal(pwm, LianLiAioProtocol.HydroShiftFanPwm(duty));
    }

    [Theory]
    [InlineData(-5, 12)]
    [InlineData(0, 12)]
    [InlineData(5, 12)]
    [InlineData(100, 95)]
    [InlineData(250, 95)]
    public void A_fan_write_never_asks_for_the_stop_below_10(int duty, int pwm)
    {
        var report = new byte[ReportLength];

        LianLiAioProtocol.FillHydroShiftFanPwm(report, duty);

        Assert.Equal(pwm, report[7]);
    }

    [Theory]
    [InlineData(-5, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(40, 40)]
    [InlineData(100, 100)]
    [InlineData(250, 100)]
    public void The_pump_is_never_commanded_below_its_floor(int duty, int pwm)
    {
        var rig = Attached();

        rig.Aio.SetPumpDuty(duty);

        Assert.Equal(new byte[] { 0x01, 0x8A, 0, 0, 0, 2, 0x00, (byte)pwm }, Assert.Single(Cmds(rig.Device, 0x8A))[..8]);
    }

    [Fact]
    public void The_pump_floor_is_the_first_entry_of_its_rpm_table()
    {
        Assert.Equal(1, LianLiAioProtocol.HydroShiftPumpPwmForRpm(1500));
        Assert.Equal(1, LianLiAioProtocol.HydroShiftPumpPwmForRpm(2000));
        Assert.Equal(50, LianLiAioProtocol.HydroShiftPumpPwmForRpm(2600));
        Assert.Equal(100, LianLiAioProtocol.HydroShiftPumpPwmForRpm(4000));
    }

    [Fact]
    public void A_fan_duty_writes_the_remapped_pwm_once_and_an_unchanged_duty_is_not_resent()
    {
        var rig = Attached();

        rig.Aio.SetFanDuty(55);
        rig.Aio.SetFanDuty(55);

        Assert.Equal(new byte[] { 0x01, 0x8B, 0, 0, 0, 2, 0x00, 53 }, Assert.Single(Cmds(rig.Device, 0x8B))[..8]);
    }

    [Fact]
    public void Wanted_speeds_are_resent_every_keepalive_including_after_a_reset_on_the_same_handle()
    {
        var rig = Attached();
        rig.Aio.SetPumpDuty(70);
        rig.Aio.SetFanDuty(55);
        rig.Tick();
        Assert.Equal(2, Cmds(rig.Device, 0x8A).Count);

        rig.Device.Writes.Clear();
        rig.Device.Writes.Add(new byte[] { 0x01, 0x8E });
        rig.Tick();

        Assert.Equal(70, Assert.Single(Cmds(rig.Device, 0x8A))[7]);
        Assert.Equal(53, Assert.Single(Cmds(rig.Device, 0x8B))[7]);
    }

    [Fact]
    public void Nothing_is_written_until_the_user_sets_a_speed()
    {
        var rig = Attached();

        for (int i = 0; i < 5; i++)
        {
            rig.Device.Reads.Enqueue(StatusReply(1700, 2700));
            rig.Tick();
        }

        Assert.Empty(Cmds(rig.Device, 0x8A));
        Assert.Empty(Cmds(rig.Device, 0x8B));
    }

    [Fact]
    public void Rpm_comes_from_the_keepalive_reply_with_one_status_poll_per_tick()
    {
        var rig = Attached();
        Assert.Null(rig.Aio.Status);

        rig.Device.Reads.Enqueue(StatusReply(1700, 2700));
        rig.Tick();

        Assert.Equal(1700, rig.Aio.Status!.FanRpm);
        Assert.Equal(2700, rig.Aio.Status!.PumpRpm);
        Assert.Single(Cmds(rig.Device, 0x81));
    }

    [Fact]
    public void The_status_is_found_behind_queued_frame_acks()
    {
        var rig = Attached();

        rig.Device.Reads.Enqueue(new byte[] { 0x02, 0x0E });
        rig.Device.Reads.Enqueue(new byte[] { 0x02, 0x0E });
        rig.Device.Reads.Enqueue(StatusReply(1650, 2900));
        rig.Tick();

        Assert.Equal(2900, rig.Aio.Status!.PumpRpm);
    }

    [Fact]
    public void A_stale_status_is_dropped()
    {
        var rig = Attached();
        rig.Device.Reads.Enqueue(StatusReply(1700, 2700));
        rig.Tick();

        rig.Now += 6000;

        Assert.Null(rig.Aio.Status);
    }

    [Fact]
    public void Detach_hands_the_pump_to_motherboard_sync_and_parks_the_fans_before_the_panel_is_released()
    {
        var rig = Attached();
        rig.Aio.SetPumpDuty(80);
        rig.Aio.SetFanDuty(60);
        rig.Device.Writes.Clear();

        rig.Hub.Detach();

        var writes = rig.Device.Writes;
        int pump = writes.FindIndex(w => w[0] == 0x01 && w[1] == 0x8A);
        int fans = writes.FindIndex(w => w[0] == 0x01 && w[1] == 0x8B);
        int panel = writes.FindIndex(w => w[0] == 0x02 && w[1] == 0x0C);
        Assert.Equal(new byte[] { 0x01, 0x8A, 0, 0, 0, 2, 0x01, 100 }, writes[pump][..8]);
        Assert.Equal(new byte[] { 0x01, 0x8B, 0, 0, 0, 2, 0x00, LianLiAioProtocol.HydroShiftFanPwm(LianLiAioProtocol.HydroShiftFanReleaseDuty) }, writes[fans][..8]);
        Assert.True(pump >= 0 && fans > pump && panel > fans);
    }

    [Fact]
    public void Detach_writes_nothing_for_a_pump_and_fans_the_user_never_set()
    {
        var rig = Attached();
        rig.Tick();

        rig.Hub.Detach();

        Assert.Empty(Cmds(rig.Device, 0x8A));
        Assert.Empty(Cmds(rig.Device, 0x8B));
    }

    [Fact]
    public void Releasing_a_driven_channel_hands_it_back_at_once_and_only_once()
    {
        var rig = Attached();
        rig.Aio.SetPumpDuty(80);
        rig.Device.Writes.Clear();

        rig.Aio.SetPumpDuty(null);
        rig.Aio.SetPumpDuty(null);
        rig.Hub.Detach();

        var pump = Cmds(rig.Device, 0x8A);
        Assert.Equal(new byte[] { 0x01, 0x8A, 0, 0, 0, 2, 0x01, 100 }, Assert.Single(pump)[..8]);
    }

    [Theory]
    [InlineData(Pid360S)]
    [InlineData(Pid360Tl)]
    public void Only_the_360R_takes_cooling_writes_or_has_its_keepalive_read(int pid)
    {
        var rig = Attached(pid);
        rig.Device.Reads.Enqueue(StatusReply(1700, 2700));
        rig.Device.Reads.Enqueue(StatusReply(1700, 2700));

        int readsBefore = rig.Device.ReadCount;

        rig.Aio.SetPumpDuty(70);
        rig.Aio.SetFanDuty(70);
        rig.Tick();
        int tickReads = rig.Device.ReadCount - readsBefore;
        rig.Hub.Detach();

        Assert.False(rig.Aio.IsConnected);
        Assert.Empty(Cmds(rig.Device, 0x8A));
        Assert.Empty(Cmds(rig.Device, 0x8B));
        Assert.Null(rig.Aio.Status);
        Assert.Equal(1, tickReads);
    }

    [Fact]
    public void Nothing_is_written_while_detached()
    {
        var hub = new JpegPanelHub(JpegPanelModel.HydroShiftLcd);
        var aio = new HydroShiftAio(hub);

        aio.SetPumpDuty(70);
        aio.SetFanDuty(70);

        Assert.False(aio.IsConnected);
    }

    [Fact]
    public void Nothing_is_written_until_the_attach_has_finished_then_the_first_keepalive_applies_the_wanted_speeds()
    {
        HydroShiftAio? aio = null;
        var model = JpegPanelModel.HydroShiftLcd with
        {
            Handshake = new LianLiAioHandshake("lianli-hydroshift-lcd", 24, true, (_, _) =>
            {
                aio!.SetPumpDuty(70);
                aio.SetFanDuty(70);
            }, () => 0),
        };
        var hub = new JpegPanelHub(model);
        long now = 0;
        aio = new HydroShiftAio(hub, () => now);
        hub.Clock = () => now;
        var device = new FakeDevice(Pid360R);

        Assert.True(hub.Attach(device));

        Assert.Empty(Cmds(device, 0x8A));
        Assert.Empty(Cmds(device, 0x8B));
        now = 1000;
        hub.Tick();
        Assert.Equal(70, Assert.Single(Cmds(device, 0x8A))[7]);
        Assert.Single(Cmds(device, 0x8B));
    }

    [Fact]
    public void A_new_attach_applies_the_wanted_speeds_again()
    {
        var rig = Attached();
        rig.Aio.SetPumpDuty(70);

        var next = new FakeDevice(Pid360R);
        Assert.True(rig.Hub.Attach(next));
        next.Writes.Clear();
        rig.Tick();

        Assert.Equal(70, Assert.Single(Cmds(next, 0x8A))[7]);
    }

    [Fact]
    public void A_rejected_write_backs_off_then_retries_on_a_later_tick()
    {
        var rig = Attached();
        rig.Device.FailWrites = true;
        rig.Aio.SetPumpDuty(70);
        int afterFailure = Cmds(rig.Device, 0x8A).Count;
        Assert.Equal(1, afterFailure);

        rig.Aio.SetPumpDuty(71);
        rig.Aio.SetPumpDuty(72);
        Assert.Equal(afterFailure, Cmds(rig.Device, 0x8A).Count);

        rig.Device.FailWrites = false;
        rig.Now = HydroShiftFanLeds.InitialBackoffMs;
        rig.Tick(0);

        Assert.Equal(72, Cmds(rig.Device, 0x8A)[^1][7]);
        Assert.Equal(afterFailure + 1, Cmds(rig.Device, 0x8A).Count);
    }

    [Fact]
    public async Task Cooling_writes_never_land_inside_a_frames_chunks()
    {
        var rig = Attached();
        var jpeg = new byte[1013 * 6];
        Array.Fill(jpeg, (byte)0xAB);
        var done = false;
        using var writerStarted = new ManualResetEventSlim();

        // The writer must be running before the first frame, else a starved thread pool lets
        // all frames finish first and the interleaving this test checks never happens.
        var frames = Task.Factory.StartNew(() =>
        {
            try
            {
                Assert.True(writerStarted.Wait(TimeSpan.FromSeconds(5)), "writer never started");
                for (int i = 0; i < 200; i++)
                {
                    rig.Hub.SendFrame(jpeg);
                }
            }
            finally
            {
                Volatile.Write(ref done, true);
            }
        }, TaskCreationOptions.LongRunning);
        var cooling = Task.Factory.StartNew(() =>
        {
            int n = 0;
            while (!Volatile.Read(ref done))
            {
                rig.Aio.SetPumpDuty(10 + (n % 80));
                rig.Aio.SetFanDuty(10 + (n % 80));
                n++;
                writerStarted.Set();
            }
        }, TaskCreationOptions.LongRunning);
        await Task.WhenAll(frames, cooling);

        int expectedChunk = 0;
        foreach (var w in rig.Device.Snapshot())
        {
            bool isFrameChunk = w[0] == 0x02 && w[1] == 0x0E;
            if (expectedChunk > 0)
            {
                Assert.True(isFrameChunk, "a non-frame write landed between two chunks of one frame");
            }
            if (isFrameChunk)
            {
                expectedChunk = expectedChunk + 1 >= 6 ? 0 : expectedChunk + 1;
            }
        }
        Assert.Equal(0, expectedChunk);
        Assert.Contains(rig.Device.Snapshot(), w => w[1] == 0x8A);
        Assert.Contains(rig.Device.Snapshot(), w => w[1] == 0x8B);
    }

    [Fact]
    public void The_provider_lists_a_fans_and_a_pump_channel_only_for_a_connected_360R()
    {
        var rig = Attached();
        rig.Device.Reads.Enqueue(StatusReply(1700, 2600));
        rig.Tick();
        var provider = new HydroShiftCoolingProvider(rig.Aio);

        var channels = provider.GetFanChannels();

        var fans = channels.Single(c => c.Kind == FanKinds.Fan);
        var pump = channels.Single(c => c.Kind == FanKinds.Pump);
        Assert.Equal("lianli-hydroshift-lcd:fans", fans.Id);
        Assert.Equal("lianli-hydroshift-lcd:pump", pump.Id);
        Assert.Equal(1700, fans.Rpm);
        Assert.Equal(2600, pump.Rpm);
        Assert.Equal(50, pump.DutyPercent);
        Assert.Equal(FanModes.Auto, fans.Mode);
        Assert.Equal(1, pump.MinDuty);
        Assert.Equal(10, fans.MinDuty);
        Assert.All(channels, c => Assert.Equal("lianli-hydroshift-lcd", c.DeviceId));
        Assert.True(HydroShiftCoolingProvider.IsHydroShiftLcdId(fans.Id));
        Assert.False(HydroShiftCoolingProvider.IsHydroShiftLcdId("lianli-aio:pump"));
        Assert.Equal(2, Assert.Single(provider.GetAll()).Devices.Count);

        var s = Attached(Pid360S);
        Assert.Empty(new HydroShiftCoolingProvider(s.Aio).GetFanChannels());
        Assert.Empty(new HydroShiftCoolingProvider(s.Aio).GetAll());
    }

    [Fact]
    public void The_provider_clamps_drives_and_releases_through_the_hub()
    {
        var rig = Attached();
        var provider = new HydroShiftCoolingProvider(rig.Aio);

        Assert.Equal(1, provider.SetFanSpeed("lianli-hydroshift-lcd:pump", 0));
        Assert.Equal(10, provider.SetFanSpeed("lianli-hydroshift-lcd:fans", 0));
        provider.DriveFanSpeed("lianli-hydroshift-lcd:fans", 100);
        Assert.Equal(FanModes.Manual, provider.GetFanChannels().Single(c => c.Kind == FanKinds.Fan).Mode);

        rig.Device.Writes.Clear();
        provider.ReleaseAll();

        Assert.Equal(0x01, Assert.Single(Cmds(rig.Device, 0x8A))[6]);
        Assert.Single(Cmds(rig.Device, 0x8B));
        Assert.All(provider.GetFanChannels(), c => Assert.Equal(FanModes.Auto, c.Mode));
    }

    private sealed class FakeDevice : IHidDevice
    {
        private readonly object _gate = new();
        private readonly List<byte[]> _writes = new();
        private int _reads;

        public FakeDevice(int productId)
        {
            ProductId = productId;
        }

        public List<byte[]> Writes => _writes;

        public Queue<byte[]> Reads { get; } = new();

        public int ReadCount => Volatile.Read(ref _reads);

        public bool FailWrites { get; set; }

        public int VendorId => 0x0416;
        public int ProductId { get; }
        public string Path => "/dev/fake-hydroshift-360r";
        public string? Serial => "FAKE";
        public int UsagePage => 0xFF1A;
        public int Usage => 0x93;

        public byte[][] Snapshot()
        {
            lock (_gate)
            {
                return _writes.ToArray();
            }
        }

        public bool Write(ReadOnlySpan<byte> report)
        {
            lock (_gate)
            {
                _writes.Add(report.ToArray());
            }
            return !FailWrites;
        }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            Interlocked.Increment(ref _reads);
            lock (_gate)
            {
                if (!Reads.TryDequeue(out var reply))
                {
                    return 0;
                }
                reply.AsSpan(0, Math.Min(reply.Length, buffer.Length)).CopyTo(buffer);
                return reply.Length;
            }
        }

        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => false;
        public void Dispose() { }
    }
}
