using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.Hyte.Np50;

namespace Nexus.Service.Peripherals.Ibp;

public enum IbpPeripheralKind
{
    Keyboard,
    Mouse,
}

/// <summary>
/// Wire dialects the iBUYPOWER keyboards and mice speak. All four stream a
/// full frame through HID feature reports (report id in byte 0); they differ
/// in header, report length and how many reports a frame spans.
/// </summary>
public enum IbpWireFormat
{
    /// <summary>KM7 / KM10 keyboards: three 33-byte reports, 9 LEDs each, <c>08 08 F8 &lt;seq&gt;</c>.</summary>
    ChimeraKeyboard,
    /// <summary>KM7 / KM10 mice: one 33-byte report, <c>08 1D 01</c>.</summary>
    ChimeraMouse,
    /// <summary>MK9 / MK9 Pro: one 520-byte report, <c>06 08 00 00 01 00 7A 01</c>, 113 wire slots.</summary>
    Mk9,
    /// <summary>MEK 4: one 382-byte report, <c>08 0A 7A 01</c>, 126 wire slots.</summary>
    Mek4,
}

/// <summary>One physical LED: its position on the model's board grid and the slot it occupies on the wire.</summary>
public readonly record struct IbpLed(int Column, int Row, int WireSlot);

/// <summary>
/// Static description of one supported model. Frame order (the order the
/// lighting engine addresses LEDs) is the wire-slot order with unpopulated
/// slots skipped, so <see cref="Leds"/> is also the engine's LED table.
/// </summary>
public sealed class IbpPeripheralModel
{
    public IbpPeripheralModel(string id, string name, IbpPeripheralKind kind, int productId, IbpWireFormat wire,
        int reportLength, int wireSlots, int gridWidth, int gridHeight, IbpLed[] leds)
    {
        Id = id;
        Name = name;
        Kind = kind;
        ProductId = productId;
        Wire = wire;
        ReportLength = reportLength;
        WireSlots = wireSlots;
        GridWidth = gridWidth;
        GridHeight = gridHeight;
        Leds = leds;
    }

    /// <summary>Stable slug used in device ids ("km7-keyboard").</summary>
    public string Id { get; }
    /// <summary>Marketed name, used as the card / handler label.</summary>
    public string Name { get; }
    public IbpPeripheralKind Kind { get; }
    public int ProductId { get; }
    public IbpWireFormat Wire { get; }
    /// <summary>
    /// Feature report byte length including the report id. Doubles as the HID
    /// collection selector, exactly as the Nexus 2 driver picked its stream:
    /// of the unit's collections (boot keyboard, consumer control, vendor),
    /// the one whose feature report has this length is the one to open.
    /// </summary>
    public int ReportLength { get; }
    /// <summary>Color slots a frame carries on the wire, populated or not.</summary>
    public int WireSlots { get; }
    public int GridWidth { get; }
    public int GridHeight { get; }
    public IReadOnlyList<IbpLed> Leds { get; }
    public int LedCount => Leds.Count;
    public string HandlerId => Kind == IbpPeripheralKind.Keyboard ? IbpPeripheralProtocol.KeyboardHandlerId : IbpPeripheralProtocol.MouseHandlerId;

    /// <summary>Reports one frame spans.</summary>
    public int ReportCount => Wire == IbpWireFormat.ChimeraKeyboard ? IbpPeripheralProtocol.ChimeraKeyboardReports : 1;

    /// <summary>Bytes one encoded frame occupies (<see cref="ReportCount"/> × <see cref="ReportLength"/>).</summary>
    public int FrameBytes => ReportCount * ReportLength;

    /// <summary>Stock per-LED positions on the unit square, in frame order.</summary>
    public (float[] U, float[] V) ComputeUv()
    {
        var u = new float[LedCount];
        var v = new float[LedCount];
        for (var i = 0; i < LedCount; i++)
        {
            u[i] = GridWidth > 1 ? Leds[i].Column / (float)(GridWidth - 1) : 0.5f;
            v[i] = GridHeight > 1 ? Leds[i].Row / (float)(GridHeight - 1) : 0.5f;
        }
        return (u, v);
    }
}

/// <summary>
/// Pure model catalog + frame encoders for the iBUYPOWER keyboards and mice
/// that enumerate under the HYTE/iBUYPOWER vendor id. No IO;
/// <see cref="IbpPeripheralHub"/> owns the HID handles. Transcribed from the
/// Nexus 2 LightDancing controllers (RedragonKeeb / RedragonMouse /
/// RedragonKM10* / MK9 / MK9Pro / RedragonMK4): report headers, LED grids and
/// wire-slot order are byte-for-byte theirs.
/// </summary>
public static class IbpPeripheralProtocol
{
    public const int VendorId = 0x3402;

