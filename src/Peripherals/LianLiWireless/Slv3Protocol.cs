using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// Pure byte-level builders/parsers for the Lian Li L-Wireless (SLV3) 2.4 GHz
/// dongle protocol. No IO; the transport owns the WinUSB endpoints. Facts are
/// from L-Connect 3 (lianli.slv3) and the public RE (sgtaziz/lian-li-linux,
/// phstudy/uni-wireless-sync).
///
/// Two framing layers:
///  - USB frame (64 B, to the TX/RX dongle over WinUSB EP 0x01, no report id):
///    [0]=USB_CMD [1]=arg/chunkSeq [2]=channel [3]=rxType [4..63]=60 B payload chunk.
///  - RF payload (240 B, what the fan firmware parses), fragmented into 4x60 B
///    chunks by Usb_SendRf: [0]=0x12 frameType [1]=RF_CMD [2..7]=fan MAC
///    [8..13]=master MAC [14]=targetRx [15]=targetChannel [16]=slot [17]=cmdSeq [18..]=body.
/// </summary>
public static class Slv3Protocol
{
    // WinUSB dongle ids. L-Connect matches either the Nuvoton pair or the WCH
    // alias for the same physical controller.
    public const int TxVendorId = 0x0416;
    public const int TxProductId = 0x8040;
    public const int RxVendorId = 0x0416;
    public const int RxProductId = 0x8041;
    public const int WchVendorId = 0x1A86;
    public const int TxProductIdWch = 0xE304;
    public const int RxProductIdWch = 0xE305;

    /// <summary>SetupDiGetClassDevs interface GUID for the TX/RX dongles (Y70 registry).</summary>
    public const string DongleInterfaceGuid = "{1D4B2365-4749-48EA-B38A-7C6FDDDD7E26}";

    // WinUSB endpoints (same on TX, RX, and each LCD): interrupt, 64-byte packets.
    public const byte WritePipeId = 0x01;
    public const byte ReadPipeId = 0x81;
    public const int UsbPacketSize = 64;
    public const int RfPayloadSize = 240;
    public const int RfChunkSize = 60;

    public const int MacLength = 6;

    // USB_CMD (first byte of a USB frame to a dongle).
    public const byte UsbSendRf = 0x10;   // transmit an RF payload via the TX; also GetDev on the RX
    public const byte UsbGetMac = 0x11;   // query own MAC + clock + fw (TX)
    public const byte UsbResetAnother = 0x15;

    // RF_CMD (payload byte 1). Values per the decompiled RFCmdType numbering.
    public const byte RfBind = 0x10;               // bind/unbind + carries the 4-byte PWM tuple
    public const byte RfSelect = 0x12;             // identify a fan
    public const byte RfClockSync = 0x14;          // master-clock heartbeat (broadcast)
    public const byte RfSaveCfg = 0x15;            // persist to fan flash
    public const byte RfRebootChain = 0x16;        // RebootLcd: soft-reboot the chain controller
    public const byte RfRgbSync = 0x20;            // streamed RGB frame animation
    public const byte RfMbSyncSwitch = 0x24;
    public const byte RfLightSyncSwitch = 0x26;

    /// <summary>Constant frame-type byte at RF payload[0] for every host->fan frame.</summary>
    public const byte RfFrameType = 0x12;

    /// <summary>Default RF channel; user channels are odd (firmware rejects even).</summary>
    public const byte DefaultChannel = 8;

    /// <summary>rx_type slot ids a master hands out to bound fans (L-Connect GetRxUnused allocates 1..13).</summary>
    public const int MinSlot = 1;
    public const int MaxSlot = 13;

    // Device-list record (42 bytes, from the RX GetDev reply).
    public const int RecordLength = 42;
    public const int RecordHeaderLength = 4;   // [0]=cmd echo [1]=count [2..3]=ver/flags
    public const int PageLength = 434;
    public const int RecordsPerPage = (PageLength - RecordHeaderLength) / RecordLength;
    public const byte RecordValidator = 0x1C;  // record[41] must equal this

    /// <summary>PWM byte meaning "follow motherboard PWM header". Real duties skip 6.</summary>
    public const byte PwmFollowMotherboard = 6;
    public const int PortsPerRecord = 4;

