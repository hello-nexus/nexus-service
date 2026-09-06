using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Peripherals.Ibp;
using Xunit;

namespace Nexus.Service.Tests.Ibp;

/// <summary>
/// Wire-format pins for the iBUYPOWER keyboards and mice, byte-for-byte
/// against the Nexus 2 LightDancing controllers they were transcribed from
/// (RedragonKeebController / RedragonMouseController / MK9KeyboardController /
/// RedragonMK4KeyboardController). No hardware was available when this
/// landed, so these tables ARE the contract.
/// </summary>
public class IbpPeripheralProtocolTests
{
    [Fact]
    public void Catalog_has_seven_models_with_unique_pids_on_the_hyte_vid()
    {
        var models = IbpPeripheralProtocol.Models;
        Assert.Equal(7, models.Count);
        Assert.Equal(models.Count, models.Select(m => m.ProductId).Distinct().Count());
        Assert.Equal(models.Count, models.Select(m => m.Id).Distinct().Count());
        Assert.Equal(0x3402, IbpPeripheralProtocol.VendorId);
        foreach (var m in models)
        {
            Assert.Same(m, IbpPeripheralProtocol.ForProductId(m.ProductId));
            Assert.True(m.LedCount > 0);
            Assert.True(m.LedCount <= m.WireSlots);
            Assert.All(m.Leds, led => Assert.InRange(led.WireSlot, 0, m.WireSlots - 1));
            Assert.Equal(m.LedCount, m.Leds.Select(l => l.WireSlot).Distinct().Count());
            Assert.All(m.Leds, led => Assert.InRange(led.Column, 0, m.GridWidth - 1));
            Assert.All(m.Leds, led => Assert.InRange(led.Row, 0, m.GridHeight - 1));
            // Frame order is wire order: the composer never re-sorts.
            Assert.Equal(m.Leds.Select(l => l.WireSlot), m.Leds.Select(l => l.WireSlot).OrderBy(x => x));
        }
        Assert.Null(IbpPeripheralProtocol.ForProductId(0x0300)); // Keeb TKL stays KeebHandler's
    }

    [Theory]
    [InlineData(0x0301, IbpPeripheralKind.Keyboard, "km7-keyboard", 24, 33, 3)]
    [InlineData(0x0305, IbpPeripheralKind.Keyboard, "km10-keyboard", 24, 33, 3)]
    [InlineData(0x0200, IbpPeripheralKind.Mouse, "km7-mouse", 6, 33, 1)]
    [InlineData(0x0201, IbpPeripheralKind.Mouse, "km10-mouse", 3, 33, 1)]
    [InlineData(0x0303, IbpPeripheralKind.Keyboard, "mk9-keyboard", 103, 520, 1)]
    [InlineData(0x0304, IbpPeripheralKind.Keyboard, "mk9pro-keyboard", 103, 520, 1)]
    [InlineData(0x0302, IbpPeripheralKind.Keyboard, "mek4-keyboard", 126, 382, 1)]
    public void Model_identity_led_count_and_report_shape(int pid, IbpPeripheralKind kind, string id, int leds, int reportLength, int reports)
    {
        var m = IbpPeripheralProtocol.ForProductId(pid)!;
        Assert.Equal(id, m.Id);
        Assert.Equal(kind, m.Kind);
        Assert.Equal(leds, m.LedCount);
        Assert.Equal(reportLength, m.ReportLength);
        Assert.Equal(reports, m.ReportCount);
        Assert.Equal(reportLength * reports, m.FrameBytes);
        Assert.StartsWith("iBUYPOWER ", m.Name);
        Assert.Equal(kind == IbpPeripheralKind.Keyboard ? "ibp-keyboard" : "ibp-mouse", m.HandlerId);
    }

