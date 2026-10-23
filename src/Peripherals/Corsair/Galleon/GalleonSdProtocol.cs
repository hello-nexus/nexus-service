using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.Hyte.Np50;

namespace Nexus.Service.Peripherals.Corsair.Galleon;

/// <summary>Outcome of matching a device reply to the command it answers.</summary>
public readonly record struct GalleonReply(bool Parsed, byte Status)
{
    public bool IsOk => Parsed && Status == 0;
}

/// <summary>
/// Pure frame builders for the keyboard half of the Corsair Galleon 100 SD
/// (USB 1B1C:2B0C, vendor interface with interrupt OUT 0x04 and IN 0x84).
/// Corsair "V2 peripheral" command set, the family OpenRGB drives through
/// CorsairPeripheralV2Controller. Every OUT report is 128 bytes:
/// <c>00 01 00 CMD ARGS... 00-pad</c>. Header, command ids, mode switch,
/// start/stop transaction and the 06/07 block-write layout are confirmed by a
/// Web Hub capture; the direct-LED resource, payload and handle come from
/// OpenRGB (K100 path) and have never run against this board.
/// </summary>
public static class GalleonSdProtocol
{
    public const int VendorId = 0x1B1C;
    public const int ProductId = 0x2B0C;

    public const string HandlerId = "corsair-galleon-keyboard";
    public const string DeviceIdPrefix = "corsair-galleon:";
    public const string ProductName = "Corsair Galleon 100 SD";

    /// <summary>Vendor usage page of the command interface, as OpenRGB matches it.</summary>
    public const int VendorUsagePage = 0xFF42;

    public const int WireLength = 128;
    public const int CommandIndex = 3;
    public const int ArgsIndex = 4;

    public const byte CmdSet = 0x01;
    public const byte CmdGet = 0x02;
    public const byte CmdStopTx = 0x05;
    public const byte CmdBlockFirst = 0x06;
    public const byte CmdBlockNext = 0x07;
    public const byte CmdStartTx = 0x0D;

    public const byte PropMode = 0x03;
    public const byte ModeHardware = 0x01;
    public const byte ModeSoftware = 0x02;

    /// <summary>Lighting-control resource OpenRGB pins the K100 family to; the only resource this driver opens.</summary>
    public const ushort DirectResource = 0x0022;

    /// <summary>Direct-LED payload tag (CORSAIR_V2_MODE_DIRECT), little endian ahead of the triplets.</summary>
    public const ushort DirectTag = 0x0012;

    public const int DirectHeaderBytes = 2;

    /// <summary>Data bytes in the 06 packet (after the 9-byte header) and in each 07 packet (after the 5-byte header).</summary>
    public const int FirstChunkBytes = WireLength - 9;
    public const int NextChunkBytes = WireLength - 5;

    /// <summary>Highest handle tried when the start reply rejects the previous one.</summary>
    public const byte MaxHandle = 3;

    /// <summary>Key ids of the Web Hub static layer (HID usage numbering, ascending); also the engine's LED order.</summary>
    public static readonly int[] KeyIds =
    {
        4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29,
        30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53,
        54, 55, 56, 57, 58, 59, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77,
        78, 79, 80, 81, 82, 83, 84, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94, 95, 96, 97, 98, 99, 100, 101,
        105, 106, 107, 108, 109, 110, 111, 115, 116, 117, 118, 119, 122, 124,
        129, 130, 131, 132, 133, 134, 135, 139, 140, 141, 142, 143, 144, 145,
    };

    public static int LedCount => KeyIds.Length;

    /// <summary>Triplet slots in the direct payload: indexed by key id, so slot 0 through the highest id.</summary>
    public static int DirectEntryCount => KeyIds[^1] + 1;

    public static int DirectPayloadLength => DirectHeaderBytes + (DirectEntryCount * 3);

    public static byte[] Command(byte cmd, ReadOnlySpan<byte> args)
    {
        var frame = new byte[WireLength];
        frame[1] = 0x01;
        frame[CommandIndex] = cmd;
        args.CopyTo(frame.AsSpan(ArgsIndex));
        return frame;
    }

    public static byte[] SetMode(byte mode) => Command(CmdSet, stackalloc byte[] { PropMode, 0x00, mode });

    public static byte[] GetProperty(byte property) => Command(CmdGet, stackalloc byte[] { property });

    public static byte[] StartTransaction(byte handle, ushort resource) =>
        Command(CmdStartTx, stackalloc byte[] { handle, (byte)(resource & 0xFF), (byte)(resource >> 8) });

    public static byte[] StopTransaction(byte handle) => Command(CmdStopTx, stackalloc byte[] { 0x01, handle });

    /// <summary>06 packet carrying the total length, then 07 packets for the rest of <paramref name="data"/>.</summary>
    public static List<byte[]> BlockWrite(byte handle, ReadOnlySpan<byte> data)
    {
        var chunks = new List<byte[]>(1 + (data.Length / NextChunkBytes));
        var first = Math.Min(data.Length, FirstChunkBytes);
        var head = new byte[WireLength];
        head[1] = 0x01;
        head[CommandIndex] = CmdBlockFirst;
        head[4] = handle;
        head[5] = (byte)(data.Length & 0xFF);
        head[6] = (byte)(data.Length >> 8);
        data[..first].CopyTo(head.AsSpan(9));
        chunks.Add(head);

        var offset = first;
        while (offset < data.Length)
        {
            var take = Math.Min(data.Length - offset, NextChunkBytes);
            var next = new byte[WireLength];
            next[1] = 0x01;
            next[CommandIndex] = CmdBlockNext;
            next[4] = handle;
            data.Slice(offset, take).CopyTo(next.AsSpan(5));
            chunks.Add(next);
            offset += take;
        }
        return chunks;
    }

