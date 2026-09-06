using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.Nollie;

/// <summary>
/// Wire format for Nollie ARGB channel controllers (and the Prism8 that shares
/// the firmware). Ported from OpenRGB's NollieController, which is
/// GPL-2.0-or-later and so compatible with this project's AGPL-3.0.
///
/// Two transports, picked by PID:
///
/// <list type="bullet">
/// <item><b>Wide</b> - one 1025-byte report carries a whole channel. Used by the
/// 16- and 32-channel controllers.</item>
/// <item><b>Chunked</b> - 65-byte reports carrying 21 LEDs each, with the report
/// id encoding both the chunk number and the channel. Used by everything
/// else.</item>
/// </list>
///
/// Colour order is GRB on the wide transport and on the 1/8-channel chunked
/// devices; the remaining chunked devices take RGB.
///
/// The firmware never reports how many LEDs are wired to a channel - there is no
/// read command in the protocol at all - so the count is a user declaration, the
/// same situation as the HYTE Smart Hub's ARGB ports.
/// </summary>
public static class NollieProtocol
{
    /// <summary>OS2 firmware generation; the VID the shipping controllers enumerate on.</summary>
    public const int VendorIdOs2 = 0x16D5;
    /// <summary>Original 1/8/28-channel controllers.</summary>
    public const int VendorIdLegacy = 0x16D2;
    /// <summary>Original 16/32-channel controllers.</summary>
    public const int VendorIdHighChannel = 0x3061;

    /// <summary>Vendor HID usage page the RGB interface advertises.</summary>
    public const int VendorUsagePage = 0xFF00;
    /// <summary>Vendor HID usage the RGB interface advertises.</summary>
    public const int VendorUsage = 0x0001;

    /// <summary>Report size for <see cref="NollieTransport.Wide"/>.</summary>
    public const int WideReportSize = 1025;
    /// <summary>Report size for <see cref="NollieTransport.Chunked"/>.</summary>
    public const int ChunkedReportSize = 65;
    /// <summary>LEDs carried by one chunked report.</summary>
    public const int LedsPerChunk = 21;

    /// <summary>Channels the 32-channel controller drives out of band; they take a marker byte and a settle delay.</summary>
    private const int Flag1Channel = 15;
    private const int Flag2Channel = 31;
    /// <summary>Milliseconds to settle after a flag-channel write, per the reference driver.</summary>
    public const int FlagChannelSettleMs = 8;

    // ── Channel index maps ──
    // Card N drives hardware channel map[N], so the label the user sees lines up
    // with the board silkscreen rather than the firmware's internal ordering.

    private static readonly int[] Identity =
    {
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31,
    };

    private static readonly int[] Map32 =
    {
        5, 4, 3, 2, 1, 0, 15, 14, 26, 27, 28, 29, 30, 31, 8, 9,
        19, 18, 17, 16, 7, 6, 25, 24, 23, 22, 21, 20, 13, 12, 11, 10,
    };

    private static readonly int[] Map16Legacy =
    {
        19, 18, 17, 16, 24, 25, 26, 27, 20, 21, 22, 23, 31, 30, 29, 28,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
    };

    private static readonly int[] Map16Os2 = { 3, 2, 1, 0, 8, 9, 10, 11, 4, 5, 6, 7, 15, 14, 13, 12 };

