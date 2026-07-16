using System;

namespace Nexus.Service.Peripherals.Aw5;

/// <summary>
/// Which ODM built the cooler's pump display. The two ship unrelated firmware and
/// share no report layout, transport, or encoding: treat them as separate protocols
/// that happen to sit behind one VID.
/// </summary>
public enum Aw5Variant
{
    Levelplay,
    CoolerMaster,
}

/// <summary>
/// Wire format for the AW5 pump display. Decoded on the bench 2026-07-15 by driving
/// each field and reading the glass; see plans/ibuypower-aw5-re.md.
/// </summary>
public static class Aw5Protocol
{
    public const int IbpVid = 0x3402;
    public const int LevelplayPid = 0x0406;
    public const int CoolerMasterPid = 0x0407;

    /// <summary>Both variants take a 64-byte report, report id included.</summary>
    public const int ReportLength = 64;

    /// <summary>
    /// Levelplay rides SET_REPORT(Feature) on the vendor-defined FF01 collection,
    /// not the interrupt OUT pipe: an output report on this collection is accepted
    /// by the HID stack and renders nothing.
    /// </summary>
    public const byte LevelplayReportId = 0x07;
    public const int LevelplayUsagePage = 0xFF01;

    /// <summary>CoolerMaster takes a plain interrupt-OUT output report.</summary>
    public const byte CoolerMasterReportId = 0x10;

    /// <summary>
    /// One sub-command per frame in byte 1; the panel wants all five per cycle. Only
    /// 0x00/0x01/0x03 carry rendered fields, but 0x02 and 0x04 are sent anyway to
    /// match the vendor stream, whose full cycle is the only shape observed to keep
    /// the panel in host mode.
    /// </summary>
    public const int LevelplaySubFrameCount = 5;

    /// <summary>
    /// Vendor cycle period. The panel reverts to its standalone reading a few seconds
    /// after frames stop, so this doubles as the keep-alive rate.
    /// </summary>
    public const int LevelplayCycleMs = 1050;

    /// <summary>Vendor spacing between the five frames of one cycle.</summary>
    public const int LevelplayInterFrameMs = 15;

    /// <summary>Vendor cadence. The panel accepts any rate; this one is known good.</summary>
    public const int CoolerMasterCycleMs = 2400;

    /// <summary>
    /// Bar-graph notches flanking each CoolerMaster reading. The host sends the count,
    /// so the scale is ours; the bar reads as roughly six segments on the glass.
    ///
    /// The vendor's own frames reach 10 for load and 7 for clock, and its counts fit
    /// 2 + 2*floor(load/15) exactly across 30 captured frames. Sending that was tried
    /// and reverted: it lights 4 bars at 15% load, which is plainly too full on a bar
    /// this size. So the vendor's bytes are not a plain segment count, and the capture
    /// cannot tell us what they are - only the glass can. Do not "correct" this scale
    /// to match the capture again without first counting lit segments on hardware.
    /// </summary>
    public const int CoolerMasterMaxNotches = 6;

    /// <summary>Panel clamps the frequency readout at four digits.</summary>
    private const int CoolerMasterMaxMhz = 9999;

    /// <summary>
    /// Writes <paramref name="value"/> as one decimal digit per byte, low nibble
    /// first byte = most significant. The panel ignores each byte's high nibble
    /// (bench: b3 = 0x12 / 0x52 / 0x92 all render "2"), so this is a digit map, not
    /// BCD - packing two digits per byte renders only half the number.
    /// Out-of-range values clamp rather than wrap, so a spike cannot render as a
    /// plausible small reading.
    /// </summary>
    internal static void WriteDigits(Span<byte> frame, int start, int value, int digits)
    {
        var max = 1;
        for (var i = 0; i < digits; i++) max *= 10;
        value = Math.Clamp(value, 0, max - 1);
        for (var i = digits - 1; i >= 0; i--)
        {
            frame[start + i] = (byte)(value % 10);
            value /= 10;
        }
    }

