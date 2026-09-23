using System;
using System.Buffers.Binary;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// ZMatrices cooler LCDs on WinUSB (38C1:0026). Commands go to EP 0x04, pictures to EP 0x02.
/// A picture is a 4:2:0 JPEG in 505-byte chunks, each behind a 7-byte "Trans" header so it
/// fills one 512-byte packet, framed by a 16-byte "Start" header and a "DCLdfinish" trailer.
/// </summary>
public static class ZMatricesProtocol
{
    public const byte CommandPipe = 0x04;
    public const byte PicturePipe = 0x02;

    public const int PacketSize = 512;
    public const int TransHeaderSize = 7;
    public const int TransPayloadSize = PacketSize - TransHeaderSize;

    private const byte FileTypeJpeg = 1;

    /// <summary>The firmware discards every 7th picture after picture mode, starting with the first.</summary>
    public const int DiscardedPictureInterval = 7;

    public static ReadOnlySpan<byte> FinishFrame => "DCLdfinish"u8;

    /// <summary>Leaves the built-in animation for host pictures; a sum16 of bytes 0-40 closes it.</summary>
    public static byte[] EncodePictureModeCommand()
    {
        var command = new byte[43];
        command[0] = 0xAA;
        command[1] = 0x2E;
        command[2] = 0x05;
        command[3] = 0x01;
        BinaryPrimitives.WriteUInt16LittleEndian(command.AsSpan(41), Sum16(command.AsSpan(0, 41)));
        return command;
    }

    /// <summary>Sets the backlight, 0-100. Byte 3 is the rotation in quarter turns; Nexus streams landscape.</summary>
    public static byte[] EncodeBrightnessCommand(int percent) =>
        new byte[] { 0xAA, 0x2E, 0x04, 0x00, (byte)Math.Clamp(percent, 0, 100), 0x00 };

    public static int TransPacketCount(int jpegLength) =>
        Math.Max(1, (jpegLength + TransPayloadSize - 1) / TransPayloadSize);

    public static byte[] EncodeStartFrame(ReadOnlySpan<byte> jpeg)
    {
        var start = new byte[16];
        "Start"u8.CopyTo(start);
        start[5] = FileTypeJpeg;
        BinaryPrimitives.WriteUInt32LittleEndian(start.AsSpan(6), (uint)jpeg.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(start.AsSpan(10), Sum16(jpeg));
        BinaryPrimitives.WriteUInt16LittleEndian(start.AsSpan(12), (ushort)TransPacketCount(jpeg.Length));
        return start;
    }

    /// <summary>
    /// Packs every "Trans" packet back to back, numbered from 1; only the last may be short.
    /// The destination must hold <see cref="TransPacketCount"/> x <see cref="PacketSize"/>.
    /// </summary>
    public static int EncodeTransPackets(ReadOnlySpan<byte> jpeg, Span<byte> destination)
    {
        int count = TransPacketCount(jpeg.Length);
        int written = 0;
        for (int i = 0; i < count; i++)
        {
            var chunk = jpeg.Slice(i * TransPayloadSize, Math.Min(TransPayloadSize, jpeg.Length - i * TransPayloadSize));
            var packet = destination.Slice(written, i < count - 1 ? PacketSize : TransHeaderSize + chunk.Length);
            packet.Clear();
            "Trans"u8.CopyTo(packet);
            BinaryPrimitives.WriteUInt16LittleEndian(packet[5..], (ushort)(i + 1));
            chunk.CopyTo(packet[TransHeaderSize..]);
            written += packet.Length;
        }
        return written;
    }

    public static ushort Sum16(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        foreach (var b in data)
        {
            sum += b;
        }
        return (ushort)sum;
    }
}
