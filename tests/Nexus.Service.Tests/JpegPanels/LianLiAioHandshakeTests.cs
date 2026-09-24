using System;
using System.Collections.Generic;
using System.Text;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.JpegPanels;
using Xunit;

namespace Nexus.Service.Tests.JpegPanels;

/// <summary>
/// The byte layouts here are checked against a hardware-verified third-party driver for the
/// same family (sgtaziz/lian-li-linux, MIT), not just against our own reading of it: the
/// panel silently keeps showing its firmware UI when the control packet is wrong, so a
/// green frame path is not evidence this is right.
/// </summary>
public class LianLiAioHandshakeTests
{
    private const int ReportLength = 1024;

    [Fact]
    public void Attach_asks_for_the_firmware_then_claims_the_glass()
    {
        var handshake = new LianLiAioHandshake("lianli-hydroshift-lcd", 24);
        var device = new ScriptedHidDevice();

        Assert.True(handshake.OnAttach(device, ReportLength));

        Assert.Equal(2, device.Writes.Count);
        Assert.All(device.Writes, w => Assert.Equal(ReportLength, w.Length));

        // A-command frame: report id 1, command 0x86, payload length 0.
        var firmware = device.Writes[0];
        Assert.Equal(0x01, firmware[0]);
        Assert.Equal(0x86, firmware[1]);
        Assert.Equal(0x00, firmware[5]);
    }