    /// <summary>Fills <paramref name="payload"/> (length <see cref="DirectPayloadLength"/>) with the direct tag and an RGB triplet per key id; ids without a key stay black.</summary>
    public static void EncodeDirect(ReadOnlySpan<RgbColor> leds, Span<byte> payload)
    {
        payload.Clear();
        payload[0] = (byte)(DirectTag & 0xFF);
        payload[1] = (byte)(DirectTag >> 8);
        var n = Math.Min(leds.Length, KeyIds.Length);
        for (var i = 0; i < n; i++)
        {
            var at = DirectHeaderBytes + (KeyIds[i] * 3);
            payload[at] = leds[i].R;
            payload[at + 1] = leds[i].G;
            payload[at + 2] = leds[i].B;
        }
    }

    /// <summary>The buffer the HID layer takes: a leading report-id byte (0, no ids declared) then the wire report.</summary>
    public static byte[] ToHidBuffer(byte[] wire)
    {
        var buf = new byte[wire.Length + 1];
        wire.CopyTo(buf, 1);
        return buf;
    }

    /// <summary>
    /// True when the collection's report length shows a 128-byte report with no
    /// report id: Windows counts the id byte (129), the other stacks do not (128).
    /// </summary>
    public static bool ReportLayoutConfirmed(int outputReportByteLength) =>
        outputReportByteLength == (OperatingSystem.IsWindows() ? WireLength + 1 : WireLength);

    /// <summary>Interface candidates: the only collection with a 128-byte output report.</summary>
    public static bool IsCommandInterface(int outputReportByteLength) =>
        outputReportByteLength == WireLength || outputReportByteLength == WireLength + 1;

    /// <summary>
    /// Replies were never captured. A reply parses only when it echoes the request
    /// header position (<c>.. .. .. CMD STATUS</c>); anything else is unparsed and logged raw.
    /// </summary>
    public static GalleonReply ParseReply(byte cmd, ReadOnlySpan<byte> reply)
    {
        if (reply.Length == WireLength + 1 && reply[0] == 0)
        {
            reply = reply[1..];
        }
        if (reply.Length > ArgsIndex && reply[CommandIndex] == cmd)
        {
            return new GalleonReply(true, reply[ArgsIndex]);
        }
        return default;
    }

    public static (float[] U, float[] V) ComputeUv()
    {
        var u = new float[KeyIds.Length];
        var v = new float[KeyIds.Length];
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        var extra = 0;
        for (var i = 0; i < KeyIds.Length; i++)
        {
            float x, y;
            if (KeyCells.TryGetValue(KeyIds[i], out var cell))
            {
                (x, y) = cell;
            }
            else
            {
                (x, y) = (extra++, -1.6f);
            }
            u[i] = x;
            v[i] = y;
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
        }
        for (var i = 0; i < u.Length; i++)
        {
            u[i] = (u[i] - minX) / (maxX - minX);
            v[i] = (v[i] - minY) / (maxY - minY);
        }
        return (u, v);
    }

    // Full-size ANSI/ISO grid in key units, keyed by HID usage; ids absent here (the K100-class extras) sit in a row above.
    private static readonly Dictionary<int, (float X, float Y)> KeyCells = BuildCells();

    private static Dictionary<int, (float X, float Y)> BuildCells()
    {
        var cells = new Dictionary<int, (float X, float Y)>();
        void Row(float y, float x, params int[] ids)
        {
            for (var i = 0; i < ids.Length; i++)
            {
                if (ids[i] != 0) cells[ids[i]] = (x + i, y);
            }
        }
        void At(int id, float x, float y) => cells[id] = (x, y);

        At(41, 0, 0);
        Row(0, 2, 58, 59, 60, 61);
        Row(0, 6.5f, 62, 63, 64, 65);
        Row(0, 11, 66, 67, 68, 69);
        Row(0, 15.5f, 70, 71, 72);

        At(53, 0, 1.5f);
        Row(1.5f, 1, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 45, 46);
        At(42, 13.5f, 1.5f);
        Row(1.5f, 15.5f, 73, 74, 75);
        Row(1.5f, 19, 83, 84, 85, 86);

        At(43, 0.5f, 2.5f);
        Row(2.5f, 1.5f, 20, 26, 8, 21, 23, 28, 24, 12, 18, 19, 47, 48);
        At(49, 13.75f, 2.5f);
        Row(2.5f, 15.5f, 76, 77, 78);
        Row(2.5f, 19, 95, 96, 97);
        At(87, 22, 3);

        At(57, 0.6f, 3.5f);
        Row(3.5f, 1.75f, 4, 22, 7, 9, 10, 11, 13, 14, 15, 51, 52, 50);
        At(40, 14, 3.5f);
        Row(3.5f, 19, 92, 93, 94);

        At(106, 0.4f, 4.5f);
        At(100, 1.5f, 4.5f);
        Row(4.5f, 2.5f, 29, 27, 6, 25, 5, 17, 16, 54, 55, 56);
        At(110, 13.4f, 4.5f);
        At(82, 16.5f, 4.5f);
        Row(4.5f, 19, 89, 90, 91);
        At(88, 22, 5);

        At(105, 0.1f, 5.5f);
        At(108, 1.3f, 5.5f);
        At(107, 2.6f, 5.5f);
        At(44, 6.5f, 5.5f);
        At(111, 10.4f, 5.5f);
        At(122, 11.7f, 5.5f);
        At(101, 13, 5.5f);
        At(109, 14.3f, 5.5f);
        Row(5.5f, 15.5f, 80, 81, 79);
        At(98, 19.5f, 5.5f);
        At(99, 21, 5.5f);
        return cells;
    }
}
