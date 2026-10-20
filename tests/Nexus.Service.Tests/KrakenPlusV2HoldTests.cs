using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Nzxt;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>How the Plus V2 holds, refreshes and releases a frame, against recording doubles.</summary>
public class KrakenPlusV2HoldTests
{
    private static readonly KrakenModel Plus = KrakenModel.Find(0x3014)!;

    private sealed class Rig
    {
        public readonly LcdLockState Lock = new();
        public readonly KrakenHub Hub;
        public readonly Hid Device = new();
        public readonly Lcd Panel = new();
        public long Now = 1_000_000;

        public Rig()
        {
            Hub = new KrakenHub(new Factory(Panel)) { Clock = () => Now };
            Hub.FadeRunner.AutoRun = false;
            Hub.BindLockSleep(Lock, () => true);
            Hub.Attach(Device, Plus, KrakenProtocol.ReportLength);
            Assert.True(Hub.Connect());
            Assert.True(Hub.SetLcdBacklight(60, 0));
            Device.Writes.Clear();
        }

        public byte[] Frame(byte fill = 0)
        {
            var f = new byte[Plus.LcdFrameBytes];
            Array.Fill(f, fill);
            return f;
        }

        public int ModeWrites(byte mode) =>
            Device.Writes.Count(w => w[0] == 0x38 && w[1] == 0x01 && w[2] == mode);

        public void Advance(int ms)
        {
            Now += ms;
            Hub.StepFade();
        }
    }

    [Fact]
    public void A_clear_during_an_in_flight_resend_is_not_undone()
    {
        var rig = new Rig();
        Assert.True(rig.Hub.PushStreamFrame(rig.Frame(1)));
        rig.Now += 1_000;
        rig.Panel.OnPayload = () => rig.Hub.SetDisplayMode(KrakenDisplayMode.Liquid);
        rig.Hub.RefreshIdleFrame();
        rig.Panel.OnPayload = null;
        Assert.Equal(2, rig.Panel.Frames);

        rig.Now += 5_000;
        rig.Hub.RefreshIdleFrame();
        Assert.Equal(2, rig.Panel.Frames);
        Assert.Equal(250, rig.Hub.IdleRefreshDelayMs());
    }

    [Fact]
    public void A_clear_during_an_in_flight_stream_frame_is_not_undone()
    {
        var rig = new Rig();
        rig.Panel.OnPayload = () => rig.Hub.ReleaseStream();
        Assert.True(rig.Hub.PushStreamFrame(rig.Frame(1)));
        rig.Panel.OnPayload = null;
        rig.Now += 1_500;
        rig.Hub.RefreshIdleFrame();
        Assert.Equal(1, rig.Panel.Frames);
    }

    [Fact]
    public void A_liquid_mode_picked_while_the_pc_is_locked_ends_the_hold_on_unlock()
    {
        var rig = new Rig();
        Assert.True(rig.Hub.PushStreamFrame(rig.Frame(1)));
        rig.Lock.Set(true);
        rig.Advance(5_000);
        Assert.True(rig.Hub.LockDimmed);
        Assert.True(rig.Hub.SetDisplayMode(KrakenDisplayMode.Liquid));

        rig.Device.Writes.Clear();
        rig.Lock.Set(false);
        rig.Advance(5_000);
        Assert.Equal(1, rig.ModeWrites((byte)KrakenDisplayMode.Liquid));

        rig.Now += 5_000;
        rig.Hub.RefreshIdleFrame();
        Assert.Equal(1, rig.Panel.Frames);
    }

    [Fact]
    public void Liquid_stops_the_resend_and_sends_its_command_even_when_the_device_does_not_answer()
    {
        var rig = new Rig();
        Assert.True(rig.Hub.PushStreamFrame(rig.Frame(1)));
        rig.Device.DropModeReplies = true;

        Assert.False(rig.Hub.SetDisplayMode(KrakenDisplayMode.Liquid));
        Assert.Equal(1, rig.ModeWrites((byte)KrakenDisplayMode.Liquid));
        rig.Now += 5_000;
        rig.Hub.RefreshIdleFrame();
        Assert.Equal(1, rig.Panel.Frames);
    }

    [Fact]
    public void The_stream_ending_sends_the_closing_mode_command_after_the_vendor_delay()
    {
        var rig = new Rig();
        Assert.True(rig.Hub.PushStreamFrame(rig.Frame(1)));
        rig.Device.Writes.Clear();

        rig.Hub.ReleaseStream();
        rig.Now += 500;
        rig.Hub.RefreshIdleFrame();
        Assert.Equal(0, rig.ModeWrites((byte)KrakenDisplayMode.Liquid));

        rig.Now += 400;
        rig.Hub.RefreshIdleFrame();
        Assert.Equal(1, rig.ModeWrites((byte)KrakenDisplayMode.Liquid));
        rig.Now += 5_000;
        rig.Hub.RefreshIdleFrame();
        Assert.Equal(1, rig.ModeWrites((byte)KrakenDisplayMode.Liquid));
        Assert.Equal(1, rig.Panel.Frames);
    }

