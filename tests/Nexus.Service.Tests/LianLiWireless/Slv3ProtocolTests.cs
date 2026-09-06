using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.LianLiWireless;

namespace Nexus.Service.Tests.LianLiWireless;

public class Slv3ProtocolTests
{
    private static readonly byte[] FanMac = { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };
    private static readonly byte[] MasterMac = { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };

    // ── Constants ──

    [Fact]
    public void Constants_match_known_values()
    {
        Assert.Equal(0x0416, Slv3Protocol.TxVendorId);
        Assert.Equal(0x8040, Slv3Protocol.TxProductId);
        Assert.Equal(0x8041, Slv3Protocol.RxProductId);
        Assert.Equal(0xE304, Slv3Protocol.TxProductIdWch);
        Assert.Equal(0xE305, Slv3Protocol.RxProductIdWch);
        Assert.Equal(0x01, Slv3Protocol.WritePipeId);
        Assert.Equal(0x81, Slv3Protocol.ReadPipeId);
        Assert.Equal(64, Slv3Protocol.UsbPacketSize);
        Assert.Equal(240, Slv3Protocol.RfPayloadSize);
        Assert.Equal(60, Slv3Protocol.RfChunkSize);
        Assert.Equal(6, Slv3Protocol.PwmFollowMotherboard);
        Assert.Equal(255, Slv3Protocol.PwmScaleMax);
        Assert.Equal(13, Slv3Protocol.MaxSlot);
    }

    // ── BuildUsbSendRf: 240 B payload -> 4 x 64 B frames ──

    [Fact]
    public void BuildUsbSendRf_fragments_240_into_four_frames()
    {
        var payload = new byte[Slv3Protocol.RfPayloadSize];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)i;

        var frames = Slv3Protocol.BuildUsbSendRf(channel: 8, rxType: 3, payload);