    /// <summary>
    /// Bind-frame duty bytes are a 0..255 scale, not a percent: L-Connect maps
    /// its 0..100 duty through Map(0,100,0,255) before the RF layer, and the
    /// chain echoes the exact byte back in fans_pwm (Y70 USBPcap 2026-09-04:
    /// 50% -> 127 -> 996 rpm, 100% -> 255 -> 1919 rpm, 25% -> 63 -> 541 rpm).
    /// </summary>
    public const int PwmScaleMax = 255;

    /// <summary>
    /// A bind/PWM frame is re-sent while any port's reported duty byte differs
    /// from its target by more than this (L-Connect NeedSyncPwm).
    /// </summary>
    public const int PwmDriftThreshold = 5;

    public static readonly byte[] ZeroMac = new byte[MacLength];

    /// <summary>
    /// Filler byte L-Connect writes across the 50-byte "cpuInfoParam" block of
    /// every RF_ClockSync when no LCD theme data is configured (InitSensorDataByWiredLess).
    /// </summary>
    public const byte ClockSyncFillByte = 0x14;

    // dev_type ranges that identify our wireless LCD fans in a device record.
    public const byte DevTypeSlv3Fan = 20;      // 20-23 SLV3 LED, 24-26 SLV3 LCD
    public const byte DevTypeSlInfinity = 36;   // 36-39 SL-Infinity

    /// <summary>
    /// Fan family from a fans_type byte (lian-li-linux fan_type.rs ranges):
    /// SLV3-LED 20-23, SLV3-LCD 24-26, TLV2-LCD 27 and 32-35, TLV2-LED 28-31,
    /// SL-INF wireless 36-39, CL/RL120 40-42.
    /// </summary>
    public static Slv3FanFamily ClassifyFanFamily(byte fansTypeByte) => fansTypeByte switch
    {
        >= 20 and <= 23 => Slv3FanFamily.Slv3Led,
        >= 24 and <= 26 => Slv3FanFamily.Slv3Lcd,
        27 or (>= 32 and <= 35) => Slv3FanFamily.Tlv2Lcd,
        >= 28 and <= 31 => Slv3FanFamily.Tlv2Led,
        >= 36 and <= 39 => Slv3FanFamily.SlInf,
        >= 40 and <= 42 => Slv3FanFamily.Cl,
        _ => Slv3FanFamily.Unknown,
    };

    /// <summary>
    /// A record in this dev_type range is a Strimer Wireless cable, not a fan
    /// chain (L-Connect RfDevice: RecType = Strimer). It reports fan_num 0 and
    /// all-zero fans_type, and takes RGB only.
    /// </summary>
    public static bool IsStrimerDevType(byte devType) => devType >= 1 && devType <= 9;

    /// <summary>
    /// Strimer Wireless lane geometry by dev_type, lanes back to back in the
    /// RGB buffer (L-Connect RfDevice.LedNum and the RgbEffect.StrimerMode lane
    /// loops; totals match lian-li.com). A Strimer dev_type without a table
    /// row is (0, 0).
    /// </summary>
    public static (int Lanes, int LedsPerLane) StrimerGeometryFor(byte devType) => devType switch
    {
        1 => (4, 29),
        2 => (6, 22),
        3 => (6, 29),
        4 => (4, 22),
        _ => (0, 0),
    };

    /// <summary>Wire LED count per physical fan (lian-li-linux leds_per_fan). Unknown keeps the bench-verified SLV3 value.</summary>
    public static int LedsPerFanFor(Slv3FanFamily family) => family switch
    {
        Slv3FanFamily.Tlv2Lcd or Slv3FanFamily.Tlv2Led => 26,
        Slv3FanFamily.SlInf => 44,
        Slv3FanFamily.Cl => 24,
        _ => 40,
    };

    /// <summary>Minimum non-zero duty percent per family; lower requests stall the fan (lian-li-linux min_duty_percent). Unknown keeps the SLV3 floor.</summary>
    public static int MinDutyPercentFor(Slv3FanFamily family) => family switch
    {
        Slv3FanFamily.Tlv2Lcd or Slv3FanFamily.Cl => 10,
        Slv3FanFamily.Tlv2Led or Slv3FanFamily.SlInf => 11,
        _ => MinDutyPercent,
    };

