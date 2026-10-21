using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.JpegPanels;
using Xunit;

namespace Nexus.Service.Tests.JpegPanels;

/// <summary>
/// The session the HydroShift LCD follows from the reference driver (sgtaziz/lian-li-linux
/// hydroshift_lcd): settle, firmware, status handshake, pause, claim, then a status
/// keepalive and LCD availability recovery. Fake transport and recorded sleeps, so these pin
/// ordering and bytes, not what a panel does with them.
/// </summary>
public class LianLiAioReferenceSessionTests
{
    private const int ReportLength = 1024;

    private static LianLiAioHandshake Session(List<int>? sleeps = null, List<string>? order = null) =>
        new("lianli-hydroshift-lcd", 24, referenceSession: true, (ms, _) =>
        {
            sleeps?.Add(ms);
            order?.Add("sleep");
        }, () => 0);

    [Fact]
    public void Attach_orders_firmware_then_status_handshake_then_a_pause_then_the_claim()
    {
        var order = new List<string>();
        var sleeps = new List<int>();
        var device = new FakeDevice { OnWrite = w => order.Add($"{w[0]:X2}{w[1]:X2}") };

        Assert.True(Session(sleeps, order).OnAttach(device, ReportLength));

        Assert.Equal(new[] { "0186", "0181", "sleep", "020C" }, order);
        Assert.Equal(new[] { 2000 }, sleeps);
    }

    [Fact]
    public void The_reference_session_settles_before_attach_and_ticks_once_a_second()
    {
        var handshake = Session();

        Assert.True(handshake.SettleMs >= 10_000);
        Assert.Equal(1000, handshake.TickIntervalMs);
    }

    [Fact]
    public void The_plain_family_handshake_keeps_its_verified_attach_and_does_not_tick()
    {
        var handshake = new LianLiAioHandshake("lianli-galahad2-lcd", 24);
        var device = new FakeDevice();

        Assert.True(handshake.OnAttach(device, ReportLength));
        handshake.Tick(device, ReportLength, 60_000);

        Assert.Equal(0, handshake.SettleMs);
        Assert.Equal(0, handshake.TickIntervalMs);
        Assert.Equal(new[] { 0x86, 0x0C }, device.Writes.Select(w => (int)w[1]));
    }

    [Fact]
    public void Only_the_hydroshift_model_opts_into_the_reference_session()
    {
        Assert.True(JpegPanelModel.HydroShiftLcd.Handshake is IJpegPanelSession { SettleMs: > 0 });
        Assert.True(JpegPanelModel.GalahadIiLcd.Handshake is IJpegPanelSession { SettleMs: 0 });
    }

    [Fact]
    public void A_silent_firmware_is_retried_with_a_pause_between_attempts()
    {
        var sleeps = new List<int>();
        var device = new FakeDevice { ReadResult = 0 };

        Assert.True(Session(sleeps).OnAttach(device, ReportLength));

        Assert.Equal(3, device.Writes.Count(w => w[0] == 0x01 && w[1] == 0x86));
        Assert.Equal(new[] { 2000, 2000, 2000 }, sleeps);
    }

    [Fact]
    public void The_keepalive_polls_status_once_a_second_not_faster()
    {
        var handshake = Session();
        var device = new FakeDevice();
        handshake.OnAttach(device, ReportLength);
        device.Writes.Clear();

        handshake.Tick(device, ReportLength, 500);
        Assert.Empty(device.Writes);

        handshake.Tick(device, ReportLength, 1000);
        var poll = Assert.Single(device.Writes);
        Assert.Equal(new byte[] { 0x01, 0x81, 0, 0, 0, 0 }, poll[..6]);

        handshake.Tick(device, ReportLength, 1500);
        Assert.Single(device.Writes, w => w[1] == 0x81);

        handshake.Tick(device, ReportLength, 2000);
        Assert.Equal(2, device.Writes.Count(w => w[1] == 0x81));
    }