    public const string KeyboardHandlerId = "ibp-keyboard";
    public const string MouseHandlerId = "ibp-mouse";

    /// <summary>Device id prefix shared by every card / frame this stack emits.</summary>
    public const string DeviceIdPrefix = "ibp:";

    public const byte ReportIdChimera = 0x08;
    public const byte ReportIdMk9 = 0x06;

    public const int ChimeraReportLength = 33;
    public const int ChimeraKeyboardReports = 3;
    public const int ChimeraKeyboardLedsPerReport = 9;
    public const int ChimeraKeyboardColorOffset = 6;
    public const int ChimeraMouseColorOffset = 3;

    public const int Mk9ReportLength = 520;
    public const int Mk9WireSlots = 113;
    public const int Mk9ColorOffset = 8;

    public const int Mek4ReportLength = 382;
    public const int Mek4WireSlots = 126;
    public const int Mek4ColorOffset = 4;

    // ── Model catalog ──

    public static readonly IbpPeripheralModel Km7Keyboard = new(
        "km7-keyboard", "iBUYPOWER Chimera KM7 Keyboard", IbpPeripheralKind.Keyboard, 0x0301,
        IbpWireFormat.ChimeraKeyboard, ChimeraReportLength, ChimeraKeyboardReports * ChimeraKeyboardLedsPerReport,
        gridWidth: 11, gridHeight: 6, ChimeraKeyboardLeds());

    public static readonly IbpPeripheralModel Km10Keyboard = new(
        "km10-keyboard", "iBUYPOWER Chimera KM10 Keyboard", IbpPeripheralKind.Keyboard, 0x0305,
        IbpWireFormat.ChimeraKeyboard, ChimeraReportLength, ChimeraKeyboardReports * ChimeraKeyboardLedsPerReport,
        gridWidth: 11, gridHeight: 6, ChimeraKeyboardLeds());

    public static readonly IbpPeripheralModel Km7Mouse = new(
        "km7-mouse", "iBUYPOWER Chimera KM7 Mouse", IbpPeripheralKind.Mouse, 0x0200,
        IbpWireFormat.ChimeraMouse, ChimeraReportLength, wireSlots: 6,
        gridWidth: 4, gridHeight: 5, Km7MouseLeds());

    public static readonly IbpPeripheralModel Km10Mouse = new(
        "km10-mouse", "iBUYPOWER Chimera KM10 Mouse", IbpPeripheralKind.Mouse, 0x0201,
        IbpWireFormat.ChimeraMouse, ChimeraReportLength, wireSlots: 3,
        gridWidth: 1, gridHeight: 3, Km10MouseLeds());

    public static readonly IbpPeripheralModel Mk9Keyboard = new(
        "mk9-keyboard", "iBUYPOWER MK9 Keyboard", IbpPeripheralKind.Keyboard, 0x0303,
        IbpWireFormat.Mk9, Mk9ReportLength, Mk9WireSlots,
        gridWidth: 18, gridHeight: 6, Mk9Leds());

    public static readonly IbpPeripheralModel Mk9ProKeyboard = new(
        "mk9pro-keyboard", "iBUYPOWER MK9 Pro Keyboard", IbpPeripheralKind.Keyboard, 0x0304,
        IbpWireFormat.Mk9, Mk9ReportLength, Mk9WireSlots,
        gridWidth: 18, gridHeight: 6, Mk9Leds());

    public static readonly IbpPeripheralModel Mek4Keyboard = new(
        "mek4-keyboard", "iBUYPOWER MEK 4 Keyboard", IbpPeripheralKind.Keyboard, 0x0302,
        IbpWireFormat.Mek4, Mek4ReportLength, Mek4WireSlots,
        gridWidth: 21, gridHeight: 6, Mek4Leds());

    public static readonly IReadOnlyList<IbpPeripheralModel> Models = new[]
    {
        Km7Keyboard, Km10Keyboard, Mk9Keyboard, Mk9ProKeyboard, Mek4Keyboard,
        Km7Mouse, Km10Mouse,
    };

    public static IbpPeripheralModel? ForProductId(int productId)
    {
        foreach (var m in Models)
        {
            if (m.ProductId == productId) return m;
        }
        return null;
    }

    // ── Frame encoding ──