        Assert.Equal(4, frames.Length);
        for (var f = 0; f < 4; f++)
        {
            Assert.Equal(64, frames[f].Length);
            Assert.Equal(Slv3Protocol.UsbSendRf, frames[f][0]);
            Assert.Equal((byte)f, frames[f][1]);   // chunk seq
            Assert.Equal(8, frames[f][2]);          // channel
            Assert.Equal(3, frames[f][3]);          // rxType
        }
        // Frame 0 carries payload[0..59] at offset 4.
        Assert.Equal(0, frames[0][4]);
        Assert.Equal(59, frames[0][63]);
        // Frame 3 carries payload[180..239].
        Assert.Equal(180, frames[3][4]);
        Assert.Equal(239, frames[3][63]);
    }

    [Fact]
    public void BuildUsbSendRf_zero_pads_a_short_tail_chunk()
    {
        var payload = new byte[70]; // 60 + 10 -> 2 frames, second mostly zero
        Array.Fill(payload, (byte)0xAB);
        var frames = Slv3Protocol.BuildUsbSendRf(1, 1, payload);
        Assert.Equal(2, frames.Length);
        Assert.Equal(0xAB, frames[1][4 + 9]);   // last real byte
        Assert.Equal(0x00, frames[1][4 + 10]);  // padded
    }

    // ── GetMac ──

    [Fact]
    public void BuildGetMac_emits_cmd_and_channel()
    {
        var frame = Slv3Protocol.BuildGetMac(8);
        Assert.Equal(64, frame.Length);
        Assert.Equal(0x11, frame[0]);
        Assert.Equal(8, frame[1]);
    }

    [Fact]
    public void TryParseGetMac_reads_mac_timer_and_version()
    {
        var reply = new byte[64];
        reply[0] = 0x11;
        MasterMac.CopyTo(reply, 1);
        reply[7] = 0x00; reply[8] = 0x01; reply[9] = 0x02; reply[10] = 0x03; // timer BE
        reply[11] = 0x01; reply[12] = 0x2C;                                   // fw 300

        Assert.True(Slv3Protocol.TryParseGetMac(reply, out var mac, out var timer, out var fw));
        Assert.Equal(MasterMac, mac);
        Assert.Equal(0x00010203u, timer);
        Assert.Equal(300, fw);
    }

    [Fact]
    public void TryParseGetMac_rejects_wrong_echo()
    {
        var reply = new byte[64];
        reply[0] = 0x99;
        Assert.False(Slv3Protocol.TryParseGetMac(reply, out _, out _, out _));
    }

    // ── GetDev ──

    [Fact]
    public void BuildGetDev_emits_cmd_and_page_count()
    {
        var frame = Slv3Protocol.BuildGetDev(2);
        Assert.Equal(0x10, frame[0]);
        Assert.Equal(2, frame[1]);
    }

    // ── RF header + Bind (PWM) ──

    [Fact]
    public void WriteRfHeader_lays_out_bytes_0_to_17()
    {
        var buf = new byte[Slv3Protocol.RfPayloadSize];
        Slv3Protocol.WriteRfHeader(buf, Slv3Protocol.RfRgbSync, FanMac, MasterMac,
            targetRx: 3, targetChannel: 8, slot: 1, cmdSeq: 5);

        Assert.Equal(0x12, buf[0]);
        Assert.Equal(0x20, buf[1]);
        Assert.Equal(FanMac, buf.AsSpan(2, 6).ToArray());
        Assert.Equal(MasterMac, buf.AsSpan(8, 6).ToArray());
        Assert.Equal(3, buf[14]);
        Assert.Equal(8, buf[15]);
        Assert.Equal(1, buf[16]);
        Assert.Equal(5, buf[17]);
    }

    [Fact]
    public void BuildBind_carries_pwm_tuple_at_17()
    {
        byte[] pwm = { 50, 75, 0, 6 };
        var payload = Slv3Protocol.BuildBind(FanMac, MasterMac, targetRx: 2, targetChannel: 8, slot: 1, pwm);

        Assert.Equal(Slv3Protocol.RfPayloadSize, payload.Length);
        Assert.Equal(0x12, payload[0]);
        Assert.Equal(0x10, payload[1]);         // RF_Bind
        Assert.Equal(2, payload[14]);
        Assert.Equal(1, payload[16]);           // slot (non-zero = bound)
        Assert.Equal(50, payload[17]);
        Assert.Equal(75, payload[18]);
        Assert.Equal(0, payload[19]);
        Assert.Equal(6, payload[20]);           // mobo-sync sentinel preserved
    }

    [Fact]
    public void EncodeDuty_maps_percent_onto_the_255_scale_like_lconnect()
    {
        // Y70 USBPcap 2026-09-04: L-Connect put 127 / 255 / 63 / 12 on the wire
        // for 50 / 100 / 25 / 5 %, truncating (int)Map(0,100,0,255).
        Assert.Equal(127, Slv3Protocol.EncodeDuty(50));
        Assert.Equal(255, Slv3Protocol.EncodeDuty(100));
        Assert.Equal(63, Slv3Protocol.EncodeDuty(25));
        Assert.Equal(12, Slv3Protocol.EncodeDuty(5));
        Assert.Equal(0, Slv3Protocol.EncodeDuty(0));
        Assert.Equal(0, Slv3Protocol.EncodeDuty(-5));
        Assert.Equal(255, Slv3Protocol.EncodeDuty(150));
        Assert.Equal(15, Slv3Protocol.EncodeDuty(6));   // a percent of 6 is a real duty, not the sentinel
    }

    [Fact]
    public void EncodeDuty_never_emits_the_mobo_sync_sentinel_byte()
    {
        for (var percent = 0; percent <= 100; percent++)
        {
            Assert.NotEqual(Slv3Protocol.PwmFollowMotherboard, Slv3Protocol.EncodeDuty(percent));
        }
    }

    [Fact]
    public void DecodeDuty_inverts_the_255_scale()
    {
        Assert.Equal(50, Slv3Protocol.DecodeDuty(127));
        Assert.Equal(100, Slv3Protocol.DecodeDuty(255));
        Assert.Equal(25, Slv3Protocol.DecodeDuty(63));
        Assert.Equal(0, Slv3Protocol.DecodeDuty(0));
        Assert.Equal(100, Slv3Protocol.DecodeDuty(300));
    }

    [Fact]
    public void NeedSyncPwm_is_true_only_past_the_drift_threshold()
    {
        Assert.False(Slv3Protocol.NeedSyncPwm(new[] { 127, 127, 127, 127 }, new byte[] { 127, 127, 127, 127 }));
        Assert.False(Slv3Protocol.NeedSyncPwm(new[] { 122, 132, 127, 127 }, new byte[] { 127, 127, 127, 127 }));
        Assert.True(Slv3Protocol.NeedSyncPwm(new[] { 121, 127, 127, 127 }, new byte[] { 127, 127, 127, 127 }));
        Assert.True(Slv3Protocol.NeedSyncPwm(new[] { 100, 100, 100, 100 }, new byte[] { 117, 117, 117, 117 }));
        Assert.True(Slv3Protocol.NeedSyncPwm(new[] { 0, 0, 0, 0 }, new byte[] { 6, 6, 6, 6 }));
    }

    [Fact]
    public void NextCmdSeq_increments_and_wraps_to_one()
    {
        Assert.Equal(1, Slv3Protocol.NextCmdSeq(0));
        Assert.Equal(81, Slv3Protocol.NextCmdSeq(80));
        Assert.Equal(1, Slv3Protocol.NextCmdSeq(254));
        Assert.Equal(1, Slv3Protocol.NextCmdSeq(255));
    }

    [Fact]
    public void BuildUnbind_clears_master_target_and_slot_but_keeps_the_pwm_tuple()
    {
        byte[] pwm = { 12, 12, 12, 12 };
        var payload = Slv3Protocol.BuildUnbind(FanMac, targetChannel: 8, pwm);

        Assert.Equal(0x10, payload[1]);
        Assert.Equal(FanMac, payload.AsSpan(2, 6).ToArray());
        Assert.Equal(new byte[6], payload.AsSpan(8, 6).ToArray());   // master all-zero
        Assert.Equal(0, payload[14]);
        Assert.Equal(8, payload[15]);
        Assert.Equal(0, payload[16]);
        Assert.Equal(pwm, payload.AsSpan(17, 4).ToArray());
    }

    [Fact]
    public void BuildSequencedCommand_lays_out_target_channel_zero_slot_and_seq()
    {
        var payload = Slv3Protocol.BuildSequencedCommand(Slv3Protocol.RfSelect, FanMac, MasterMac, targetRx: 1, targetChannel: 8, cmdSeq: 0x50);

        Assert.Equal(0x12, payload[0]);
        Assert.Equal(Slv3Protocol.RfSelect, payload[1]);
        Assert.Equal(FanMac, payload.AsSpan(2, 6).ToArray());
        Assert.Equal(MasterMac, payload.AsSpan(8, 6).ToArray());
        Assert.Equal(new byte[] { 0x01, 0x08, 0x00, 0x50 }, payload.AsSpan(14, 4).ToArray());
        Assert.All(payload.AsSpan(18).ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void BuildClockSync_matches_the_lconnect_heartbeat_layout()
    {
        // Y70 USBPcap 2026-09-04: [14..45] = 0x14 x32, then year/month/day/h/m/s, then 0x14 x11.
        var payload = Slv3Protocol.BuildClockSync(MasterMac, new DateTime(2026, 9, 4, 12, 44, 3));

        Assert.Equal(0x12, payload[0]);
        Assert.Equal(Slv3Protocol.RfClockSync, payload[1]);
        Assert.Equal(new byte[6], payload.AsSpan(2, 6).ToArray());   // fan MAC zero, not broadcast
        Assert.Equal(MasterMac, payload.AsSpan(8, 6).ToArray());
        Assert.All(payload.AsSpan(14, 32).ToArray(), b => Assert.Equal(0x14, b));
        Assert.Equal(new byte[] { 0x07, 0xEA, 0x09, 0x04, 0x0C, 0x2C, 0x03 }, payload.AsSpan(46, 7).ToArray());
        Assert.All(payload.AsSpan(53, 11).ToArray(), b => Assert.Equal(0x14, b));
        Assert.All(payload.AsSpan(64).ToArray(), b => Assert.Equal(0, b));
    }

    // ── FloorDuty / ResolvePortDuty / BuildPwmTuple (Phase 3) ──

    [Fact]
    public void FloorDuty_leaves_zero_alone_but_floors_low_nonzero_values()
    {
        Assert.Equal(0, Slv3Protocol.FloorDuty(0));
        Assert.Equal(14, Slv3Protocol.FloorDuty(1));
        Assert.Equal(14, Slv3Protocol.FloorDuty(13));
        Assert.Equal(14, Slv3Protocol.FloorDuty(14));
        Assert.Equal(50, Slv3Protocol.FloorDuty(50));
        Assert.Equal(100, Slv3Protocol.FloorDuty(150));
        Assert.Equal(0, Slv3Protocol.FloorDuty(-5));
    }

    [Fact]
    public void ResolvePortDuty_null_is_mobo_sync_sentinel()
    {
        Assert.Equal(Slv3Protocol.PwmFollowMotherboard, Slv3Protocol.ResolvePortDuty(null));
    }

    [Fact]
    public void ResolvePortDuty_floors_then_encodes_a_manual_percent()
    {
        // 6 % is within the floor band (0,14) so it floors to 14 % = wire 35.
        Assert.Equal(35, Slv3Protocol.ResolvePortDuty(6));
        Assert.Equal(0, Slv3Protocol.ResolvePortDuty(0));
        Assert.Equal(191, Slv3Protocol.ResolvePortDuty(75));
    }

    [Fact]
    public void BuildPwmTuple_defaults_to_mobo_sync_and_mirrors_it_onto_unoccupied_ports()
    {
        var pwm = Slv3Protocol.BuildPwmTuple(new int?[] { null, null, null, null }, fanCount: 2);

        // L-Connect writes one uniform tuple; an unused port repeats its neighbour.
        Assert.Equal(new byte[] { 6, 6, 6, 6 }, pwm);
    }

    [Fact]
    public void BuildPwmTuple_zero_fan_chain_honors_manual_targets_and_mobo_syncs_the_rest()
    {
        // A controller that does not enumerate its fans reports fanCount 0 while
        // still accepting PWM; a manual target must reach the wire (so the user
        // can drive it) and unset ports follow the motherboard rather than being
        // commanded off.
        var pwm = Slv3Protocol.BuildPwmTuple(new int?[] { 40, null, null, null }, fanCount: 0);

        Assert.Equal(102, pwm[0]);
        Assert.Equal(Slv3Protocol.PwmFollowMotherboard, pwm[1]);
        Assert.Equal(Slv3Protocol.PwmFollowMotherboard, pwm[2]);
        Assert.Equal(Slv3Protocol.PwmFollowMotherboard, pwm[3]);
    }

    [Fact]
    public void BuildPwmTuple_encodes_manual_targets_for_occupied_ports_only()
    {
        var targets = new int?[] { 50, null, 5, 100 };
        var pwm = Slv3Protocol.BuildPwmTuple(targets, fanCount: 3);

        Assert.Equal(127, pwm[0]);
        Assert.Equal(Slv3Protocol.PwmFollowMotherboard, pwm[1]);
        Assert.Equal(35, pwm[2]);  // 5 floored to the SLV3 minimum of 14 %
        Assert.Equal(35, pwm[3]);  // port 3 is beyond fanCount=3: mirrors port 2, its own target is ignored
    }

    [Fact]
    public void BuildPwmTuple_treats_a_short_targets_list_as_mobo_sync()
    {
        var pwm = Slv3Protocol.BuildPwmTuple(new int?[] { 40 }, fanCount: 3);

        Assert.Equal(102, pwm[0]);
        Assert.Equal(Slv3Protocol.PwmFollowMotherboard, pwm[1]);
        Assert.Equal(Slv3Protocol.PwmFollowMotherboard, pwm[2]);
        Assert.Equal(Slv3Protocol.PwmFollowMotherboard, pwm[3]);
    }

    // ── SaveCfg / ResetAnother / video-mode frames (link-health) ──

    [Fact]
    public void BuildSaveCfg_broadcasts_to_ff_and_carries_the_master_mac()
    {
        var payload = Slv3Protocol.BuildSaveCfg(MasterMac);

        Assert.Equal(Slv3Protocol.RfPayloadSize, payload.Length);
        Assert.Equal(0x12, payload[0]);
        Assert.Equal(0x15, payload[1]);            // RF_SaveCfg
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, payload.AsSpan(2, 6).ToArray());
        Assert.Equal(MasterMac, payload.AsSpan(8, 6).ToArray());
        Assert.Equal(0xFF, payload[14]);
        Assert.Equal(0, payload[15]);               // rest of the payload is zero
    }

    [Fact]
    public void BuildResetAnother_emits_the_reset_command_with_no_body()
    {
        var frame = Slv3Protocol.BuildResetAnother();

        Assert.Equal(64, frame.Length);
        Assert.Equal(0x15, frame[0]);
        for (var i = 1; i < frame.Length; i++)
        {
            Assert.Equal(0, frame[i]);
        }
    }

    [Fact]
    public void BuildVideoStart_emits_getmac_cmd_with_the_start_flag()
    {
        var frame = Slv3Protocol.BuildVideoStart();
        Assert.Equal(0x11, frame[0]);
        Assert.Equal(0x01, frame[1]);
    }

    [Fact]
    public void BuildVideoPrep_lays_out_device_index_channel_and_ff()
    {
        var frame = Slv3Protocol.BuildVideoPrep(deviceIndex: 2, channel: 8);
        Assert.Equal(0x10, frame[0]);
        Assert.Equal(2, frame[1]);
        Assert.Equal(8, frame[2]);
        Assert.Equal(0xFF, frame[3]);
    }

    // ── ParseGetDevMoboDuty ──

    [Fact]
    public void ParseGetDevMoboDuty_returns_null_when_the_unavailable_bit_is_set()
    {
        var reply = new byte[4];
        reply[0] = Slv3Protocol.UsbSendRf;
        reply[2] = 0x80; // unavailable bit
        reply[3] = 0;
        Assert.Null(Slv3Protocol.ParseGetDevMoboDuty(reply));
    }

    [Fact]
    public void ParseGetDevMoboDuty_returns_null_when_on_and_off_are_both_zero()
    {
        var reply = new byte[4];
        reply[0] = Slv3Protocol.UsbSendRf;
        reply[2] = 0;
        reply[3] = 0;
        Assert.Null(Slv3Protocol.ParseGetDevMoboDuty(reply));
    }

    [Fact]
    public void ParseGetDevMoboDuty_computes_percent_from_on_and_off_times()
    {
        var reply = new byte[4];
        reply[0] = Slv3Protocol.UsbSendRf;
        reply[2] = 63; // off
        reply[3] = 63; // on
        Assert.Equal(50, Slv3Protocol.ParseGetDevMoboDuty(reply));

        reply[2] = 0;
        reply[3] = 255;
        Assert.Equal(100, Slv3Protocol.ParseGetDevMoboDuty(reply));
    }

    // ── ChannelScanOrder ──

    [Fact]
    public void ChannelScanOrder_starts_at_default_and_covers_1_through_39_exactly_once()
    {
        var order = Slv3Protocol.ChannelScanOrder();

        Assert.Equal(Slv3Protocol.DefaultChannel, order[0]);
        Assert.Equal(39, order.Length);
        Assert.Equal(39, new HashSet<byte>(order).Count);
        for (byte ch = 1; ch <= 39; ch++)
        {
            Assert.Contains(ch, order);
        }
    }

    // ── ClassifyFanFamily / LedsPerFanFor / MinDutyPercentFor / FloorDuty ──

    [Theory]
    [InlineData(19, Slv3FanFamily.Unknown)]
    [InlineData(20, Slv3FanFamily.Slv3Led)]
    [InlineData(23, Slv3FanFamily.Slv3Led)]
    [InlineData(24, Slv3FanFamily.Slv3Lcd)]
    [InlineData(26, Slv3FanFamily.Slv3Lcd)]
    [InlineData(27, Slv3FanFamily.Tlv2Lcd)]
    [InlineData(28, Slv3FanFamily.Tlv2Led)]
    [InlineData(31, Slv3FanFamily.Tlv2Led)]
    [InlineData(32, Slv3FanFamily.Tlv2Lcd)]
    [InlineData(35, Slv3FanFamily.Tlv2Lcd)]
    [InlineData(36, Slv3FanFamily.SlInf)]
    [InlineData(39, Slv3FanFamily.SlInf)]
    [InlineData(40, Slv3FanFamily.Cl)]
    [InlineData(42, Slv3FanFamily.Cl)]
    [InlineData(43, Slv3FanFamily.Unknown)]
    public void ClassifyFanFamily_matches_the_documented_ranges(byte fansTypeByte, Slv3FanFamily expected)
    {
        Assert.Equal(expected, Slv3Protocol.ClassifyFanFamily(fansTypeByte));
    }

    [Theory]
    [InlineData(Slv3FanFamily.Tlv2Lcd, 26)]
    [InlineData(Slv3FanFamily.Tlv2Led, 26)]
    [InlineData(Slv3FanFamily.SlInf, 44)]
    [InlineData(Slv3FanFamily.Cl, 24)]
    [InlineData(Slv3FanFamily.Slv3Led, 40)]
    [InlineData(Slv3FanFamily.Slv3Lcd, 40)]
    [InlineData(Slv3FanFamily.Unknown, 40)]
    public void LedsPerFanFor_matches_family(Slv3FanFamily family, int expected)
    {
        Assert.Equal(expected, Slv3Protocol.LedsPerFanFor(family));
    }

    [Theory]
    [InlineData(Slv3FanFamily.Tlv2Lcd, 10)]
    [InlineData(Slv3FanFamily.Cl, 10)]
    [InlineData(Slv3FanFamily.Tlv2Led, 11)]
    [InlineData(Slv3FanFamily.SlInf, 11)]
    [InlineData(Slv3FanFamily.Slv3Led, 14)]
    [InlineData(Slv3FanFamily.Slv3Lcd, 14)]
    [InlineData(Slv3FanFamily.Unknown, 14)]
    public void MinDutyPercentFor_matches_family(Slv3FanFamily family, int expected)
    {
        Assert.Equal(expected, Slv3Protocol.MinDutyPercentFor(family));
    }

    [Fact]
    public void FloorDuty_uses_the_family_minimum_but_leaves_zero_alone()
    {
        Assert.Equal(11, Slv3Protocol.FloorDuty(5, Slv3FanFamily.SlInf));
        Assert.Equal(14, Slv3Protocol.FloorDuty(5, Slv3FanFamily.Slv3Lcd));
        Assert.Equal(0, Slv3Protocol.FloorDuty(0, Slv3FanFamily.SlInf));
        Assert.Equal(0, Slv3Protocol.FloorDuty(0, Slv3FanFamily.Cl));
    }

    // ── Device record parsing ──

    [Fact]
    public void TryParseRecord_decodes_mac_type_rpm_pwm()
    {
        var reply = new byte[Slv3Protocol.RecordHeaderLength + Slv3Protocol.RecordLength];
        reply[0] = 0x10; reply[1] = 1;
        var rec = reply.AsSpan(Slv3Protocol.RecordHeaderLength);
        FanMac.CopyTo(rec.Slice(0));
        MasterMac.CopyTo(rec.Slice(6));
        rec[12] = 8;    // channel
        rec[13] = 3;    // rxType
        rec[18] = 0;    // dev_type: a wireless fan chain reports 0 (real Y70 value)
        rec[19] = 3;    // fan_num
        rec[24] = 0x18; rec[25] = 0x18; rec[26] = 0x18; // fans_type: 3x SLV3-LCD (24)
        // fans_speed @28: port0 RPM 0x0ABC (hi nibble masked), flags in the top nibble.
        rec[28] = 0xFA; rec[29] = 0xBC;   // -> ((0x0A)<<8)|0xBC = 0x0ABC = 2748
        rec[36] = 55;   // pwm port0
        rec[41] = 0x1C; // validator

        Assert.True(Slv3Protocol.TryParseRecord(reply, Slv3Protocol.RecordHeaderLength, out var record));
        Assert.Equal(FanMac, record.Mac);
        Assert.Equal(MasterMac, record.MasterMac);
        Assert.Equal(0, record.DevType);
        Assert.True(record.IsWirelessFan);     // non-master => a fan, regardless of dev_type 0
        Assert.False(record.IsMaster);
        Assert.Equal(24, record.EffectiveFanType); // SLV3-LCD from fans_type
        Assert.Equal(3, record.FanCount);
        Assert.Equal(0x0ABC, record.Rpm[0]);   // hi nibble flags masked off
        Assert.Equal(55, record.Pwm[0]);
    }

    [Fact]
    public void TryParseRecord_treats_devtype_FF_as_master_not_fan()
    {
        var reply = new byte[Slv3Protocol.RecordHeaderLength + Slv3Protocol.RecordLength];
        var rec = reply.AsSpan(Slv3Protocol.RecordHeaderLength);
        rec[18] = 0xFF; // the dongle's own record
        rec[41] = 0x1C;
        Assert.True(Slv3Protocol.TryParseRecord(reply, Slv3Protocol.RecordHeaderLength, out var record));
        Assert.True(record.IsMaster);
        Assert.False(record.IsWirelessFan);
    }

    [Fact]
    public void TryParseRecord_rejects_bad_validator()
    {
        var reply = new byte[Slv3Protocol.RecordHeaderLength + Slv3Protocol.RecordLength];
        reply[Slv3Protocol.RecordHeaderLength + 41] = 0x00; // not 0x1C
        Assert.False(Slv3Protocol.TryParseRecord(reply, Slv3Protocol.RecordHeaderLength, out _));
    }

    [Fact]
    public void TryParseRecord_forces_full_duty_when_spinning_but_zero_pwm()
    {
        var reply = new byte[Slv3Protocol.RecordHeaderLength + Slv3Protocol.RecordLength];
        var rec = reply.AsSpan(Slv3Protocol.RecordHeaderLength);
        rec[18] = 20;
        rec[28] = 0x05; rec[29] = 0x00; // port0 spinning (RPM 0x500)
        // all pwm bytes left 0
        rec[41] = 0x1C;
        Assert.True(Slv3Protocol.TryParseRecord(reply, Slv3Protocol.RecordHeaderLength, out var record));
        Assert.Equal(100, record.Pwm[0]);
    }

    [Fact]
    public void TryParseRecord_surfaces_family_from_the_first_nonzero_fans_type_byte()
    {
        var reply = new byte[Slv3Protocol.RecordHeaderLength + Slv3Protocol.RecordLength];
        var rec = reply.AsSpan(Slv3Protocol.RecordHeaderLength);
        rec[24] = 37; // SL-Infinity
        rec[41] = Slv3Protocol.RecordValidator;

        Assert.True(Slv3Protocol.TryParseRecord(reply, Slv3Protocol.RecordHeaderLength, out var record));
        Assert.Equal(Slv3FanFamily.SlInf, record.Family);
    }

    [Fact]
    public void TryParseRecord_family_is_unknown_when_fans_type_is_all_zero()
    {
        var reply = new byte[Slv3Protocol.RecordHeaderLength + Slv3Protocol.RecordLength];
        reply[Slv3Protocol.RecordHeaderLength + 41] = Slv3Protocol.RecordValidator;

        Assert.True(Slv3Protocol.TryParseRecord(reply, Slv3Protocol.RecordHeaderLength, out var record));
        Assert.Equal(Slv3FanFamily.Unknown, record.Family);
    }

    [Fact]
    public void MacEquals_and_MacIsZero()
    {
        Assert.True(Slv3Protocol.MacEquals(FanMac, FanMac));
        Assert.False(Slv3Protocol.MacEquals(FanMac, MasterMac));
        Assert.True(Slv3Protocol.MacIsZero(new byte[6]));
        Assert.False(Slv3Protocol.MacIsZero(FanMac));
    }
}
