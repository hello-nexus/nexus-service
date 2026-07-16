using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.Hyte.Keeb;

/// <summary>One web-layout cell: its printed-legend default and the firmware layer-table slot it occupies.</summary>
public sealed record KeebLayerCell(string Function, string Mode, int Slot);

/// <summary>
/// Web layout cell (x=row, y=index in row) → firmware layer-table slot map for
/// the Keeb TKL. Slots are 0-based indices into the 128×4-byte 0xF2 table;
/// source of truth is the vendor driver's 1-based ANSI/ISO_COMMAND_INDEX
/// (LightDancing KeyAssignmentCommon.cs), cross-verified byte-for-byte against
/// a live factory layer-0 dump (fw 1.33). Rows mirror nexus-web
/// keebLayout.ts exactly - the wire contract for /keeb/layer/{n}/key is that
/// both sides agree on (x, y).
///
/// Row 0 is the RGB-edit key above F1 (slot 1, vendor category 0x0A); row 1 is
/// the physical media strip. The board is a TKL: there is no keypad hardware,
/// so no keypad cells exist here.
/// </summary>
public static class KeebLayerMap
{
    /// <summary>Slots in the 0xF2 table (128 × 4 bytes = 512 data bytes).</summary>
    public const int SlotCount = 128;

    /// <summary>Total layers per profile (0..3).</summary>
    public const int LayerCount = 4;

    /// <summary>Firmware profiles (0..1).</summary>
    public const int ProfileCount = 2;

    private static KeebLayerCell Std(string fn, int slot) => new(fn, "StandardKey", slot);

    private static readonly KeebLayerCell[] Row0 = { new("RGBEffectLoop", "RGBKey", 1) };

    private static readonly KeebLayerCell[] Row1 =
    {
        new("Stop", "MediaKey", 77), new("ScanPreviousTrack", "MediaKey", 78),
        new("PlayAndPause", "MediaKey", 79), new("ScanNextTrack", "MediaKey", 98),
        new("Mute", "MediaKey", 100),
    };

    private static readonly KeebLayerCell[] Row2 =
    {
        Std("Escape", 0),
        Std("F1", 2), Std("F2", 3), Std("F3", 4), Std("F4", 5),
        Std("F5", 6), Std("F6", 7), Std("F7", 8), Std("F8", 9),
        Std("F9", 10), Std("F10", 11), Std("F11", 12), Std("F12", 13),
        Std("PrintScreen", 14), Std("ScrollLock", 15), Std("Pause", 16),
    };

    private static readonly KeebLayerCell[] Row3 =
    {
        Std("Backtick", 21),
        Std("Number1", 22), Std("Number2", 23), Std("Number3", 24), Std("Number4", 25),
        Std("Number5", 26), Std("Number6", 27), Std("Number7", 28), Std("Number8", 29),
        Std("Number9", 30), Std("Number0", 31),
        Std("Minus", 32), Std("Equals", 33), Std("Backspace", 34),
        Std("Insert", 35), Std("Home", 36), Std("PageUp", 37),
    };

    private static KeebLayerCell[] Row4(bool iso) => new[]
    {
        Std("Tab", 42),
        Std("Q", 43), Std("W", 44), Std("E", 45), Std("R", 46), Std("T", 47),
        Std("Y", 48), Std("U", 49), Std("I", 50), Std("O", 51), Std("P", 52),
        Std("LeftBracket", 53), Std("RightBracket", 54),
        iso ? Std("Return", 76) : Std("Backslash", 55),
        Std("Delete", 56), Std("End", 57), Std("PageDown", 58),
    };

    private static KeebLayerCell[] Row5(bool iso) => new[]
    {
        Std("CapsLock", 63),
        Std("A", 64), Std("S", 65), Std("D", 66), Std("F", 67), Std("G", 68),
        Std("H", 69), Std("J", 70), Std("K", 71), Std("L", 72),
        Std("Semicolon", 73), Std("Quote", 74),
        iso ? Std("NonUsPound", 75) : Std("Return", 76),
    };

    private static KeebLayerCell[] Row6(bool iso)
    {
        var cells = new List<KeebLayerCell> { Std("LeftShift", 84) };
        if (iso) cells.Add(Std("NonUsBackslash", 85));
        cells.AddRange(new[]
        {
            Std("Z", 86), Std("X", 87), Std("C", 88), Std("V", 89), Std("B", 90),
            Std("N", 91), Std("M", 92), Std("Comma", 93), Std("Period", 94), Std("Slash", 95),
            Std("RightShift", 97), Std("UpArrow", 99),
        });
        return cells.ToArray();
    }

    private static readonly KeebLayerCell[] Row7 =
    {
        Std("LeftControl", 105), Std("LeftGUI", 106), Std("LeftAlt", 107),
        Std("Space", 111), Std("RightAlt", 115),
        new("MOSwitch", "LayerKey", 116),
        Std("RightGUI", 117), Std("RightControl", 118),
        Std("LeftArrow", 119), Std("DownArrow", 120), Std("RightArrow", 121),
    };

    private static readonly KeebLayerCell[][] AnsiRows =
        { Row0, Row1, Row2, Row3, Row4(iso: false), Row5(iso: false), Row6(iso: false), Row7 };

    private static readonly KeebLayerCell[][] IsoRows =
        { Row0, Row1, Row2, Row3, Row4(iso: true), Row5(iso: true), Row6(iso: true), Row7 };

    public static IReadOnlyList<KeebLayerCell[]> Rows(string layout)
        => string.Equals(layout, "ISO", StringComparison.OrdinalIgnoreCase) ? IsoRows : AnsiRows;

    /// <summary>Cell at web coordinates (x=row, y=index), or false when no such key exists.</summary>
    public static bool TryGetCell(string layout, int x, int y, out KeebLayerCell cell)
    {
        var rows = Rows(layout);
        if (x >= 0 && x < rows.Count && y >= 0 && y < rows[x].Length)
        {
            cell = rows[x][y];
            return true;
        }
        cell = null!;
        return false;
    }
}
