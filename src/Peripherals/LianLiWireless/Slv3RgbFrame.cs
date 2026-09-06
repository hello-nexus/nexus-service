using System;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// Pure builders for the RF_RgbSync (0x20) wire format: the raw per-LED
/// buffer (brightness + power cap applied) and its multi-part 240-byte RF
/// payload framing, laid out exactly as L-Connect's MasterDevice.SyncRgbData
/// puts it on the air (Y70 USBPcap 2026-09-04): part 0 is a header-only
/// packet, parts 1..N carry the TinyUZ stream in 220-byte pieces at [20..],
/// and byte [19] is the data-part count plus one.
/// </summary>
public static class Slv3RgbFrame
{
    /// <summary>SL V2/V3 wire LED count per physical fan (slv3:92887).</summary>
    public const int LedsPerFan = 40;

    /// <summary>Compressed-data capacity of each data packet (lzo_rgb_rf_valid_len).</summary>
    public const int DataPacketChunk = 220;

    /// <summary>Offset of the compressed data inside a data packet.</summary>
    public const int DataPacketOffset = 20;

    private const int PowerCapSum = 600;
    private const double PowerCapScale = 0.95;

    /// <summary>
    /// Applies brightness (wire = color * bright/255 &gt;&gt; 8, GetRgbData) then
    /// the power cap (scale x0.95 until R+G+B&lt;=600, GetRgb slv3:77389) to
    /// every LED, returning one animation frame's raw R,G,B-interleaved buffer
    /// (the uncompressed input to <see cref="TinyUz.Compress"/>).
    /// </summary>
    public static byte[] BuildFrameBuffer(ReadOnlySpan<RgbColor> leds, int brightnessPercent)
    {
        var bright = Math.Clamp((int)Math.Round(brightnessPercent * 255.0 / 100.0), 0, 255);
        var buf = new byte[leds.Length * 3];
        for (var i = 0; i < leds.Length; i++)
        {
            var r = (leds[i].R * bright) >> 8;
            var g = (leds[i].G * bright) >> 8;
            var b = (leds[i].B * bright) >> 8;
            while (r + g + b > PowerCapSum)
            {
                r = (int)(r * PowerCapScale);
                g = (int)(g * PowerCapScale);
                b = (int)(b * PowerCapScale);
            }
            var off = i * 3;
            buf[off] = (byte)r;
            buf[off + 1] = (byte)g;
            buf[off + 2] = (byte)b;
        }
        return buf;
    }

    /// <summary>4-byte big-endian change id derived from a ms timestamp; the firmware ignores a resend carrying the same value.</summary>
    public static byte[] BuildEffectIndex(long unixTimeMs)
    {
        var v = unchecked((uint)unixTimeMs);
        return new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
    }

    /// <summary>
    /// Builds the RF_RgbSync payload set. Index 0 is the header packet: common
    /// fields, then [20..23] compressed length BE32, [24] 0, [25..26] total
    /// frames BE16, [27] LED count, [32..33] frame interval ms BE16, [34] the
    /// interval's centi-fraction (0 for whole ms), [35..36] sub-ring interval,
    /// [37] isOuterMatchMax 0, [38..39] sub-ring frame count. L-Connect derives
    /// the sub-ring pair from its inner/outer renders; a frame set rendered as
    /// one buffer runs both rings on the same clock, so they mirror the main
    /// interval and frame count. Indices 1..N carry <see cref="DataPacketChunk"/>
    /// bytes each at <see cref="DataPacketOffset"/>. Every array is exactly
    /// <see cref="Slv3Protocol.RfPayloadSize"/> bytes.
    /// </summary>
    public static byte[][] BuildPackets(
        ReadOnlySpan<byte> fanMac, ReadOnlySpan<byte> masterMac, ReadOnlySpan<byte> effectIndex,
        ReadOnlySpan<byte> compressed, int ledCount, int totalFrames, int intervalMs)
    {
        var dataPackets = (compressed.Length + DataPacketChunk - 1) / DataPacketChunk;
        var totalPackets = dataPackets + 1;
        if (totalPackets > 255)
        {
            throw new InvalidOperationException("RGB payload is too large to transmit");
        }

        var packets = new byte[totalPackets][];
        var dataOffset = 0;
        for (var packetIndex = 0; packetIndex < totalPackets; packetIndex++)
        {
            var payload = new byte[Slv3Protocol.RfPayloadSize];
            payload[0] = Slv3Protocol.RfFrameType;
            payload[1] = Slv3Protocol.RfRgbSync;
            fanMac.Slice(0, Slv3Protocol.MacLength).CopyTo(payload.AsSpan(2));
            masterMac.Slice(0, Slv3Protocol.MacLength).CopyTo(payload.AsSpan(8));
            effectIndex.Slice(0, 4).CopyTo(payload.AsSpan(14));
            payload[18] = (byte)packetIndex;
            payload[19] = (byte)totalPackets;

            if (packetIndex == 0)
            {
                payload[20] = (byte)(compressed.Length >> 24);
                payload[21] = (byte)(compressed.Length >> 16);
                payload[22] = (byte)(compressed.Length >> 8);
                payload[23] = (byte)compressed.Length;
                payload[24] = 0;
                payload[25] = (byte)(totalFrames >> 8);
                payload[26] = (byte)totalFrames;
                payload[27] = (byte)ledCount;
                payload[32] = (byte)(intervalMs >> 8);
                payload[33] = (byte)intervalMs;
                payload[34] = 0;
                payload[35] = (byte)(intervalMs >> 8);
                payload[36] = (byte)intervalMs;
                payload[37] = 0;
                payload[38] = (byte)(totalFrames >> 8);
                payload[39] = (byte)totalFrames;
            }
            else
            {
                var chunkLen = Math.Min(DataPacketChunk, compressed.Length - dataOffset);
                compressed.Slice(dataOffset, chunkLen).CopyTo(payload.AsSpan(DataPacketOffset));
                dataOffset += chunkLen;
            }
            packets[packetIndex] = payload;
        }
        return packets;
    }
}