    /// <summary>
    /// Encode one frame into <paramref name="dest"/> (at least
    /// <see cref="IbpPeripheralModel.FrameBytes"/> long): <see cref="IbpPeripheralModel.ReportCount"/>
    /// consecutive feature reports of <see cref="IbpPeripheralModel.ReportLength"/> bytes.
    /// <paramref name="leds"/> is in frame order; missing trailing LEDs and
    /// unpopulated wire slots are black.
    /// </summary>
    public static void EncodeFrame(IbpPeripheralModel model, ReadOnlySpan<RgbColor> leds, Span<byte> dest)
    {
        if (dest.Length < model.FrameBytes)
            throw new ArgumentException($"Frame buffer must be at least {model.FrameBytes} bytes.", nameof(dest));
        dest = dest.Slice(0, model.FrameBytes);
        dest.Clear();
        WriteHeaders(model, dest);
        var n = Math.Min(leds.Length, model.LedCount);
        for (var i = 0; i < n; i++)
        {
            var offset = ColorOffset(model, model.Leds[i].WireSlot);
            var c = leds[i];
            dest[offset] = c.R;
            dest[offset + 1] = c.G;
            dest[offset + 2] = c.B;
        }
    }

    /// <summary>Byte offset of a wire slot's R byte within the encoded frame.</summary>
    internal static int ColorOffset(IbpPeripheralModel model, int wireSlot) => model.Wire switch
    {
        IbpWireFormat.ChimeraKeyboard =>
            (wireSlot / ChimeraKeyboardLedsPerReport) * ChimeraReportLength
            + ChimeraKeyboardColorOffset + (wireSlot % ChimeraKeyboardLedsPerReport) * 3,
        IbpWireFormat.ChimeraMouse => ChimeraMouseColorOffset + wireSlot * 3,
        IbpWireFormat.Mk9 => Mk9ColorOffset + wireSlot * 3,
        IbpWireFormat.Mek4 => Mek4ColorOffset + wireSlot * 3,
        _ => throw new ArgumentOutOfRangeException(nameof(model)),
    };

    private static void WriteHeaders(IbpPeripheralModel model, Span<byte> frame)
    {
        switch (model.Wire)
        {
            case IbpWireFormat.ChimeraKeyboard:
                for (var r = 0; r < ChimeraKeyboardReports; r++)
                {
                    var report = frame.Slice(r * ChimeraReportLength, ChimeraReportLength);
                    report[0] = ReportIdChimera;
                    report[1] = 0x08;
                    report[2] = 0xF8;
                    report[3] = (byte)(r + 1);
                }
                break;
            case IbpWireFormat.ChimeraMouse:
                frame[0] = ReportIdChimera;
                frame[1] = 0x1D;
                frame[2] = 0x01;
                break;
            case IbpWireFormat.Mk9:
                frame[0] = ReportIdMk9;
                frame[1] = 0x08;
                frame[4] = 0x01;
                frame[6] = 0x7A;
                frame[7] = 0x01;
                break;
            case IbpWireFormat.Mek4:
                frame[0] = ReportIdChimera;
                frame[1] = 0x0A;
                frame[2] = 0x7A;
                frame[3] = 0x01;
                break;
        }
    }

    // ── Mode switching ──

    /// <summary>
    /// Feature report that hands the LEDs to the host before the first
    /// streamed frame, or null when the model switches on the first frame by
    /// itself (MK9, MEK 4).
    /// </summary>
    public static byte[]? SoftwareModeReport(IbpPeripheralModel model) => model.Wire switch
    {
        IbpWireFormat.ChimeraKeyboard => Report(model.ReportLength, ReportIdChimera, 0x09, 0xF9, 0x00),
        IbpWireFormat.ChimeraMouse => Report(model.ReportLength, ReportIdChimera, 0x2D, 0x00),
        _ => null,
    };

    /// <summary>
    /// Feature report that returns the LEDs to the onboard animation once
    /// streaming stops, or null when the firmware has no such command (MK9).
    /// </summary>
    public static byte[]? FirmwareModeReport(IbpPeripheralModel model) => model.Wire switch
    {
        IbpWireFormat.ChimeraKeyboard => Report(model.ReportLength, ReportIdChimera, 0x09, 0xF9, 0x01),
        IbpWireFormat.ChimeraMouse => Report(model.ReportLength, ReportIdChimera, 0x2D, 0x01),
        IbpWireFormat.Mek4 => Report(model.ReportLength, ReportIdChimera, 0xF9, 0x01),
        _ => null,
    };

    private static byte[] Report(int length, params byte[] head)
    {
        var buf = new byte[length];
        head.CopyTo(buf, 0);
        return buf;
    }