    /// <summary>
    /// Fragment a 240-byte RF payload into <see cref="UsbPacketSize"/>-byte USB
    /// frames for the TX: [0]=UsbSendRf [1]=chunkSeq [2]=channel [3]=rxType
    /// [4..]=60 payload bytes. Returns ceil(len/60) frames (4 for a 240 B payload).
    /// </summary>
    public static byte[][] BuildUsbSendRf(byte channel, byte rxType, ReadOnlySpan<byte> rfPayload)
    {
        var frames = (rfPayload.Length + RfChunkSize - 1) / RfChunkSize;
        var result = new byte[frames][];
        for (var i = 0; i < frames; i++)
        {
            var frame = new byte[UsbPacketSize];
            frame[0] = UsbSendRf;
            frame[1] = (byte)i;
            frame[2] = channel;
            frame[3] = rxType;
            var offset = i * RfChunkSize;
            var count = Math.Min(RfChunkSize, rfPayload.Length - offset);
            rfPayload.Slice(offset, count).CopyTo(frame.AsSpan(4));
            result[i] = frame;
        }
        return result;
    }

    /// <summary>USB frame that asks the TX for the master MAC/clock/fw: [0]=0x11 [1]=channel.</summary>
    public static byte[] BuildGetMac(byte channel)
    {
        var frame = new byte[UsbPacketSize];
        frame[0] = UsbGetMac;
        frame[1] = channel;
        return frame;
    }

    /// <summary>USB frame that asks the RX for <paramref name="pageCount"/> device-list pages: [0]=0x10 [1]=pageCount.</summary>
    public static byte[] BuildGetDev(byte pageCount)
    {
        var frame = new byte[UsbPacketSize];
        frame[0] = UsbSendRf;
        frame[1] = pageCount;
        return frame;
    }

    /// <summary>
    /// USB frame that resets the RX dongle's RF MCU: [0]=0x15. The reference
    /// (lian-li-linux controller.rs) issues it after 5 consecutive GetDev
    /// failures; a USB handle reopen alone does not reset a wedged radio.
    /// </summary>
    public static byte[] BuildResetAnother()
    {
        var frame = new byte[UsbPacketSize];
        frame[0] = UsbResetAnother;
        return frame;
    }

    /// <summary>TX video-start frame [0]=0x11 [1]=0x01 (lian-li-linux CMD_VIDEO_START); precedes wireless-LCD streaming.</summary>
    public static byte[] BuildVideoStart()
    {
        var frame = new byte[UsbPacketSize];
        frame[0] = UsbGetMac;
        frame[1] = 0x01;
        return frame;
    }

    /// <summary>Per-device video prep frame [0]=0x10 [1]=deviceIndex [2]=channel [3]=0xFF, no payload (lian-li-linux ensure_video_mode).</summary>
    public static byte[] BuildVideoPrep(byte deviceIndex, byte channel)
    {
        var frame = new byte[UsbPacketSize];
        frame[0] = UsbSendRf;
        frame[1] = deviceIndex;
        frame[2] = channel;
        frame[3] = 0xFF;
        return frame;
    }

    /// <summary>
    /// RF_SaveCfg (0x15) payload: broadcast fan MAC (FF x6), our master MAC,
    /// targetRx 0xFF, remaining header/body zero (lian-li-linux bind.rs
    /// save_rf_config). Persists the fans' current binding to flash so it
    /// survives a power cycle; without it a bind lives only in RAM.
    /// </summary>
    public static byte[] BuildSaveCfg(ReadOnlySpan<byte> masterMac)
    {
        var payload = new byte[RfPayloadSize];
        payload[0] = RfFrameType;
        payload[1] = RfSaveCfg;
        payload.AsSpan(2, MacLength).Fill(0xFF);
        masterMac.Slice(0, MacLength).CopyTo(payload.AsSpan(8));
        payload[14] = 0xFF;
        return payload;
    }