    [Fact]
    public void Releasing_the_stream_leaves_a_held_still_alone()
    {
        var rig = new Rig();
        Assert.True(rig.Hub.UploadLcdImage(rig.Frame(1)));
        rig.Hub.ReleaseStream();
        rig.Now += 1_000;
        rig.Hub.RefreshIdleFrame();
        Assert.Equal(2, rig.Panel.Frames);
    }

    [Fact]
    public void A_still_reports_the_image_mode_and_picking_image_reuses_it_without_the_bucket_command()
    {
        var rig = new Rig();
        Assert.True(rig.Hub.UploadLcdImage(rig.Frame(1)));
        Assert.Equal(KrakenDisplayMode.Bucket, rig.Hub.Snapshot.DisplayMode);

        Assert.True(rig.Hub.SetDisplayMode(KrakenDisplayMode.Liquid));
        Assert.Equal(KrakenDisplayMode.Liquid, rig.Hub.Snapshot.DisplayMode);
        rig.Device.Writes.Clear();

        Assert.True(rig.Hub.SetDisplayMode(KrakenDisplayMode.Bucket));
        rig.Hub.RefreshIdleFrame();
        Assert.Equal(2, rig.Panel.Frames);
        Assert.Equal(KrakenDisplayMode.Bucket, rig.Hub.Snapshot.DisplayMode);
        Assert.Equal(0, rig.ModeWrites((byte)KrakenDisplayMode.Bucket));
    }

    [Fact]
    public void Picking_image_with_no_stored_still_is_refused()
    {
        var rig = new Rig();
        Assert.False(rig.Hub.SetDisplayMode(KrakenDisplayMode.Bucket));
        Assert.Equal(0, rig.ModeWrites((byte)KrakenDisplayMode.Bucket));
    }

    [Fact]
    public void A_rotation_change_re_encodes_the_held_still()
    {
        var rig = new Rig();
        var still = rig.Frame(0);
        still[0] = 0xFF;
        still[3] = 0xFF;
        Assert.True(rig.Hub.UploadLcdImage(still));
        Assert.Equal(new byte[] { 0xF8, 0x00 }, rig.Panel.LastPayloadFirstBytes);

        Assert.True(rig.Hub.SetLcdBacklight(60, 1));
        rig.Hub.RefreshIdleFrame();
        Assert.Equal(2, rig.Panel.Frames);
        Assert.Equal(new byte[] { 0x00, 0x00 }, rig.Panel.LastPayloadFirstBytes);
    }

    [Fact]
    public void Frames_after_a_lock_dim_re_enter_the_live_session_first()
    {
        var rig = new Rig();
        Assert.True(rig.Hub.PushStreamFrame(rig.Frame(1)));
        rig.Lock.Set(true);
        rig.Advance(5_000);
        rig.Lock.Set(false);
        rig.Advance(5_000);

        rig.Device.Writes.Clear();
        Assert.True(rig.Hub.PushStreamFrame(rig.Frame(2)));
        var lcdCommands = rig.Device.Writes.Where(w => w[0] == 0x36).Select(w => w[1]).ToArray();
        Assert.Equal(new byte[] { 0x04, 0x03, 0x01, 0x02 }, lcdCommands);
    }

    [Fact]
    public void The_idle_resend_deadline_tracks_the_last_send_and_resends_the_cached_frame()
    {
        var rig = new Rig();
        Assert.True(rig.Hub.PushStreamFrame(rig.Frame(1)));
        Assert.Equal(1_000, rig.Hub.IdleRefreshDelayMs());

        rig.Now += 400;
        Assert.Equal(600, rig.Hub.IdleRefreshDelayMs());
        rig.Hub.RefreshIdleFrame();
        Assert.Equal(1, rig.Panel.Frames);

        rig.Now += 599;
        rig.Hub.RefreshIdleFrame();
        Assert.Equal(1, rig.Panel.Frames);

        rig.Now += 1;
        rig.Hub.RefreshIdleFrame();
        Assert.Equal(2, rig.Panel.Frames);
        Assert.Equal(1_000, rig.Hub.IdleRefreshDelayMs());
    }

    [Fact]
    public void A_non_plus_model_gets_the_idle_wait_and_no_end_command_after_a_stream_release()
    {
        var device = new Hid();
        var panel = new Lcd();
        var hub = new KrakenHub(new Factory(panel));
        hub.Attach(device, KrakenModel.Find(0x300C)!, 64);
        Assert.True(hub.Connect());
        Assert.True(hub.PushStreamFrame(new byte[KrakenModel.Find(0x300C)!.LcdFrameBytes]));
        device.Writes.Clear();

        hub.ReleaseStream();
        Assert.Equal(250, hub.IdleRefreshDelayMs());
        hub.RefreshIdleFrame();
        Assert.Empty(device.Writes);
    }