    [Fact]
    public void The_availability_request_is_a_bare_report_b_with_command_0x17()
    {
        var handshake = Session();
        var device = new FakeDevice();
        handshake.OnAttach(device, ReportLength);
        device.Writes.Clear();

        handshake.Tick(device, ReportLength, 2000);

        var request = Assert.Single(device.Writes, w => w[0] == 0x02);
        Assert.Equal(0x17, request[1]);
        Assert.All(request[2..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void An_available_lcd_is_never_reset()
    {
        var handshake = Session();
        var device = new FakeDevice();
        handshake.OnAttach(device, ReportLength);
        device.Writes.Clear();

        for (long t = 2000; t <= 20_000; t += 1000)
        {
            handshake.Tick(device, ReportLength, t);
        }

        Assert.Contains(device.Writes, w => w[0] == 0x02 && w[1] == 0x17);
        Assert.DoesNotContain(device.Writes, w => w[0] == 0x01 && w[1] == 0x8E);
    }

    [Fact]
    public void A_silent_panel_counts_as_available()
    {
        var handshake = Session();
        var device = new FakeDevice();
        handshake.OnAttach(device, ReportLength);
        device.ReadResult = 0;
        device.Writes.Clear();

        for (long t = 2000; t <= 20_000; t += 2000)
        {
            handshake.Tick(device, ReportLength, t);
        }

        Assert.DoesNotContain(device.Writes, w => w[0] == 0x01 && w[1] == 0x8E);
    }

    [Fact]
    public void Three_unavailable_reports_reset_the_panel_then_reclaim_the_glass()
    {
        var sleeps = new List<int>();
        var handshake = Session(sleeps);
        var device = new FakeDevice { LcdUnavailable = true };
        handshake.OnAttach(device, ReportLength);
        device.Writes.Clear();
        sleeps.Clear();

        handshake.Tick(device, ReportLength, 2000);
        handshake.Tick(device, ReportLength, 4000);
        Assert.DoesNotContain(device.Writes, w => w[0] == 0x01 && w[1] == 0x8E);

        handshake.Tick(device, ReportLength, 6000);

        var tail = device.Writes
            .Where(w => (w[0] == 0x01 && w[1] == 0x8E) || (w[0] == 0x02 && w[1] == 0x0C))
            .ToList();
        Assert.Equal(2, tail.Count);
        Assert.Equal(0x8E, tail[0][1]);
        Assert.Equal(0x0C, tail[1][1]);
        Assert.Equal(0x01, tail[1][11]);
        Assert.Equal(100, tail[1][12]);
        Assert.Equal(new[] { 500 }, sleeps);
    }

    [Fact]
    public void A_failed_reset_still_reclaims_the_glass()
    {
        var sleeps = new List<int>();
        var handshake = Session(sleeps);
        var device = new FakeDevice { LcdUnavailable = true, ResetFails = true };
        handshake.OnAttach(device, ReportLength);
        device.Writes.Clear();
        sleeps.Clear();

        handshake.Tick(device, ReportLength, 2000);
        handshake.Tick(device, ReportLength, 4000);
        handshake.Tick(device, ReportLength, 6000);

        Assert.Contains(device.Writes, w => w[0] == 0x01 && w[1] == 0x8E);
        Assert.Contains(device.Writes, w => w[0] == 0x02 && w[1] == 0x0C && w[11] == 0x01);
        Assert.Empty(sleeps);
    }

    [Fact]
    public void Recovery_waits_out_the_cooldown_before_checking_again()
    {
        var handshake = Session();
        var device = new FakeDevice { LcdUnavailable = true };
        handshake.OnAttach(device, ReportLength);
        handshake.Tick(device, ReportLength, 2000);
        handshake.Tick(device, ReportLength, 4000);
        handshake.Tick(device, ReportLength, 6000);
        device.Writes.Clear();

        handshake.Tick(device, ReportLength, 7000);

        Assert.DoesNotContain(device.Writes, w => w[0] == 0x02 && w[1] == 0x17);
    }

    [Fact]
    public void No_availability_check_runs_while_frames_are_flowing()
    {
        var handshake = Session();
        var device = new FakeDevice { LcdUnavailable = true };
        handshake.OnAttach(device, ReportLength);
        device.Writes.Clear();

        for (long t = 2000; t <= 12_000; t += 1000)
        {
            handshake.BeforeFrame(device, ReportLength, t);
            handshake.Tick(device, ReportLength, t);
        }

        Assert.DoesNotContain(device.Writes, w => w[0] == 0x02 && w[1] == 0x17);
    }

    [Fact]
    public void The_hub_ticks_an_attached_session_and_counts_a_rejected_frame()
    {
        var model = JpegPanelModel.HydroShiftLcd with { Handshake = Session() };
        using var hub = new JpegPanelHub(model);
        var device = new FakeDevice();
        Assert.True(hub.Attach(device));
        device.Writes.Clear();

        Assert.Equal(1000, hub.TickIntervalMs);
        Assert.True(hub.SettleMs > 0);

        device.FailWrites = true;
        Assert.False(hub.SendFrame(new byte[] { 1, 2, 3 }));
        Assert.False(hub.SendFrame(new byte[] { 1, 2, 3 }));

        Assert.Equal(2, hub.FrameFailures);
    }

    [Fact]
    public void The_frame_failure_log_fires_first_time_and_is_rate_limited()
    {
        using var hub = new JpegPanelHub(JpegPanelModel.HydroShiftLcd);

        Assert.True(hub.TryClaimFailureLog(0));
        Assert.False(hub.TryClaimFailureLog(1000));
        Assert.False(hub.TryClaimFailureLog(9_999));
        Assert.True(hub.TryClaimFailureLog(10_000));
        // A tick clock that has just started must not be swallowed by the initial state either.
        using var fresh = new JpegPanelHub(JpegPanelModel.HydroShiftLcd);
        Assert.True(fresh.TryClaimFailureLog(5));
    }

    [Fact]
    public void A_rejected_reclaim_after_reset_drops_the_handle_so_the_worker_re_attaches()
    {
        var model = JpegPanelModel.HydroShiftLcd with { Handshake = Session() };
        using var hub = new JpegPanelHub(model);
        var device = new FakeDevice { LcdUnavailable = true };
        Assert.True(hub.Attach(device));
        Assert.True(hub.IsConnected);

        // The reset re-enumerates USB: the reset is acknowledged, then the handle dies.
        device.OnWrite = w =>
        {
            if (w[0] == 0x01 && w[1] == 0x8E)
            {
                device.FailWrites = true;
            }
        };
        long t = Environment.TickCount64 + 100_000;
        var session = (IJpegPanelSession)model.Handshake!;
        Assert.True(session.Tick(device, ReportLength, t));
        Assert.True(session.Tick(device, ReportLength, t + 2000));
        Assert.False(session.Tick(device, ReportLength, t + 4000));
    }

    [Fact]
    public void The_hub_marks_itself_detached_when_the_session_reports_a_dead_handle()
    {
        var model = JpegPanelModel.HydroShiftLcd with { Handshake = Session() };
        using var hub = new JpegPanelHub(model);
        long now = 0;
        hub.Clock = () => now;
        var device = new FakeDevice();
        Assert.True(hub.Attach(device));
        device.FailWrites = true;

        // Keepalive writes fail three ticks in a row.
        for (int i = 0; i < 3 && hub.IsConnected; i++)
        {
            now += 1100;
            hub.Tick();
        }

        Assert.False(hub.IsConnected);
    }

    [Fact]
    public void Attach_releases_the_hub_lock_across_pauses_and_queues_a_brightness_set_meanwhile()
    {
        JpegPanelHub? hub = null;
        bool reachable = false;
        bool applied = true;
        var handshake = new LianLiAioHandshake("lianli-hydroshift-lcd", 24, true, (_, _) =>
        {
            // Another thread must get the lock while attach is pausing.
            reachable = Task.Run(() => hub!.Serial).Wait(2000);
            if (reachable)
            {
                applied = Task.Run(() => hub!.SetBrightness(40)).Result;
            }
        }, () => 0);
        hub = new JpegPanelHub(JpegPanelModel.HydroShiftLcd with { Handshake = handshake });
        using var _ = hub;
        var device = new FakeDevice();

        Assert.True(hub.Attach(device));

        Assert.True(reachable);
        Assert.False(applied);
        var claim = device.Writes.Last(w => w[0] == 0x02 && w[1] == 0x0C);
        Assert.Equal(40, claim[12]);
        Assert.Equal(0x01, claim[11]);
    }

    [Fact]
    public void Cancelling_during_a_pause_aborts_attach_and_closes_the_handle()
    {
        using var cts = new CancellationTokenSource();
        var handshake = new LianLiAioHandshake("lianli-hydroshift-lcd", 24, true, (_, _) => cts.Cancel(), () => 0);
        using var hub = new JpegPanelHub(JpegPanelModel.HydroShiftLcd with { Handshake = handshake });
        var device = new FakeDevice();

        Assert.ThrowsAny<OperationCanceledException>(() => hub.Attach(device, cts.Token));

        Assert.False(hub.IsConnected);
        Assert.True(device.Disposed);
        Assert.DoesNotContain(device.Writes, w => w[0] == 0x02 && w[1] == 0x0C);
    }

    [Fact]
    public void Backoff_doubles_per_failed_cycle_up_to_the_cap_and_clears_when_healthy()
    {
        long now = 0;
        var backoff = new AttachBackoff(() => now);
        Assert.Equal(0, backoff.DelayMs);

        var delays = new List<long>();
        for (int i = 0; i < 9; i++)
        {
            backoff.NoteFailure();
            delays.Add(backoff.DelayMs);
        }

        Assert.Equal(new long[] { 5_000, 10_000, 20_000, 40_000, 80_000, 160_000, 300_000, 300_000, 300_000 }, delays);
        now += 100_000;
        Assert.Equal(200_000, backoff.DelayMs);

        backoff.NoteHealthy();
        Assert.Equal(0, backoff.DelayMs);
        backoff.NoteFailure();
        Assert.Equal(5_000, backoff.DelayMs);
    }

    [Fact]
    public void Repeat_failures_collapse_into_one_summary_per_interval()
    {
        long now = 0;
        var backoff = new AttachBackoff(() => now);

        Assert.Equal(1, backoff.NoteFailure());
        int silent = 0;
        for (int i = 0; i < 5; i++)
        {
            now += 60_000;
            silent += backoff.NoteFailure();
        }
        Assert.Equal(0, silent);

        now += AttachBackoff.SummaryIntervalMs;
        Assert.Equal(6, backoff.NoteFailure());
    }

    [Fact]
    public void Clearing_the_backoff_makes_a_replug_attach_at_the_normal_cadence()
    {
        long now = 0;
        var backoff = new AttachBackoff(() => now);
        for (int i = 0; i < 6; i++)
        {
            backoff.NoteFailure();
        }
        Assert.True(backoff.DelayMs > 100_000);

        backoff.Clear();

        Assert.Equal(0, backoff.DelayMs);
        Assert.Equal(0, backoff.Failures);
    }

    [Fact]
    public void A_healthy_connection_reports_how_many_failures_it_ended()
    {
        var backoff = new AttachBackoff(() => 0);
        backoff.NoteFailure();
        backoff.NoteFailure();

        Assert.Equal(2, backoff.NoteHealthy());
        Assert.Equal(0, backoff.NoteHealthy());
    }

    [Fact]
    public void Galahad_keeps_its_fixed_retry_because_only_a_session_model_backs_off()
    {
        using var hub = new JpegPanelHub(JpegPanelModel.GalahadIiLcd);
        Assert.Equal(0, hub.SettleMs);

        var backoff = new AttachBackoff(() => 0, enabled: hub.SettleMs > 0);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(0, backoff.NoteFailure());
        }

        Assert.Equal(0, backoff.DelayMs);
        Assert.Equal(0, backoff.Failures);
    }

    [Fact]
    public async Task The_backoff_wait_ends_and_clears_when_the_device_leaves_mid_wait()
    {
        long now = 0;
        var backoff = new AttachBackoff(() => now);
        for (int i = 0; i < 6; i++)
        {
            backoff.NoteFailure();
        }
        int slices = 0;
        bool gone = false;

        await backoff.WaitAsync(
            () => gone,
            (ms, _) =>
            {
                Assert.True(ms <= AttachBackoff.WaitSliceMs);
                now += ms;
                gone = ++slices == 3;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(3, slices);
        Assert.Equal(0, backoff.DelayMs);
        Assert.Equal(0, backoff.Failures);
    }

    [Fact]
    public async Task The_backoff_wait_runs_its_full_course_when_nothing_changes_and_stops_on_cancel()
    {
        long now = 0;
        var backoff = new AttachBackoff(() => now);
        backoff.NoteFailure();

        await backoff.WaitAsync(() => false, (ms, _) => { now += ms; return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(0, backoff.DelayMs);
        Assert.Equal(1, backoff.Failures);

        backoff.NoteFailure();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => backoff.WaitAsync(() => false, (_, _) => Task.CompletedTask, cts.Token));
    }

    [Fact]
    public async Task A_disabled_backoff_never_waits()
    {
        var backoff = new AttachBackoff(() => 0, enabled: false);
        backoff.NoteFailure();
        int calls = 0;

        await backoff.WaitAsync(() => false, (_, _) => { calls++; return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal(0, calls);
    }

    private static byte[] AResponse(byte cmd, string text)
    {
        var frame = new byte[64];
        frame[0] = 0x01;
        frame[1] = cmd;
        var payload = Encoding.ASCII.GetBytes(text);
        frame[5] = (byte)payload.Length;
        payload.CopyTo(frame, 6);
        return frame;
    }

    /// <summary>Answers each write the way the panel does: echoing A-commands, reporting LCD availability, acking resets.</summary>
    private sealed class FakeDevice : IHidDevice
    {
        public List<byte[]> Writes { get; } = new();
        public Action<byte[]>? OnWrite { get; set; }
        public int ReadResult { get; set; } = 64;
        public bool LcdUnavailable { get; set; }
        public bool ResetFails { get; set; }
        public bool FailWrites { get; set; }

        public int VendorId => 0x0416;
        public int ProductId => 0x7399;
        public string Path => "/dev/fake-hydroshift-v1";
        public string? Serial => "FAKE";
        public int UsagePage => 0xFF1A;
        public int Usage => 0x93;

        public bool Write(ReadOnlySpan<byte> report)
        {
            var copy = report.ToArray();
            Writes.Add(copy);
            OnWrite?.Invoke(copy);
            return !FailWrites;
        }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (ReadResult <= 0)
            {
                return ReadResult;
            }
            var reply = Reply();
            int copy = Math.Min(reply.Length, buffer.Length);
            reply.AsSpan(0, copy).CopyTo(buffer);
            return copy;
        }

        private byte[] Reply()
        {
            var last = Writes.Count > 0 ? Writes[^1] : new byte[ReportLength];
            if (last[0] == 0x01 && last[1] == 0x8E)
            {
                var reset = AResponse(0x8E, "x");
                reset[6] = (byte)(ResetFails ? 0 : 1);
                return reset;
            }
            if (last[0] == 0x01)
            {
                return AResponse(last[1], "1.3");
            }
            if (last[0] == 0x02 && last[1] == 0x17)
            {
                var b = new byte[ReportLength];
                b[0] = 0x02;
                b[1] = 0x17;
                b[10] = 1;
                b[11] = (byte)(LcdUnavailable ? 1 : 0);
                return b;
            }
            return AResponse(0x0C, "");
        }

        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => false;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