    /// <summary>
    /// Builds one Levelplay cycle: five 64-byte feature reports, in send order. The
    /// panel shows the same three readings as the CoolerMaster, laid out differently:
    /// <paramref name="tempC"/> large, then <paramref name="loadPct"/> and
    /// <paramref name="mhz"/> on the row beneath it.
    /// </summary>
    public static byte[][] BuildLevelplayCycle(int tempC, int loadPct, int mhz)
    {
        var frames = new byte[LevelplaySubFrameCount][];
        for (var i = 0; i < LevelplaySubFrameCount; i++)
        {
            frames[i] = new byte[ReportLength];
            frames[i][0] = LevelplayReportId;
            frames[i][1] = (byte)i;
        }

        // 0x00: the big two-digit reading, plus the load byte. The vendor sends load
        // as tens in the high nibble; kept so the stream matches even though no
        // element on this panel reads it - the rendered copy rides sub 0x01.
        WriteDigits(frames[0], 3, tempC, 2);
        frames[0][5] = (byte)((Math.Clamp(loadPct, 0, 99) / 10) << 4);

        // 0x01: the load percentage shown left of the rpm. Two digits, so 100% renders
        // as 99: the panel has no third digit.
        WriteDigits(frames[1], 3, loadPct, 2);
        frames[1][6] = 0x01;
        frames[1][7] = 0x01;

        // 0x02: no byte of this sub-command changes anything on the glass. Vendor
        // constants, sent to keep the cycle shape.
        frames[2][3] = 0x02;
        frames[2][4] = 0x04;

        // 0x03: the four-digit CPU clock. Not a fan reading despite sitting under the
        // fan icon: the vendor's own value steps by up to 4200 between consecutive
        // 1.1s cycles and tops out at the part's boost ceiling, which no fan does.
        WriteDigits(frames[3], 2, mhz, 4);
        frames[3][6] = 0x02;

        // 0x04: byte-identical in every captured cycle; commit/refresh.
        frames[4][3] = 0x09;
        frames[4][4] = 0x09;
        frames[4][6] = 0x01;

        return frames;
    }

    /// <summary>
    /// Builds the single CoolerMaster report. Unlike Levelplay this is plain binary:
    /// load and temp are whole bytes and the clock is a big-endian uint16.
    /// </summary>
    public static byte[] BuildCoolerMasterFrame(int loadPct, int mhz, int tempC)
    {
        var f = new byte[ReportLength];
        f[0] = CoolerMasterReportId;
        f[1] = 0x08;

        loadPct = Math.Clamp(loadPct, 0, 100);
        mhz = Math.Clamp(mhz, 0, CoolerMasterMaxMhz);
        tempC = Math.Clamp(tempC, 0, 255);

        f[2] = (byte)loadPct;
        f[3] = (byte)(mhz >> 8);
        f[4] = (byte)(mhz & 0xFF);
        f[5] = (byte)tempC;

        f[9] = Notches(tempC, 20, 90);
        f[10] = Notches(mhz, 800, 5000);
        f[11] = Notches(loadPct, 0, 100);

        f[12] = 0xF9;
        return f;
    }

    /// <summary>
    /// A blanked panel: the report id alone. Bench-verified by switching a live frame
    /// stream to this one with no gap, which darkens the glass at once rather than
    /// waiting out the panel's own fade.
    /// </summary>
    public static byte[] BuildCoolerMasterBlankFrame()
    {
        var f = new byte[ReportLength];
        f[0] = CoolerMasterReportId;
        return f;
    }

    /// <summary>
    /// Maps a reading onto the panel's bar, linearly, clamped at both ends. Only a
    /// true zero empties the bar: a running CPU idling below the first step still
    /// lights one segment, so an empty bar means "no reading", not "low reading".
    /// </summary>
    internal static byte Notches(int value, int min, int max)
    {
        if (value <= 0 || max <= min) return 0;
        var span = (double)(max - min);
        var scaled = (value - min) / span * CoolerMasterMaxNotches;
        return (byte)Math.Clamp((int)Math.Round(scaled), 1, CoolerMasterMaxNotches);
    }
}