    /// <summary>
    /// Every controller the driver knows, keyed by VID/PID. Interface is the USB
    /// interface carrying the RGB endpoint on the composite OS2_1 devices; -1
    /// where the device is not composite and any matching interface will do.
    /// </summary>
    public static readonly NollieDevice[] Devices =
    {
        // Original firmware.
        new(VendorIdHighChannel, 0x4714, "Nollie 32CH",  32, 256, Map32,      NollieTransport.Wide,    -1, ChunkStride: 25),
        new(VendorIdHighChannel, 0x4716, "Nollie 16CH",  16, 256, Map16Legacy, NollieTransport.Wide,   -1, ChunkStride: 25),
        new(VendorIdLegacy,      0x1F01, "Nollie 8CH",    8, 126, Identity,   NollieTransport.Chunked, -1, ChunkStride: 6),
        new(VendorIdLegacy,      0x1F11, "Nollie 1CH",    1, 630, Identity,   NollieTransport.Chunked, -1, ChunkStride: 30),
        new(VendorIdLegacy,      0x1616, "Nollie 28 12",  1,  42, Identity,   NollieTransport.Chunked, -1, ChunkStride: 2),
        new(VendorIdLegacy,      0x1617, "Nollie 28 L1",  8, 525, Identity,   NollieTransport.Chunked, -1, ChunkStride: 25),
        new(VendorIdLegacy,      0x1618, "Nollie 28 L2",  8, 525, Identity,   NollieTransport.Chunked, -1, ChunkStride: 25),

        // OS2 firmware on the new VID, legacy PIDs.
        new(VendorIdOs2, 0x4714, "Nollie 32_OS2", 32, 256, Map32,       NollieTransport.Wide,    -1, ChunkStride: 25),
        new(VendorIdOs2, 0x4716, "Nollie 16_OS2", 16, 256, Map16Os2,    NollieTransport.Wide,    -1, ChunkStride: 25),
        new(VendorIdOs2, 0x1F01, "Nollie 8_OS2",   8, 126, Identity,    NollieTransport.Chunked, -1, ChunkStride: 6),
        new(VendorIdOs2, 0x1F11, "Nollie 1_OS2",   1, 630, Identity,    NollieTransport.Chunked, -1, ChunkStride: 30),

        // OS2_1 composite devices; RGB rides interface 0 on the high-channel
        // parts and interface 2 on the 1/8-channel parts.
        new(VendorIdOs2, 0x2A32, "Nollie 32_OS2_1", 32, 256, Map32,    NollieTransport.Wide,    0, ChunkStride: 25),
        new(VendorIdOs2, 0x2A16, "Nollie 16_OS2_1", 16, 256, Map16Os2, NollieTransport.Wide,    0, ChunkStride: 25),
        new(VendorIdOs2, 0x2A08, "Nollie 8_OS2_1",   8, 126, Identity, NollieTransport.Chunked, 2, ChunkStride: 6),
        new(VendorIdOs2, 0x2C08, "Prism8 8_OS2_1",   8, 126, Identity, NollieTransport.Chunked, 2, ChunkStride: 6),
        new(VendorIdOs2, 0x2A01, "Nollie 1_OS2_1",   1, 630, Identity, NollieTransport.Chunked, 2, ChunkStride: 30),
    };

    /// <summary>
    /// Distinct vendor ids across <see cref="Devices"/>. Derived, not hand-listed:
    /// the presence gate in NollieConnectionWorker keys on this, so a new row under
    /// a fourth VID would otherwise become undetectable with nothing logged.
    /// </summary>
    public static readonly int[] VendorIds = BuildVendorIds();

    private static int[] BuildVendorIds()
    {
        var ids = new List<int>();
        foreach (var d in Devices)
        {
            if (!ids.Contains(d.VendorId)) ids.Add(d.VendorId);
        }
        return ids.ToArray();
    }

    public static NollieDevice? Lookup(int vendorId, int productId)
    {
        foreach (var d in Devices)
        {
            if (d.VendorId == vendorId && d.ProductId == productId) return d;
        }
        return null;
    }

    /// <summary>
    /// Channel label. The high indices are the 32-channel board's dedicated
    /// ATX / GPU / extension headers, which are silkscreened separately.
    /// </summary>
    public static string ChannelName(int channel)
    {
        if (channel > 27) return $"Channel EXT {channel + 1 - 28}";
        if (channel > 21) return $"Channel GPU {channel + 1 - 22}";
        if (channel > 15) return $"Channel ATX {channel + 1 - 16}";
        return $"Channel {channel + 1}";
    }

    /// <summary>Out-of-band channels needing a marker byte and a settle delay; keyed on the hardware channel value, as the reference driver does, so a 16-channel board's hw channel 15 also qualifies.</summary>
    public static bool IsFlagChannel(NollieDevice device, int hardwareChannel)
        => device.Transport == NollieTransport.Wide
        && (hardwareChannel == Flag1Channel || hardwareChannel == Flag2Channel);

    /// <summary>Marker byte a flag channel carries in byte 2; 0 for an ordinary channel.</summary>
    public static byte FlagMarker(NollieDevice device, int hardwareChannel)
    {
        if (device.Transport != NollieTransport.Wide) return 0;
        if (hardwareChannel == Flag1Channel) return 1;
        if (hardwareChannel == Flag2Channel) return 2;
        return 0;
    }

    /// <summary>
    /// Fills a <see cref="WideReportSize"/> report with a whole channel's colours.
    /// report[1]=hardware channel, [2]=flag marker, [3]/[4]=count high/low,
    /// then GRB triples from offset 5.
    /// </summary>
    public static void WriteWide(Span<byte> report, NollieDevice device, int hardwareChannel, ReadOnlySpan<byte> rgb)
    {
        report.Clear();
        var ledCount = rgb.Length / 3;
        report[1] = (byte)hardwareChannel;
        report[2] = FlagMarker(device, hardwareChannel);
        report[3] = (byte)(ledCount / 256);
        report[4] = (byte)(ledCount % 256);

        var dst = 5;
        for (var src = 0; src + 2 < rgb.Length && dst + 2 < report.Length; src += 3, dst += 3)
        {
            report[dst]     = rgb[src + 1]; // G
            report[dst + 1] = rgb[src];     // R
            report[dst + 2] = rgb[src + 2]; // B
        }
    }

