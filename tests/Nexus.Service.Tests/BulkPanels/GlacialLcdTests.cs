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

    private const string BaseReply = "EC 5C 00 00 00 32 00 00 00 32 14 00 00 00 00 32";

    [Theory]
    [InlineData(null, false, null, "EC 5C 01 00 00 32 00 00 00 32 00 00 00 00 00 32")]
    [InlineData(30, null, null, "EC 5C 01 00 00 1E 00 00 00 1E 14 00 00 00 00 1E")]
    [InlineData(70, null, null, "EC 5C 01 00 00 46 00 00 00 46 14 00 00 00 00 46")]
    [InlineData(70, null, true, "EC 5C 01 00 00 46 00 00 00 46 14 00 00 00 00 46 14")]
    [InlineData(null, null, null, "EC 5C 01 00 00 32 00 00 00 32 14 00 00 00 00 32")]
    public void Settings_edit_matches_the_captured_brightness_display_and_sleep_fields(int? percent, bool? displayOn, bool? keepOn, string expected)
    {
        Assert.Equal(Report(expected), GlacialLcdProtocol.EncodeSettingsEdit(Report(BaseReply), percent, displayOn, keepOn));
    }

    [Fact]
    public void Settings_edit_leaves_every_other_byte_of_the_reply_alone()
    {
        var edited = GlacialLcdProtocol.EncodeSettingsEdit(Report("EC 5C 00 00 00 28 00 00 00 29 14 AA BB 00 00 2A 14"), null, false, null);
        Assert.Equal(Report("EC 5C 01 00 00 28 00 00 00 29 00 AA BB 00 00 2A 14"), edited);
    }

    [Fact]
    public void Settings_brightness_is_clamped_to_a_percent()
    {
        Assert.Equal(100, GlacialLcdProtocol.EncodeSettingsEdit(Report(BaseReply), 250, null, null)[5]);
        Assert.Equal(0, GlacialLcdProtocol.EncodeSettingsEdit(Report(BaseReply), -5, null, null)[5]);
    }

    [Fact]
    public void A_state_write_is_not_mistaken_for_a_state_reply()
    {
        Assert.Null(GlacialLcdProtocol.ParseState(Report("EC 5C 01 00 00 32 00 00 00 32 14 00 00 00 00 32")));
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


    [Fact]
    public void The_custom_image_prelude_matches_the_complete_capture()
    {
        var prelude = GlacialLcdProtocol.EncodeCustomImagePrelude(0x5A12, 2);

        Assert.Equal(
            new[] { Report("EC 71 01 01"), Report("EC F1"), Report("EC 72 01 01 02"), Report("EC 73 01"), Report("EC 7F 02 12 5A") },
            prelude.Select(p => p.Report));
        Assert.Equal(new[] { 291, 25, 67, 112, 516 }, prelude.Select(p => p.GapMs));
    }

    [Theory]
    [InlineData(0, "EC 72 01 01")]
    [InlineData(1, "EC 72 01 01 01")]
    [InlineData(2, "EC 72 01 01 02")]
    public void The_target_slot_byte_matches_the_slots_the_vendor_apps_wrote(int slot, string expected)
    {
        Assert.Equal(Report(expected), GlacialLcdProtocol.EncodeCustomImagePrelude(0x1234, slot)[2].Report);
    }

    [Fact]
    public void All_six_stock_pictures_are_listed_and_match_the_captured_selects()
    {
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, GlacialLcdProtocol.StaticAssets);
        var expected = new[] { "EC 51 11", "EC 51 11 00 01", "EC 51 11 00 02", "EC 51 11 00 03", "EC 51 11 00 04", "EC 51 11 00 05" };
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(Report(expected[i]), GlacialLcdProtocol.EncodeMode(GlacialLcdProtocol.ModeStaticAsset, i));
        }
    }

    [Fact]
    public void The_finish_step_is_the_captured_end_report_and_the_custom_picture_select()
    {
        Assert.Equal(Report("EC 73 FF"), GlacialLcdProtocol.EncodeUploadEnd());
        Assert.Equal(Report("EC 51 11 01 02"), GlacialLcdProtocol.EncodeCustomPicture(2));
        // Armoury Crate's two uploads: slot 0 leaves the slot byte out, slot 1 names it.
        Assert.Equal(Report("EC 51 11 01"), GlacialLcdProtocol.EncodeCustomPicture(0));
        Assert.Equal(Report("EC 51 11 01 01"), GlacialLcdProtocol.EncodeCustomPicture(1));
    }

    [Fact]
    public void An_upload_size_outside_24_bits_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GlacialLcdProtocol.EncodeCustomImagePrelude(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => GlacialLcdProtocol.EncodeCustomImagePrelude(0x1000000, 1));
    }

    [Fact]
    public void The_close_write_mode_report_is_the_write_mode_command_with_the_enable_byte_cleared()
    {
        Assert.Equal(Report("EC 72 01 00"), GlacialLcdProtocol.EncodeCloseWriteMode());
    }

    [Fact]
    public void The_raw_stream_reports_are_the_frame_stream_mode_and_a_little_endian_frame_announce()
    {
        Assert.Equal(3_686_400, GlacialLcdProtocol.RawFrameBytes);
        Assert.Equal(Report("EC 51 20"), GlacialLcdProtocol.EncodeFrameStreamMode());
        Assert.Equal(Report("EC 7F 03 00 40 38 00"), GlacialLcdProtocol.EncodeFrameAnnounce(GlacialLcdProtocol.RawFrameBytes));
    }

    [Theory]
    [InlineData("EE 12", true, false, false, false)]
    [InlineData("EE 13 00 01", false, true, false, false)]
    [InlineData("EE 13 00 FF", false, false, false, true)]
    [InlineData("EE 13 00 03", false, false, false, false)]
    [InlineData("EE 14 00 00 10", false, false, true, false)]
    [InlineData("EC 5C 00", false, false, false, false)]
    [InlineData("EE", false, false, false, false)]
    public void Each_progress_report_matches_only_its_own_step(string hex, bool begin, bool stage, bool chunk, bool commit)
    {
        var bytes = Convert.FromHexString(hex.Replace(" ", string.Empty));
        Assert.Equal(begin, GlacialLcdProtocol.IsBeginAck(bytes));
        Assert.Equal(stage, GlacialLcdProtocol.IsStageAck(bytes));
        Assert.Equal(chunk, GlacialLcdProtocol.IsChunkAck(bytes));
        Assert.Equal(commit, GlacialLcdProtocol.IsCommitAck(bytes));
    }

    [Theory]
    [InlineData("EC 7F 00", true)]
    [InlineData("EC 7F 01", false)]
    [InlineData("EC 7F", false)]
    [InlineData("EE 7F 00", false)]
    public void Only_a_zero_status_frame_announce_reply_accepts_the_frame(string hex, bool accepted)
    {
        Assert.Equal(accepted, GlacialLcdProtocol.IsFrameAnnounceAck(Convert.FromHexString(hex.Replace(" ", string.Empty))));
    }
}