    /// <summary>
    /// Motherboard PWM duty sensed by the RX, from the GetDev reply header:
    /// [2] bit7 = unavailable, else off-time = [2] &amp; 0x7F and on-time = [3];
    /// duty = on / (on + off). Returns a percent 0..100, or null when the RX
    /// reports it unavailable (or on + off == 0).
    /// </summary>
    public static int? ParseGetDevMoboDuty(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < RecordHeaderLength || reply[0] != UsbSendRf)
        {
            return null;
        }
        var indicator = reply[2];
        if ((indicator & 0x80) != 0)
        {
            return null;
        }
        int off = indicator & 0x7F;
        int on = reply[3];
        if (on + off == 0)
        {
            return null;
        }
        return Math.Clamp((int)Math.Round(100.0 * on / (on + off)), 0, 100);
    }

    /// <summary>
    /// GetMac probe order when the dongle is not on <see cref="DefaultChannel"/>:
    /// 8 first, then even channels, then odd (lian-li-linux discover_master_mac).
    /// A dongle left on another channel by L-Connect answers only there.
    /// </summary>
    public static byte[] ChannelScanOrder()
    {
        var order = new List<byte> { DefaultChannel };
        for (byte ch = 2; ch <= 38; ch += 2)
        {
            if (ch != DefaultChannel)
            {
                order.Add(ch);
            }
        }
        for (byte ch = 1; ch <= 39; ch += 2)
        {
            order.Add(ch);
        }
        return order.ToArray();
    }

    /// <summary>
    /// Parse the GetMac reply: [0]=0x11 echo, [1..6]=master MAC, [7..10]=RF timer
    /// (BE u32), [11..12]=TX fw version (BE). Returns false if the echo is wrong.
    /// </summary>
    public static bool TryParseGetMac(ReadOnlySpan<byte> reply, out byte[] masterMac, out uint rfTimer, out int fwVersion)
    {
        masterMac = Array.Empty<byte>();
        rfTimer = 0;
        fwVersion = 0;
        if (reply.Length < 13 || reply[0] != UsbGetMac) return false;
        masterMac = reply.Slice(1, MacLength).ToArray();
        rfTimer = (uint)((reply[7] << 24) | (reply[8] << 16) | (reply[9] << 8) | reply[10]);
        fwVersion = (reply[11] << 8) | reply[12];
        return true;
    }

    /// <summary>
    /// Common RF payload header shared by every host->fan frame. Writes bytes
    /// 0..17; the caller fills the command body from [18]. <paramref name="dst"/>
    /// must be at least <see cref="RfPayloadSize"/> bytes and is not cleared.
    /// </summary>
    public static void WriteRfHeader(
        Span<byte> dst, byte rfCmd, ReadOnlySpan<byte> fanMac, ReadOnlySpan<byte> masterMac,
        byte targetRx, byte targetChannel, byte slot, byte cmdSeq)
    {
        dst[0] = RfFrameType;
        dst[1] = rfCmd;
        fanMac.Slice(0, MacLength).CopyTo(dst.Slice(2));
        masterMac.Slice(0, MacLength).CopyTo(dst.Slice(8));
        dst[14] = targetRx;
        dst[15] = targetChannel;
        dst[16] = slot;
        dst[17] = cmdSeq;
    }

    /// <summary>
    /// Build the RF_Bind (0x10) payload, which also carries the PWM tuple:
    /// [17..20] are four duty bytes on the 0..255 scale
    /// (<see cref="PwmFollowMotherboard"/> = mobo sync), one per port, as
    /// <see cref="BuildPwmTuple"/> lays them out. <paramref name="slot"/> is the
    /// chain's ordinal among bound chains; a release is <see cref="BuildUnbind"/>.
    /// </summary>
    public static byte[] BuildBind(
        ReadOnlySpan<byte> fanMac, ReadOnlySpan<byte> masterMac,
        byte targetRx, byte targetChannel, byte slot, ReadOnlySpan<byte> pwm4)
    {
        var payload = new byte[RfPayloadSize];
        // Bind puts the PWM tuple where the generic header's [17] cmd_seq would be,
        // so write the header then overwrite [17..20] with the duties.
        WriteRfHeader(payload, RfBind, fanMac, masterMac, targetRx, targetChannel, slot, 0);
        for (var i = 0; i < PortsPerRecord && i < pwm4.Length; i++)
        {
            payload[17 + i] = pwm4[i];
        }
        return payload;
    }

    /// <summary>
    /// Encode a duty percent (0..100) to a bind-frame byte on the firmware's
    /// 0..255 scale (<see cref="PwmScaleMax"/>), truncating like L-Connect's
    /// (int)Map(duty, 0, 100, 0, 255) (50 -> 127, 25 -> 63). A result of 6 is
    /// remapped to 0 so it is never read as the mobo-sync sentinel (SetFansRPM).
    /// </summary>
    public static byte EncodeDuty(int percent)
    {
        var d = Math.Clamp(percent, 0, 100);
        var wire = (int)(d * (double)PwmScaleMax / 100.0);
        return wire == PwmFollowMotherboard ? (byte)0 : (byte)wire;
    }

    /// <summary>Decode a reported fans_pwm byte (0..255) to a percent; the caller handles the mobo-sync sentinel.</summary>
    public static int DecodeDuty(int wire) =>
        Math.Clamp((int)Math.Round(Math.Clamp(wire, 0, PwmScaleMax) * 100.0 / PwmScaleMax), 0, 100);

    /// <summary>
    /// L-Connect's NeedSyncPwm: true when any port's reported duty byte is more
    /// than <see cref="PwmDriftThreshold"/> away from its target byte. The chain
    /// echoes a received tuple exactly, so this converges after one frame.
    /// </summary>
    public static bool NeedSyncPwm(ReadOnlySpan<int> reported, ReadOnlySpan<byte> target)
    {
        for (var i = 0; i < PortsPerRecord && i < reported.Length && i < target.Length; i++)
        {
            if (Math.Abs(reported[i] - target[i]) > PwmDriftThreshold)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Next per-device command sequence for RF_Select / RF_RebootLcd: increments and wraps to 1 past 254 (L-Connect targe_cmd_seq).</summary>
    public static byte NextCmdSeq(byte current) => current >= 254 ? (byte)1 : (byte)(current + 1);

    /// <summary>SLV3 minimum non-zero duty percent; lower requests would stall the fan.</summary>
    public const int MinDutyPercent = 14;

    /// <summary>Floors a nonzero duty percent up to the family's minimum; 0 (fully off) is left alone.</summary>
    public static int FloorDuty(int percent, Slv3FanFamily family = Slv3FanFamily.Slv3Lcd)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        var floor = MinDutyPercentFor(family);
        return clamped > 0 && clamped < floor ? floor : clamped;
    }

    /// <summary>
    /// Wire byte for one bind-frame duty port: null follows the motherboard
    /// PWM header (<see cref="PwmFollowMotherboard"/>); otherwise a manual
    /// percent, floored via <see cref="FloorDuty"/> then encoded via
    /// <see cref="EncodeDuty"/> so it can never collide with the mobo-sync
    /// sentinel.
    /// </summary>
    public static byte ResolvePortDuty(int? percent, Slv3FanFamily family = Slv3FanFamily.Slv3Lcd) =>
        percent is null ? PwmFollowMotherboard : EncodeDuty(FloorDuty(percent.Value, family));

    /// <summary>
    /// Builds the 4-port duty tuple for a bind frame from per-port targets. A
    /// manual target (non-null) is encoded on the 0..255 scale; a port with no
    /// target follows the motherboard PWM header. Ports at or beyond a non-zero
    /// <paramref name="fanCount"/> repeat the last occupied port's byte: L-Connect
    /// always writes one uniform tuple (SetFanSpeed repeats the duty x4), and a 0
    /// on an unused port is a value the firmware never sees from it. An unknown
    /// count (0) treats every port as in play; the hub only sends such a chain a
    /// tuple once a manual target exists for it.
    /// </summary>
    public static byte[] BuildPwmTuple(IReadOnlyList<int?> targets, int fanCount, Slv3FanFamily family = Slv3FanFamily.Slv3Lcd)
    {
        var pwm = new byte[PortsPerRecord];
        var occupied = fanCount <= 0 ? PortsPerRecord : Math.Min(fanCount, PortsPerRecord);
        for (var port = 0; port < PortsPerRecord; port++)
        {
            if (port < occupied)
            {
                var target = port < targets.Count ? targets[port] : null;
                pwm[port] = target is not null
                    ? EncodeDuty(FloorDuty(target.Value, family))
                    : PwmFollowMotherboard;
            }
            else
            {
                pwm[port] = pwm[occupied - 1];
            }
        }
        return pwm;
    }

    /// <summary>
    /// RF_Bind payload that releases a fan (L-Connect RfDevice.unBind): master
    /// MAC all-zero, targetRx 0, slot 0, the current duty tuple at [17..20].
    /// The chain clears its bound master in the next device-list report; a
    /// release that keeps our master MAC in the frame leaves it stale instead.
    /// </summary>
    public static byte[] BuildUnbind(ReadOnlySpan<byte> fanMac, byte targetChannel, ReadOnlySpan<byte> pwm4) =>
        BuildBind(fanMac, ZeroMac, targetRx: 0, targetChannel, slot: 0, pwm4);

    /// <summary>
    /// Sequenced control frame (RF_Select 0x12 identify, RF_RebootLcd 0x16):
    /// [14]=target rx, [15]=channel, [16]=0, [17]=cmdSeq. The chain echoes the
    /// last cmdSeq it processed in its record's byte [40]; the sender repeats the
    /// frame until that echo matches (L-Connect SyncControlInfo).
    /// </summary>
    public static byte[] BuildSequencedCommand(
        byte rfCmd, ReadOnlySpan<byte> fanMac, ReadOnlySpan<byte> masterMac, byte targetRx, byte targetChannel, byte cmdSeq)
    {
        var payload = new byte[RfPayloadSize];
        WriteRfHeader(payload, rfCmd, fanMac, masterMac, targetRx, targetChannel, slot: 0, cmdSeq);
        return payload;
    }

    /// <summary>
    /// RF_ClockSync (0x14) master heartbeat exactly as L-Connect's SyncMasterClock
    /// puts it on the air once a second: fan MAC all-zero (not broadcast), our
    /// master MAC, then the 50-byte cpuInfoParam block at [14..63]: 32 filler
    /// bytes, the wall clock (year BE16, month, day, hour, minute, second) at
    /// [46..52], 11 more filler bytes. Sent with USB rxType 0xFF.
    /// </summary>
    public static byte[] BuildClockSync(ReadOnlySpan<byte> masterMac, DateTime now)
    {
        var payload = new byte[RfPayloadSize];
        payload[0] = RfFrameType;
        payload[1] = RfClockSync;
        masterMac.Slice(0, MacLength).CopyTo(payload.AsSpan(8));
        payload.AsSpan(14, 50).Fill(ClockSyncFillByte);
        payload[46] = (byte)(now.Year >> 8);
        payload[47] = (byte)(now.Year & 0xFF);
        payload[48] = (byte)now.Month;
        payload[49] = (byte)now.Day;
        payload[50] = (byte)now.Hour;
        payload[51] = (byte)now.Minute;
        payload[52] = (byte)now.Second;
        return payload;
    }

    /// <summary>Number of valid records in a GetDev reply (first byte is the command echo, second is the count).</summary>
    public static int RecordCount(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 2 || reply[0] != UsbSendRf) return 0;
        return reply[1];
    }

    /// <summary>
    /// Parse one 42-byte device record at <paramref name="offset"/>. Returns false
    /// if out of range or the trailing validator byte is not 0x1C.
    /// </summary>
    public static bool TryParseRecord(ReadOnlySpan<byte> reply, int offset, out Slv3DeviceRecord record)
    {
        record = default;
        if (offset + RecordLength > reply.Length) return false;
        var rec = reply.Slice(offset, RecordLength);
        if (rec[41] != RecordValidator) return false;

        var mac = rec.Slice(0, MacLength).ToArray();
        var masterMac = rec.Slice(6, MacLength).ToArray();
        var channel = rec[12];
        var rxType = rec[13];
        var devType = rec[18];
        var fanNumRaw = rec[19];
        var rightAttach = fanNumRaw >= 10;
        var fanNum = rightAttach ? fanNumRaw - 10 : fanNumRaw;

        var effectIndex = new byte[4];
        rec.Slice(20, 4).CopyTo(effectIndex);

        // fans_type carries the per-port fan subtype (0x18=24 SLV3-LCD, 20-23
        // SLV3-LED, 36-39 SL-Infinity); dev_type at [18] is a coarse category and
        // reads 0 for a wireless fan chain, so the family lives here.
        var fansType = new byte[PortsPerRecord];
        rec.Slice(24, PortsPerRecord).CopyTo(fansType);

        var rpm = new int[PortsPerRecord];
        var pwm = new int[PortsPerRecord];
        var anyRpm = false;
        for (var k = 0; k < PortsPerRecord; k++)
        {
            // fans_speed is 8 bytes at [28]; the hi nibble of bytes 0/2/4/6 holds
            // flags and must be masked, leaving a big-endian 12-bit RPM per port.
            var hi = rec[28 + k * 2] & 0x0F;
            var lo = rec[28 + k * 2 + 1];
            rpm[k] = (hi << 8) | lo;
            if (rpm[k] > 0) anyRpm = true;
            pwm[k] = rec[36 + k];
        }
        // A spinning chain reporting all-zero fans_pwm reads as 100 (L-Connect
        // RefreshList's rule, on the same 0..255 scale as the rest of the bytes).
        if (anyRpm && pwm[0] == 0 && pwm[1] == 0 && pwm[2] == 0 && pwm[3] == 0)
        {
            for (var k = 0; k < PortsPerRecord; k++) pwm[k] = 100;
        }

        record = new Slv3DeviceRecord(mac, masterMac, channel, rxType, devType, fanNum, rightAttach, effectIndex, fansType, rpm, pwm, rec[40]);
        return true;
    }

    /// <summary>True when both MACs are equal over their first 6 bytes.</summary>
    public static bool MacEquals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length < MacLength || b.Length < MacLength) return false;
        for (var i = 0; i < MacLength; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    /// <summary>True when the MAC is all zeros (an unbound device reports a zero master MAC).</summary>
    public static bool MacIsZero(ReadOnlySpan<byte> mac)
    {
        for (var i = 0; i < MacLength && i < mac.Length; i++)
        {
            if (mac[i] != 0) return false;
        }
        return true;
    }
}