    [Fact]
    public void A_still_marked_while_the_pc_is_dark_gets_the_idle_wait_and_returns_on_unlock()
    {
        var rig = new Rig();
        Assert.True(rig.Hub.UploadLcdImage(rig.Frame(1)));
        Assert.True(rig.Hub.SetDisplayMode(KrakenDisplayMode.Liquid));
        Assert.True(rig.Hub.SetDisplayMode(KrakenDisplayMode.Bucket));
        Assert.Equal(20, rig.Hub.IdleRefreshDelayMs());

        rig.Lock.Set(true);
        rig.Advance(5_000);
        rig.Hub.RefreshIdleFrame();
        Assert.Equal(250, rig.Hub.IdleRefreshDelayMs());
        Assert.Equal(1, rig.Panel.Frames);

        rig.Lock.Set(false);
        rig.Advance(5_000);
        rig.Hub.RefreshIdleFrame();
        Assert.Equal(2, rig.Panel.Frames);
    }

    [Fact]
    public async Task The_refresh_loop_sleeps_without_waking_until_a_plus_is_connected()
    {
        int wakes = 0;
        int refreshes = 0;
        var loop = new KrakenIdleRefresher(() => { Interlocked.Increment(ref wakes); return 1; }, () => Interlocked.Increment(ref refreshes));
        using var cts = new CancellationTokenSource();
        var run = loop.RunAsync(cts.Token);

        await Task.Delay(300);
        Assert.Equal(0, Volatile.Read(ref wakes));

        loop.SetActive(true);
        await Task.Delay(200);
        Assert.True(Volatile.Read(ref refreshes) > 0);

        loop.SetActive(false);
        await Task.Delay(100);
        int settled = Volatile.Read(ref wakes);
        await Task.Delay(200);
        Assert.Equal(settled, Volatile.Read(ref wakes));

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task The_refresh_loop_survives_an_exception()
    {
        int calls = 0;
        var loop = new KrakenIdleRefresher(
            () => 1,
            () =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    throw new InvalidOperationException("boom");
                }
            },
            faultBackoffMs: 5);
        using var cts = new CancellationTokenSource();
        loop.SetActive(true);
        var run = loop.RunAsync(cts.Token);

        await Task.Delay(300);
        Assert.True(Volatile.Read(ref calls) > 1);
        Assert.False(run.IsCompleted);

        cts.Cancel();
        await run;
    }

    private sealed class Hid : IHidDevice
    {
        private readonly Queue<byte[]> _replies = new();
        public List<byte[]> Writes { get; } = new();
        public bool DropModeReplies { get; set; }
        public int VendorId => 0x1E71;
        public int ProductId => 0x3014;
        public string Path => "fake";
        public string? Serial => "TEST";
        public int UsagePage => 0xFF00;
        public int Usage => 1;

        public bool Write(ReadOnlySpan<byte> report)
        {
            var copy = report.ToArray();
            Writes.Add(copy);
            if (DropModeReplies && copy[0] == 0x38)
            {
                return true;
            }
            var reply = new byte[KrakenProtocol.ReportLength];
            reply[0] = (byte)(copy[0] + 1);
            reply[1] = copy[1];
            reply[14] = KrakenProtocol.AckOk;
            if (copy[0] == 0x10)
            {
                reply[0x11] = 1;
                reply[0x12] = 0;
            }
            _replies.Enqueue(reply);
            return true;
        }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (_replies.Count == 0) return 0;
            var r = _replies.Dequeue();
            r.CopyTo(buffer);
            return r.Length;
        }

        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => true;
        public void Dispose() { }
    }

    private sealed class Lcd : IKrakenLcdTransport
    {
        public int Frames { get; private set; }
        public byte[] LastPayloadFirstBytes { get; private set; } = Array.Empty<byte>();
        public Action? OnPayload { get; set; }

        public bool Write(ReadOnlySpan<byte> data)
        {
            if (data.Length == 20)
            {
                Frames++;
            }
            else
            {
                LastPayloadFirstBytes = data[..2].ToArray();
                OnPayload?.Invoke();
            }
            return true;
        }

        public void Dispose() { }
    }

    private sealed class Factory : IKrakenLcdTransportFactory
    {
        private readonly IKrakenLcdTransport _lcd;
        public Factory(IKrakenLcdTransport lcd) { _lcd = lcd; }
        public IKrakenLcdTransport? Open(string? serial) => _lcd;
    }
}