    [Fact]
    public void Chimera_keyboard_frame_is_three_reports_of_nine_leds()
    {
        var m = IbpPeripheralProtocol.Km7Keyboard;
        var leds = Ramp(m.LedCount);
        var frame = new byte[m.FrameBytes];
        IbpPeripheralProtocol.EncodeFrame(m, leds, frame);

        for (var r = 0; r < 3; r++)
        {
            var report = frame.AsSpan(r * 33, 33);
            Assert.Equal(new byte[] { 0x08, 0x08, 0xF8, (byte)(r + 1), 0x00, 0x00 }, report.Slice(0, 6).ToArray());
        }
        // LED1 -> report 1 slot 0; LED10 -> report 2 slot 0; LED24 -> report 3 slot 5 (bytes 21..23).
        AssertColor(frame, 6, leds[0]);
        AssertColor(frame, 33 + 6, leds[9]);
        AssertColor(frame, 66 + 6 + 5 * 3, leds[23]);
        // Report 3 carries only six LEDs; slots 6..8 stay black.
        Assert.All(frame.AsSpan(66 + 6 + 6 * 3, 9).ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Chimera_keyboard_ring_starts_bottom_right_and_runs_clockwise()
    {
        var leds = IbpPeripheralProtocol.Km7Keyboard.Leds;
        Assert.Equal(new IbpLed(9, 5, 0), leds[0]);   // bottom row, right end
        Assert.Equal(new IbpLed(1, 5, 8), leds[8]);   // bottom row, left end
        Assert.Equal(new IbpLed(0, 4, 9), leds[9]);   // up the left side
        Assert.Equal(new IbpLed(1, 0, 13), leds[13]); // top row starts
        Assert.Equal(new IbpLed(10, 5, 23), leds[23]); // right side, bottom
        Assert.Equal(IbpPeripheralProtocol.Km7Keyboard.Leds, IbpPeripheralProtocol.Km10Keyboard.Leds);
    }

    [Fact]
    public void Chimera_mouse_frame_is_one_report_with_colors_from_byte_three()
    {
        var m = IbpPeripheralProtocol.Km7Mouse;
        var leds = Ramp(m.LedCount);
        var frame = new byte[m.FrameBytes];
        IbpPeripheralProtocol.EncodeFrame(m, leds, frame);
        Assert.Equal(new byte[] { 0x08, 0x1D, 0x01 }, frame.AsSpan(0, 3).ToArray());
        for (var i = 0; i < 6; i++) AssertColor(frame, 3 + i * 3, leds[i]);
        Assert.All(frame.AsSpan(3 + 18).ToArray(), b => Assert.Equal(0, b));

        var km10 = IbpPeripheralProtocol.Km10Mouse;
        var f10 = new byte[km10.FrameBytes];
        IbpPeripheralProtocol.EncodeFrame(km10, Ramp(3), f10);
        Assert.Equal(new byte[] { 0x08, 0x1D, 0x01 }, f10.AsSpan(0, 3).ToArray());
        AssertColor(f10, 3 + 2 * 3, Ramp(3)[2]);
    }

    [Fact]
    public void Mk9_frame_skips_the_ten_unpopulated_wire_slots()
    {
        var m = IbpPeripheralProtocol.Mk9Keyboard;
        var leds = Ramp(m.LedCount);
        var frame = new byte[m.FrameBytes];
        IbpPeripheralProtocol.EncodeFrame(m, leds, frame);
        Assert.Equal(520, frame.Length);
        Assert.Equal(new byte[] { 0x06, 0x08, 0x00, 0x00, 0x01, 0x00, 0x7A, 0x01 }, frame.AsSpan(0, 8).ToArray());

        // Wire slots 70,71,75,76,77,84,85,86,87,111 have no key: streamed black.
        var gaps = new[] { 70, 71, 75, 76, 77, 84, 85, 86, 87, 111 };
        foreach (var slot in gaps)
        {
            Assert.DoesNotContain(m.Leds, l => l.WireSlot == slot);
            Assert.All(frame.AsSpan(8 + slot * 3, 3).ToArray(), b => Assert.Equal(0, b));
        }
        // Slots 0 and 112 are populated: frame index 0 -> slot 0, last index -> slot 112.
        AssertColor(frame, 8, leds[0]);
        Assert.Equal(112, m.Leds[^1].WireSlot);
        AssertColor(frame, 8 + 112 * 3, leds[^1]);
        // Frame index 70 lands on wire slot 72 (two gaps before it).
        Assert.Equal(72, m.Leds[70].WireSlot);
        AssertColor(frame, 8 + 72 * 3, leds[70]);
        // Bottom letter row is shifted one column right of its slot column.
        Assert.Equal(new IbpLed(2, 4, 10), m.Leds[10]);
        Assert.Equal(new IbpLed(17, 4, 112), m.Leds[^1]);
        Assert.Equal(IbpPeripheralProtocol.Mk9Keyboard.Leds, IbpPeripheralProtocol.Mk9ProKeyboard.Leds);
    }

    [Fact]
    public void Mek4_frame_is_a_full_column_major_matrix()
    {
        var m = IbpPeripheralProtocol.Mek4Keyboard;
        var leds = Ramp(m.LedCount);
        var frame = new byte[m.FrameBytes];
        IbpPeripheralProtocol.EncodeFrame(m, leds, frame);
        Assert.Equal(382, frame.Length);
        Assert.Equal(new byte[] { 0x08, 0x0A, 0x7A, 0x01 }, frame.AsSpan(0, 4).ToArray());
        AssertColor(frame, 4, leds[0]);
        AssertColor(frame, 4 + 125 * 3, leds[125]);
        Assert.Equal(new IbpLed(0, 5, 5), m.Leds[5]);
        Assert.Equal(new IbpLed(1, 0, 6), m.Leds[6]);
        Assert.Equal(new IbpLed(20, 5, 125), m.Leds[125]);
    }

    [Fact]
    public void Short_or_long_led_spans_never_corrupt_the_header()
    {
        var m = IbpPeripheralProtocol.Km7Keyboard;
        var frame = new byte[m.FrameBytes];
        IbpPeripheralProtocol.EncodeFrame(m, Ramp(3), frame);
        Assert.Equal(0xF8, frame[2]);
        AssertColor(frame, 6 + 2 * 3, Ramp(3)[2]);
        Assert.All(frame.AsSpan(6 + 9, 33 - 15).ToArray(), b => Assert.Equal(0, b));

        IbpPeripheralProtocol.EncodeFrame(m, Ramp(200), frame);
        AssertColor(frame, 66 + 6 + 5 * 3, Ramp(200)[23]);
        Assert.Throws<ArgumentException>(() => IbpPeripheralProtocol.EncodeFrame(m, Ramp(1), new byte[10]));
    }

    [Fact]
    public void Mode_reports_match_the_vendor_controllers()
    {
        var kb = IbpPeripheralProtocol.SoftwareModeReport(IbpPeripheralProtocol.Km7Keyboard)!;
        Assert.Equal(33, kb.Length);
        Assert.Equal(new byte[] { 0x08, 0x09, 0xF9, 0x00 }, kb.AsSpan(0, 4).ToArray());
        var kbFw = IbpPeripheralProtocol.FirmwareModeReport(IbpPeripheralProtocol.Km10Keyboard)!;
        Assert.Equal(new byte[] { 0x08, 0x09, 0xF9, 0x01 }, kbFw.AsSpan(0, 4).ToArray());

        var mouse = IbpPeripheralProtocol.SoftwareModeReport(IbpPeripheralProtocol.Km7Mouse)!;
        Assert.Equal(new byte[] { 0x08, 0x2D, 0x00 }, mouse.AsSpan(0, 3).ToArray());
        var mouseFw = IbpPeripheralProtocol.FirmwareModeReport(IbpPeripheralProtocol.Km10Mouse)!;
        Assert.Equal(new byte[] { 0x08, 0x2D, 0x01 }, mouseFw.AsSpan(0, 3).ToArray());

        Assert.Null(IbpPeripheralProtocol.SoftwareModeReport(IbpPeripheralProtocol.Mk9Keyboard));
        Assert.Null(IbpPeripheralProtocol.FirmwareModeReport(IbpPeripheralProtocol.Mk9ProKeyboard));

        Assert.Null(IbpPeripheralProtocol.SoftwareModeReport(IbpPeripheralProtocol.Mek4Keyboard));
        var mek = IbpPeripheralProtocol.FirmwareModeReport(IbpPeripheralProtocol.Mek4Keyboard)!;
        Assert.Equal(382, mek.Length);
        Assert.Equal(new byte[] { 0x08, 0xF9, 0x01 }, mek.AsSpan(0, 3).ToArray());
    }

    [Fact]
    public void Uv_normalises_the_board_grid_and_tolerates_a_single_column()
    {
        var (u, v) = IbpPeripheralProtocol.Km7Keyboard.ComputeUv();
        Assert.Equal(24, u.Length);
        Assert.Equal(0.9f, u[0], 3);
        Assert.Equal(1f, v[0], 3);
        Assert.Equal(1f, u[23], 3);
        Assert.Equal(0f, v[13], 3);

        var (mu, mv) = IbpPeripheralProtocol.Km10Mouse.ComputeUv();
        Assert.All(mu, x => Assert.Equal(0.5f, x, 3));
        Assert.Equal(new[] { 0f, 0.5f, 1f }, mv);
    }

    private static RgbColor[] Ramp(int n)
    {
        var leds = new RgbColor[n];
        for (var i = 0; i < n; i++) leds[i] = new RgbColor((byte)(i + 1), (byte)(i + 101), (byte)(255 - i));
        return leds;
    }

    private static void AssertColor(byte[] frame, int offset, RgbColor c)
    {
        Assert.Equal(new[] { c.R, c.G, c.B }, frame.AsSpan(offset, 3).ToArray());
    }
}
