using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.BulkPanels;
using Xunit;

namespace Nexus.Service.Tests.BulkPanels;

/// <summary>Byte sequences are the MB Manager traffic in the customer's Nexus Echo captures, trailing zeros trimmed.</summary>
public class GlacialLcdTests
{
    private static byte[] Report(string hex)
    {
        var bytes = Convert.FromHexString(hex.Replace(" ", string.Empty));
        var report = new byte[GlacialLcdProtocol.ReportBytes];
        bytes.CopyTo(report, 0);
        return report;
    }

    [Fact]
    public void Query_and_hello_match_the_capture()
    {
        Assert.Equal(Report("EC DC"), GlacialLcdProtocol.EncodeQuery());
        Assert.Equal(Report("EC 82"), GlacialLcdProtocol.EncodeHello());
    }

    [Theory]
    [InlineData(50, false, false, "EC 5C 01 00 00 32 00 00 00 32 00 00 00 00 00 32")]
    [InlineData(50, true, false, "EC 5C 01 00 00 32 00 00 00 32 14 00 00 00 00 32")]
    [InlineData(30, true, false, "EC 5C 01 00 00 1E 00 00 00 1E 14 00 00 00 00 1E")]
    [InlineData(70, true, false, "EC 5C 01 00 00 46 00 00 00 46 14 00 00 00 00 46")]
    [InlineData(70, true, true, "EC 5C 01 00 00 46 00 00 00 46 14 00 00 00 00 46 14")]
    public void Settings_write_matches_the_captured_brightness_display_and_sleep_fields(int percent, bool displayOn, bool keepOn, string expected)
    {
        Assert.Equal(Report(expected), GlacialLcdProtocol.EncodeSettings(percent, displayOn, keepOn));
    }

    [Fact]
    public void Settings_brightness_is_clamped_to_a_percent()
    {
        Assert.Equal(100, GlacialLcdProtocol.EncodeSettings(250, false, false)[5]);
        Assert.Equal(0, GlacialLcdProtocol.EncodeSettings(-5, false, false)[5]);
    }

    [Fact]
    public void State_reply_parses_the_fields_the_settings_write_sets()
    {
        var reply = Report("EC 5C 00 00 00 32 00 00 00 32 14 00 00 00 00 32");
        var state = GlacialLcdProtocol.ParseState(reply);
        Assert.Equal((50, true, false), state);
        Assert.Null(GlacialLcdProtocol.ParseState(Report("EC 51")));
    }

    [Theory]
    [InlineData(GlacialLcdProtocol.ModeAnimatedAsset, 0, "EC 51 14")]
    [InlineData(GlacialLcdProtocol.ModeAnimatedAsset, 1, "EC 51 14 00 01")]
    [InlineData(GlacialLcdProtocol.ModeStaticAsset, 3, "EC 51 11 00 03")]
    [InlineData(GlacialLcdProtocol.ModeStaticAsset, 4, "EC 51 11 00 04")]
    public void Mode_select_matches_the_captured_asset_picks(byte mode, int asset, string expected)
    {
        Assert.Equal(Report(expected), GlacialLcdProtocol.EncodeMode(mode, asset));
    }

    [Fact]
    public void Off_and_hardware_monitor_modes_match_the_capture()
    {
        Assert.Equal(Report("EC 51"), GlacialLcdProtocol.EncodeOff());
        Assert.Equal(Report("EC 51 21"), GlacialLcdProtocol.EncodeHardwareMonitor());
    }

    [Theory]
    [InlineData(1, "EC 52")]
    [InlineData(2, "EC 52 00 01")]
    [InlineData(3, "EC 52 00 02")]
    public void Slot_count_matches_one_two_and_three_widget_captures(int slots, string expected)
    {
        Assert.Equal(Report(expected), GlacialLcdProtocol.EncodeSlotCount(slots));
    }

    [Fact]
    public void Slot_text_matches_the_captured_cpu_temperature_and_voltage_widgets()
    {
        Assert.Equal(
            Report("EC 53 00 43 50 55 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 34 33 2E 30 E2 84 83"),
            GlacialLcdProtocol.EncodeSlot(0, "CPU", "43.0℃"));
        Assert.Equal(
            Report("EC 53 01 43 50 55 20 50 61 63 6B 61 67 65 00 00 00 00 00 00 00 35 37 2E 30 E2 84 83"),
            GlacialLcdProtocol.EncodeSlot(1, "CPU Package", "57.0℃"));
        Assert.Equal(
            Report("EC 53 02 43 50 55 20 56 44 44 49 4F 20 2F 20 4D 43 00 00 00 00 31 2E 34 34 37 E2 86 8A"),
            GlacialLcdProtocol.EncodeSlot(2, "CPU VDDIO / MC", "1.447↊"));
    }

    [Fact]
    public void Slot_label_is_cut_on_a_character_boundary_and_always_leaves_a_nul()
    {
        var report = GlacialLcdProtocol.EncodeSlot(0, new string('\u2103', 10), "1");
        // Five whole three-byte characters fit in the label field; the byte before the value stays NUL.
        Assert.Equal(0xE2, report[3]);
        Assert.Equal(0x83, report[3 + 14]);
        Assert.Equal(0x00, report[GlacialLcdProtocol.SlotValueOffset - 1]);
        Assert.Equal(0x00, report[GlacialLcdProtocol.SlotValueOffset - 3]);
        Assert.Equal((byte)'1', report[GlacialLcdProtocol.SlotValueOffset]);
    }

}