/// <summary>One parsed 42-byte device-list record from the RX dongle.</summary>
public readonly record struct Slv3DeviceRecord(
    byte[] Mac,
    byte[] MasterMac,
    byte Channel,
    byte RxType,
    byte DevType,
    int FanCount,
    bool RightAttach,
    byte[] EffectIndex,
    byte[] FansType,
    int[] Rpm,
    int[] Pwm,
    byte CmdSeq)
{
    /// <summary>
    /// Any non-master record on the RF link (L-Connect's rfList rule: dev_type 0xFF
    /// is a master, everything else is a device). A wireless fan chain reports
    /// dev_type 0 with the fan subtype in <see cref="FansType"/>.
    /// </summary>
    public bool IsWirelessFan => DevType != 0xFF;

    /// <summary>A record with dev_type 0xFF is another master on the link, not a fan.</summary>
    public bool IsMaster => DevType == 0xFF;

    /// <summary>A Strimer Wireless cable: no fan ports, RGB only.</summary>
    public bool IsStrimer => Slv3Protocol.IsStrimerDevType(DevType);

    /// <summary>
    /// First non-zero per-port fan subtype (0x18=24 SLV3-LCD, 20-23 SLV3-LED,
    /// 36-39 SL-Infinity); 0 when every port reads empty (starving beacon).
    /// Port 0 alone is not authoritative - it can be empty on a populated chain.
    /// </summary>
    public byte EffectiveFanType
    {
        get
        {
            foreach (var b in FansType)
            {
                if (b != 0)
                {
                    return b;
                }
            }
            return 0;
        }
    }

    /// <summary>Family from <see cref="EffectiveFanType"/>; all-zero fans_type classifies Unknown.</summary>
    public Slv3FanFamily Family => Slv3Protocol.ClassifyFanFamily(EffectiveFanType);
}

/// <summary>Wireless fan family, classified from a record's fans_type bytes.</summary>
public enum Slv3FanFamily
{
    Unknown,
    Slv3Led,
    Slv3Lcd,
    Tlv2Led,
    Tlv2Lcd,
    SlInf,
    Cl,
}