    [Fact]
    public void The_control_packet_matches_the_reference_driver_byte_for_byte()
    {
        var handshake = new LianLiAioHandshake("lianli-hydroshift-lcd", 24);
        var device = new ScriptedHidDevice();
        handshake.OnAttach(device, ReportLength);

        var control = device.Writes[1];
        // 11-byte sequenced header: report id 2, command 0x0C, big-endian total length,
        // 24-bit packet number, 16-bit payload length.
        Assert.Equal(new byte[] { 0x02, 0x0C, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x08 },
            control[..11]);
        // Payload: application mode, brightness, rotation, four reserved, frame rate.
        Assert.Equal(new byte[] { 0x01, 100, 0x00, 0x00, 0x00, 0x00, 0x00, 24 }, control[11..19]);
        // Everything past the payload is padding, not stale bytes from a previous write.
        Assert.All(control[19..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Galahad_brightness_packet_uses_the_hardware_verified_settings_mode()
    {
        var handshake = new LianLiAioHandshake("lianli-galahad2-lcd", 24);
        var device = new ScriptedHidDevice();

        Assert.True(handshake.OnAttach(device, ReportLength));
        device.Writes.Clear();
        handshake.SetBrightness(35);
        Assert.True(handshake.ApplyBrightness(device, ReportLength));

        var control = Assert.Single(device.Writes);
        Assert.Equal(
            new byte[]
            {
                0x02, 0x0C, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x08,
                0x04, 35, 0x00, 0x00, 0x00, 0x00, 0x00, 24,
            },
            control[..19]);
        Assert.All(control[19..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void A_brightness_change_uses_the_family_wide_settings_mode()
    {
        var handshake = new LianLiAioHandshake("lianli-hydroshift-lcd", 24);
        var device = new ScriptedHidDevice();
        handshake.OnAttach(device, ReportLength);
        device.Writes.Clear();

        handshake.SetBrightness(35);
        Assert.True(handshake.ApplyBrightness(device, ReportLength));

        var control = Assert.Single(device.Writes);
        Assert.Equal(0x0C, control[1]);
        // LcdSetting (0x04), the reference driver's brightness-only mode for the whole
        // family - not application mode, which OnAttach uses to take the glass in the
        // first place.
        Assert.Equal(new byte[] { 0x04, 35, 0x00, 0x00, 0x00, 0x00, 0x00, 24 }, control[11..19]);
    }

    [Fact]
    public void The_backlight_survives_a_re_attach_because_the_claim_packet_carries_it()
    {
        var handshake = new LianLiAioHandshake("lianli-galahad2-lcd", 24);
        handshake.SetBrightness(12);

        var device = new ScriptedHidDevice();
        handshake.OnAttach(device, ReportLength);

        Assert.Equal(12, device.Writes[1][12]);
    }

    [Fact]
    public void The_hand_back_restores_full_brightness_so_the_firmware_ui_is_not_left_dim()
    {
        var handshake = new LianLiAioHandshake("lianli-galahad2-lcd", 24);
        var device = new ScriptedHidDevice();
        handshake.OnAttach(device, ReportLength);
        handshake.SetBrightness(0);
        device.Writes.Clear();

        handshake.OnDetach(device, ReportLength);

        var control = Assert.Single(device.Writes);
        Assert.Equal(0x00, control[11]);
        Assert.Equal(LianLiAioHandshake.DefaultBrightness, control[12]);
    }

    [Fact]
    public void A_backlight_change_drains_its_own_ack_so_the_frame_path_keeps_its_budget()
    {
        var handshake = new LianLiAioHandshake("lianli-hydroshift-lcd", 24);
        var device = new ScriptedHidDevice();
        handshake.OnAttach(device, ReportLength);
        var readsAfterAttach = device.Reads;

        handshake.SetBrightness(50);
        Assert.True(handshake.ApplyBrightness(device, ReportLength));

        Assert.Equal(readsAfterAttach + 1, device.Reads);
    }

    [Fact]
    public void Detach_hands_the_glass_back_to_the_coolers_own_firmware()
    {
        var handshake = new LianLiAioHandshake("lianli-hydroshift-lcd", 24);
        var device = new ScriptedHidDevice();
        handshake.OnAttach(device, ReportLength);
        device.Writes.Clear();

        handshake.OnDetach(device, ReportLength);

        var control = Assert.Single(device.Writes);
        Assert.Equal(0x0C, control[1]);
        Assert.Equal(0x00, control[11]); // LocalUi, not Application
    }

    [Fact]
    public void A_refused_control_packet_drops_the_handle()
    {
        var handshake = new LianLiAioHandshake("lianli-hydroshift-lcd", 24);
        // The firmware request lands; the mode switch is refused.
        var device = new ScriptedHidDevice { FailWritesFrom = 1 };

        Assert.False(handshake.OnAttach(device, ReportLength));
    }

    [Fact]
    public void A_silent_panel_still_attaches_because_streaming_does_not_need_the_firmware()
    {
        var handshake = new LianLiAioHandshake("lianli-hydroshift-lcd", 24);
        var device = new ScriptedHidDevice { ReadResult = 0 };

        Assert.True(handshake.OnAttach(device, ReportLength));
    }

    [Fact]
    public void A_stale_reply_is_skipped_rather_than_read_as_the_firmware_string()
    {
        var handshake = new LianLiAioHandshake("lianli-hydroshift-lcd", 24);
        // A queued handshake response (0x81) precedes the firmware response (0x86).
        var device = new ScriptedHidDevice();
        device.Replies.Add(AResponse(0x81, "stale"));
        device.Replies.Add(AResponse(0x86, "N9,01,HS,SQ,HydroShift,V3.0C.013,1.3"));

        Assert.True(handshake.OnAttach(device, ReportLength));
        Assert.Equal(2, device.Reads);
    }

    [Fact]
    public void A_dead_handle_skips_the_frame_rather_than_writing_into_it()
    {
        var handshake = new LianLiAioHandshake("lianli-hydroshift-lcd", 24);
        var device = new ScriptedHidDevice { ReadResult = -1 };

        Assert.False(handshake.BeforeFrame(device, ReportLength, 0));
    }

    [Fact]
    public void A_queued_ack_lets_the_frame_through()
    {
        var handshake = new LianLiAioHandshake("lianli-hydroshift-lcd", 24);
        var device = new ScriptedHidDevice();

        Assert.True(handshake.BeforeFrame(device, ReportLength, 0));
    }

    /// <summary>A 64-byte A-frame: report id, command, three pad bytes, length, payload.</summary>
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

    private sealed class ScriptedHidDevice : IHidDevice
    {
        public List<byte[]> Writes { get; } = new();
        public List<byte[]> Replies { get; } = new();
        public int? FailWritesFrom { get; set; }
        public int ReadResult { get; set; } = 64;
        public int Reads { get; private set; }

        public int VendorId => 0x0416;
        public int ProductId => 0x7398;
        public string Path => "/dev/fake-hydroshift";
        public string? Serial => "FAKE";
        public int UsagePage => 0xFF1A;
        public int Usage => 0x93;

        public bool Write(ReadOnlySpan<byte> report)
        {
            bool ok = !(FailWritesFrom is { } from && Writes.Count >= from);
            Writes.Add(report.ToArray());
            return ok;
        }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (ReadResult <= 0)
            {
                return ReadResult;
            }
            var reply = Reads < Replies.Count ? Replies[Reads] : AResponse(0x86, "1.3");
            Reads++;
            int copy = Math.Min(reply.Length, buffer.Length);
            reply.AsSpan(0, copy).CopyTo(buffer);
            return copy;
        }

        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => false;
        public void Dispose() { }
    }
}

/// <summary>
/// The HydroShift's glass is a quarter turn clockwise from the frames it accepts, measured
/// on hardware. These pin the compensating turn, which is otherwise only visible on glass.
/// </summary>
public class BgraQuarterTurnTests
{
    [Fact]
    public void Counter_clockwise_sends_the_top_right_corner_to_the_top_left()
    {
        // 2x2, one distinct byte per pixel: TL=1 TR=2 BL=3 BR=4.
        var src = new byte[] { 1,1,1,1,  2,2,2,2,  3,3,3,3,  4,4,4,4 };
        var dest = new byte[src.Length];

        Nexus.Service.Peripherals.PixelFormats.BgraQuarterTurn.RotateCcw(src, 2, 2, dest);

        // CCW: TR->TL, BR->TR, TL->BL, BL->BR.
        Assert.Equal(new byte[] { 2,2,2,2,  4,4,4,4,  1,1,1,1,  3,3,3,3 }, dest);
    }

    [Fact]
    public void Four_turns_return_the_original_frame()
    {
        var src = new byte[4 * 4 * 4];
        for (int i = 0; i < src.Length; i++)
        {
            src[i] = (byte)(i * 7 % 251);
        }
        var a = new byte[src.Length];
        var b = new byte[src.Length];
        Nexus.Service.Peripherals.PixelFormats.BgraQuarterTurn.RotateCcw(src, 4, 4, a);
        Nexus.Service.Peripherals.PixelFormats.BgraQuarterTurn.RotateCcw(a, 4, 4, b);
        Nexus.Service.Peripherals.PixelFormats.BgraQuarterTurn.RotateCcw(b, 4, 4, a);
        Nexus.Service.Peripherals.PixelFormats.BgraQuarterTurn.RotateCcw(a, 4, 4, b);

        Assert.Equal(src, b);
    }

    [Fact]
    public void Only_the_hydroshift_declares_a_turn()
    {
        Assert.True(JpegPanelModel.HydroShiftLcd.QuarterTurnCcw);
        Assert.All(
            System.Linq.Enumerable.Where(JpegPanelModel.All, m => m.HandlerId != "lianli-hydroshift-lcd"),
            m => Assert.False(m.QuarterTurnCcw));
    }

    /// <summary>
    /// A turn swaps the frame's sides, but the encoder reads it back at the row's declared
    /// size, so a turned rectangular panel would render garbled with nothing to catch it.
    /// </summary>
    [Fact]
    public void A_turned_panel_must_be_square()
    {
        Assert.All(
            System.Linq.Enumerable.Where(JpegPanelModel.All, m => m.QuarterTurnCcw),
            m => Assert.Equal(m.Width, m.Height));
    }
}