    // ── LED tables (column, row, wire slot) ──

    // KM7 / KM10 keyboards light 24 zones around the board edge, wired
    // clockwise from the bottom-right: along the bottom, up the left side,
    // across the top, then down the right. Grid 11 × 6.
    private static IbpLed[] ChimeraKeyboardLeds()
    {
        (int C, int R)[] cells =
        {
            (9, 5), (8, 5), (7, 5), (6, 5), (5, 5), (4, 5), (3, 5), (2, 5), (1, 5),
            (0, 4), (0, 3), (0, 2), (0, 1),
            (1, 0), (2, 0), (3, 0), (4, 0), (5, 0), (6, 0), (7, 0),
            (8, 1), (9, 1), (10, 3), (10, 5),
        };
        return Sequential(cells);
    }

    // Six LEDs ringing the KM7 mouse shell. Grid 4 × 5.
    private static IbpLed[] Km7MouseLeds() =>
        Sequential(new (int, int)[] { (1, 0), (0, 2), (0, 4), (3, 4), (3, 2), (2, 0) });

    // Three LEDs down the KM10 mouse spine. Grid 1 × 3.
    private static IbpLed[] Km10MouseLeds() =>
        Sequential(new (int, int)[] { (0, 0), (0, 1), (0, 2) });

    // MK9 / MK9 Pro: 113 wire slots walked column by column; ten slots have no
    // key behind them (the wide keys' spare positions) and are skipped in
    // frame order but still streamed black. Row 4 (the bottom letter row) sits
    // one column right of its slot for columns 1..10. Grid 18 × 6.
    private static IbpLed[] Mk9Leds()
    {
        (sbyte R, sbyte C)[] slots =
        {
            (0, 0), (1, 0), (2, 0), (3, 0), (4, 0), (5, 0),
            (0, 1), (1, 1), (2, 1), (3, 1), (4, 2), (5, 1),
            (0, 2), (1, 2), (2, 2), (3, 2), (4, 3), (5, 2),
            (0, 3), (1, 3), (2, 3), (3, 3), (4, 4), (5, 4),
            (0, 4), (1, 4), (2, 4), (3, 4), (4, 5), (5, 5),
            (0, 5), (1, 5), (2, 5), (3, 5), (4, 6), (5, 6),
            (0, 6), (1, 6), (2, 6), (3, 6), (4, 7), (5, 7),
            (0, 7), (1, 7), (2, 7), (3, 7), (4, 8), (5, 8),
            (0, 8), (1, 8), (2, 8), (3, 8), (4, 9), (5, 9),
            (0, 9), (1, 9), (2, 9), (3, 9), (4, 10), (5, 10),
            (0, 10), (1, 10), (2, 10), (3, 10), (4, 11), (5, 11),
            (0, 11), (1, 11), (2, 11), (3, 11), (-1, -1), (-1, -1),
            (0, 12), (1, 12), (2, 12), (-1, -1), (-1, -1), (-1, -1),
            (0, 13), (1, 13), (2, 13), (3, 13), (4, 12), (5, 12),
            (-1, -1), (-1, -1), (-1, -1), (-1, -1), (4, 13), (5, 13),
            (0, 14), (1, 14), (2, 14), (3, 14), (4, 14), (5, 14),
            (0, 15), (1, 15), (2, 15), (3, 15), (4, 15), (5, 15),
            (0, 16), (1, 16), (2, 16), (3, 16), (4, 16), (5, 16),
            (0, 17), (1, 17), (2, 17), (-1, -1), (4, 17),
        };
        var leds = new List<IbpLed>(slots.Length);
        for (var slot = 0; slot < slots.Length; slot++)
        {
            if (slots[slot].R < 0) continue;
            leds.Add(new IbpLed(slots[slot].C, slots[slot].R, slot));
        }
        return leds.ToArray();
    }

    // MEK 4: a full 21 × 6 matrix, one wire slot per cell, column-major.
    private static IbpLed[] Mek4Leds()
    {
        var leds = new IbpLed[Mek4WireSlots];
        for (var slot = 0; slot < Mek4WireSlots; slot++)
        {
            leds[slot] = new IbpLed(slot / 6, slot % 6, slot);
        }
        return leds;
    }

    private static IbpLed[] Sequential((int C, int R)[] cells)
    {
        var leds = new IbpLed[cells.Length];
        for (var i = 0; i < cells.Length; i++)
        {
            leds[i] = new IbpLed(cells[i].C, cells[i].R, i);
        }
        return leds;
    }
}