    /// <summary>
    /// Fills a <see cref="ChunkedReportSize"/> report with up to
    /// <see cref="LedsPerChunk"/> LEDs. report[1] encodes chunk and channel
    /// together as <c>chunk + channel * stride</c>.
    /// </summary>
    public static void WriteChunk(Span<byte> report, NollieDevice device, int hardwareChannel, int chunkIndex, ReadOnlySpan<byte> rgb)
    {
        report.Clear();
        report[1] = (byte)(chunkIndex + hardwareChannel * device.ChunkStride);

        var dst = 2;
        for (var src = 0; src + 2 < rgb.Length && dst + 2 < report.Length; src += 3, dst += 3)
        {
            if (device.ChunkedIsGrb)
            {
                report[dst]     = rgb[src + 1]; // G
                report[dst + 1] = rgb[src];     // R
            }
            else
            {
                report[dst]     = rgb[src];     // R
                report[dst + 1] = rgb[src + 1]; // G
            }
            report[dst + 2] = rgb[src + 2];     // B
        }
    }

    /// <summary>Latch report ending a chunked update cycle (report[1]=0xFF).</summary>
    public static void WriteLatch(Span<byte> report)
    {
        report.Clear();
        report[1] = 0xFF;
    }

    /// <summary>
    /// Declares per-channel LED counts to the firmware (report[1]=0xFE,
    /// [2]=0x03, then little-endian uint16 per channel). Only the legacy 1CH
    /// controller is known to want this; the reference driver sends it nowhere
    /// else, so <see cref="NollieDevice.WantsLedCountHandshake"/> gates it.
    /// </summary>
    public static void WriteLedCounts(Span<byte> report, ReadOnlySpan<int> countsPerChannel)
    {
        report.Clear();
        report[1] = 0xFE;
        report[2] = 0x03;
        for (var i = 0; i < countsPerChannel.Length && 4 + i * 2 < report.Length; i++)
        {
            var c = countsPerChannel[i];
            report[3 + i * 2] = (byte)(c & 0xFF);
            report[4 + i * 2] = (byte)((c >> 8) & 0xFF);
        }
    }
}

public enum NollieTransport
{
    /// <summary>One 1025-byte report per channel.</summary>
    Wide,
    /// <summary>65-byte reports carrying 21 LEDs each.</summary>
    Chunked,
}

/// <summary>One entry of the supported-controller table.</summary>
/// <param name="VendorId">USB vendor id.</param>
/// <param name="ProductId">USB product id.</param>
/// <param name="Name">Controller name, matching what OpenRGB calls it so support tables line up.</param>
/// <param name="Channels">Independently wired ARGB channels.</param>
/// <param name="MaxLedsPerChannel">Ceiling the firmware accepts on one channel.</param>
/// <param name="ChannelMap">Card index to hardware channel.</param>
/// <param name="Transport">Which report shape this controller takes.</param>
/// <param name="InterfaceNumber">USB interface carrying the RGB endpoint, or -1 when the device is not composite.</param>
/// <param name="ChunkStride">Multiplier applied to the channel when packing the chunked report id.</param>
public sealed record NollieDevice(
    int VendorId,
    int ProductId,
    string Name,
    int Channels,
    int MaxLedsPerChannel,
    int[] ChannelMap,
    NollieTransport Transport,
    int InterfaceNumber,
    int ChunkStride)
{
    /// <summary>
    /// Chunked transport colour order. The 1- and 8-channel controllers (and the
    /// Prism8) take GRB; the 28-series takes RGB.
    /// </summary>
    public bool ChunkedIsGrb => ProductId is 0x1F01 or 0x1F11 or 0x2A08 or 0x2A01 or 0x2C08;

    /// <summary>Only the legacy 1CH controller takes the 0xFE/0x03 LED-count handshake.</summary>
    public bool WantsLedCountHandshake => VendorId == NollieProtocol.VendorIdLegacy && ProductId == 0x1F11;

    /// <summary>Hardware channel driven by card <paramref name="cardIndex"/>.</summary>
    public int HardwareChannel(int cardIndex)
        => cardIndex >= 0 && cardIndex < ChannelMap.Length ? ChannelMap[cardIndex] : cardIndex;
}
