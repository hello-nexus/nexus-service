using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.Hyte.Keeb;

/// <summary>
/// Key name per keeb key LED, in the key-name space of
/// <see cref="Nexus.Service.Lighting.KeyReactive.KeyNames"/>. Keyed by wire
/// slot; slots 75 and 85 are the ISO-only keys (hash, and the angle-bracket key
/// beside Left Shift). Media keys report on the consumer page, never as scan
/// codes, so they carry no name.
/// </summary>
public static class KeebKeyNames
{
    private static readonly Dictionary<int, string> ByWireSlot = new()
    {
        [0] = "Escape", [2] = "F1", [3] = "F2", [4] = "F3", [5] = "F4", [6] = "F5", [7] = "F6",
        [8] = "F7", [9] = "F8", [10] = "F9", [11] = "F10", [12] = "F11", [13] = "F12",
        [14] = "Print Screen", [15] = "Scroll Lock", [16] = "Pause/Break", [21] = "`", [22] = "1",
        [23] = "2", [24] = "3", [25] = "4", [26] = "5", [27] = "6", [28] = "7", [29] = "8",
        [30] = "9", [31] = "0", [32] = "-", [33] = "=", [34] = "Backspace", [35] = "Insert",
        [36] = "Home", [37] = "Page Up", [42] = "Tab", [43] = "Q", [44] = "W", [45] = "E",
        [46] = "R", [47] = "T", [48] = "Y", [49] = "U", [50] = "I", [51] = "O", [52] = "P",
        [53] = "[", [54] = "]", [55] = "\\", [56] = "Delete", [57] = "End", [58] = "Page Down",
        [63] = "Caps Lock", [64] = "A", [65] = "S", [66] = "D", [67] = "F", [68] = "G", [69] = "H",
        [70] = "J", [71] = "K", [72] = "L", [73] = ";", [74] = "'", [75] = "\\", [76] = "Enter",
        [84] = "Left Shift", [85] = "\\ (ISO)", [86] = "Z", [87] = "X", [88] = "C", [89] = "V",
        [90] = "B", [91] = "N", [92] = "M", [93] = ",", [94] = ".", [95] = "/",
        [97] = "Right Shift", [99] = "Up Arrow", [105] = "Left Control", [106] = "Left Windows",
        [107] = "Left Alt", [111] = "Space", [115] = "Right Alt", [116] = "Right Fn",
        [117] = "Menu", [118] = "Right Control", [119] = "Left Arrow", [120] = "Down Arrow",
        [121] = "Right Arrow",
    };

    // The key-matrix callback's (row, column) addresses a MatrixWidth-wide scan
    // matrix whose cells are the wire slots, row-major.
    private const int MatrixWidth = 21;

    /// <summary>Key name for a key-matrix callback, or null for a key with no LED name (media keys).</summary>
    public static string? FromMatrix(int row, int column) =>
        row >= 0 && column >= 0 && column < MatrixWidth
        && ByWireSlot.TryGetValue(row * MatrixWidth + column, out var name) ? name : null;

    /// <summary>Names parallel to <paramref name="keys"/>' LED order, null where a slot has none.</summary>
    public static string?[] For(KeebKeyMap keys)
    {
        var names = new string?[keys.LedCount];
        for (var i = 0; i < names.Length; i++)
        {
            names[i] = ByWireSlot.TryGetValue(keys.WireValues[i], out var name) ? name : null;
        }
        return names;
    }
}
