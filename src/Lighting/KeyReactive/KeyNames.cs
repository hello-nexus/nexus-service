using System;
using System.Collections.Generic;

namespace Nexus.Service.Lighting.KeyReactive;

/// <summary>
/// Key names for key reactions: OpenRGB's English LED names without the "Key:"
/// prefix. Presses arrive as scan codes, which name the physical key whatever
/// the OS layout is.
/// </summary>
public static class KeyNames
{
    private const string OpenRgbPrefix = "Key:";

    /// <summary>Scan codes (Set 1 make code, plus 0xE000 for an E0-prefixed key) to canonical names.</summary>
    private static readonly Dictionary<int, string> ScanToName = BuildScanTable();

    /// <summary>Other names the same physical key carries on some boards (ISO twins, OpenRGB's localized labels), tried after the canonical one.</summary>
    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.Ordinal)
    {
        ["\\"] = new[] { "\\ (ANSI)", "#", "' *" },
        ["\\ (ISO)"] = new[] { "< >" },
        ["Enter"] = new[] { "Enter (ISO)" },
        ["`"] = new[] { "§ ½", "^", "´ `" },
        ["-"] = new[] { "ß", "+ ?", "- _" },
        ["="] = new[] { "´ `" },
        ["["] = new[] { "Ü", "Å" },
        ["]"] = new[] { "+", "¨ ^" },
        [";"] = new[] { "Ö", "Ö Æ", "Ñ" },
        ["'"] = new[] { "Ä", "Ä Ø" },
        ["Pause/Break"] = new[] { "Pause" },
    };

    /// <summary>Canonical name for a raw keyboard event, or null for a code with no LED-bearing key.</summary>
    /// <param name="makeCode">Set 1 make code as Raw Input reports it.</param>
    /// <param name="e0">The RI_KEY_E0 flag.</param>
    /// <param name="virtualKey">The event's VKey; disambiguates Pause and Num Lock, which share make codes.</param>
    public static string? FromScanCode(int makeCode, bool e0, int virtualKey = 0)
    {
        // Pause arrives as E1 1D 45 and Num Lock as 45: the make codes collide
        // with Left Control and Num Lock, so the virtual key decides.
        if (virtualKey == 0x13) return "Pause/Break";
        if (virtualKey == 0x90) return "Num Lock";
        return ScanToName.TryGetValue(makeCode | (e0 ? 0xE000 : 0), out var name) ? name : null;
    }

    /// <summary>The names to try for a press, canonical first.</summary>
    public static IEnumerable<string> Candidates(string canonical)
    {
        yield return canonical;
        if (Aliases.TryGetValue(canonical, out var more))
        {
            foreach (var alias in more) yield return alias;
        }
    }

    /// <summary>An LED name as reported by the hardware, reduced to the key-name space; null when the LED is not a key.</summary>
    public static string? Normalize(string? ledName)
    {
        if (string.IsNullOrWhiteSpace(ledName)) return null;
        var name = ledName.Trim();
        if (name.StartsWith(OpenRgbPrefix, StringComparison.Ordinal))
        {
            name = name.Substring(OpenRgbPrefix.Length).Trim();
        }
        return name.Length == 0 ? null : name;
    }

    /// <summary>True when <paramref name="name"/> is a canonical name a scan code can produce.</summary>
    public static bool IsKnown(string name) => ScanToName.ContainsValue(name);

    /// <summary>Key positions on a full-size ANSI board in key units (function row at y 0), for boards whose LEDs carry no names.</summary>
    public static readonly IReadOnlyDictionary<string, (float X, float Y)> NominalPositions = BuildNominal();

    /// <summary>Right edge of the nominal layout's main block plus navigation cluster (a tenkeyless board).</summary>
    public const float NominalTklWidth = 17.25f;

    /// <summary>Right edge of the nominal layout including the number pad.</summary>
    public const float NominalFullWidth = 21.5f;

    /// <summary>Bottom row of the nominal layout.</summary>
    public const float NominalBottomRow = 5f;

    private static Dictionary<int, string> BuildScanTable()
    {
        var t = new Dictionary<int, string>
        {
            [0x01] = "Escape",
            [0x0C] = "-",
            [0x0D] = "=",
            [0x0E] = "Backspace",
            [0x0F] = "Tab",
            [0x1A] = "[",
            [0x1B] = "]",
            [0x1C] = "Enter",
            [0x1D] = "Left Control",
            [0x27] = ";",
            [0x28] = "'",
            [0x29] = "`",
            [0x2A] = "Left Shift",
            [0x2B] = "\\",
            [0x33] = ",",
            [0x34] = ".",
            [0x35] = "/",
            [0x36] = "Right Shift",
            [0x37] = "Number Pad *",
            [0x38] = "Left Alt",
            [0x39] = "Space",
            [0x3A] = "Caps Lock",
            [0x45] = "Num Lock",
            [0x46] = "Scroll Lock",
            [0x47] = "Number Pad 7",
            [0x48] = "Number Pad 8",
            [0x49] = "Number Pad 9",
            [0x4A] = "Number Pad -",
            [0x4B] = "Number Pad 4",
            [0x4C] = "Number Pad 5",
            [0x4D] = "Number Pad 6",
            [0x4E] = "Number Pad +",
            [0x4F] = "Number Pad 1",
            [0x50] = "Number Pad 2",
            [0x51] = "Number Pad 3",
            [0x52] = "Number Pad 0",
            [0x53] = "Number Pad .",
            [0x56] = "\\ (ISO)",
            [0x57] = "F11",
            [0x58] = "F12",
            [0xE01C] = "Number Pad Enter",
            [0xE01D] = "Right Control",
            [0xE035] = "Number Pad /",
            [0xE037] = "Print Screen",
            [0xE038] = "Right Alt",
            [0xE046] = "Pause/Break",
            [0xE047] = "Home",
            [0xE048] = "Up Arrow",
            [0xE049] = "Page Up",
            [0xE04B] = "Left Arrow",
            [0xE04D] = "Right Arrow",
            [0xE04F] = "End",
            [0xE050] = "Down Arrow",
            [0xE051] = "Page Down",
            [0xE052] = "Insert",
            [0xE053] = "Delete",
            [0xE05B] = "Left Windows",
            [0xE05C] = "Right Windows",
            [0xE05D] = "Menu",
            [0xE020] = "Media Mute",
            [0xE02E] = "Media Volume -",
            [0xE030] = "Media Volume +",
            [0xE022] = "Media Play/Pause",
            [0xE024] = "Media Stop",
            [0xE010] = "Media Previous",
            [0xE019] = "Media Next",
        };
        // Digit row: 1..9 then 0.
        for (var i = 0; i < 10; i++) t[0x02 + i] = i == 9 ? "0" : ((char)('1' + i)).ToString();
        AddRun(t, 0x10, "QWERTYUIOP");
        AddRun(t, 0x1E, "ASDFGHJKL");
        AddRun(t, 0x2C, "ZXCVBNM");
        for (var i = 0; i < 10; i++) t[0x3B + i] = $"F{i + 1}";
        for (var i = 0; i < 11; i++) t[0x64 + i] = $"F{13 + i}";
        t[0x76] = "F24";
        return t;
    }

    private static void AddRun(Dictionary<int, string> t, int first, string letters)
    {
        for (var i = 0; i < letters.Length; i++) t[first + i] = letters[i].ToString();
    }

    private static Dictionary<string, (float, float)> BuildNominal()
    {
        var p = new Dictionary<string, (float, float)>(StringComparer.Ordinal)
        {
            ["Escape"] = (0f, 0f),
            ["Print Screen"] = (15.25f, 0f),
            ["Scroll Lock"] = (16.25f, 0f),
            ["Pause/Break"] = (17.25f, 0f),
            ["`"] = (0f, 1f),
            ["-"] = (11f, 1f),
            ["="] = (12f, 1f),
            ["Backspace"] = (13.5f, 1f),
            ["Insert"] = (15.25f, 1f),
            ["Home"] = (16.25f, 1f),
            ["Page Up"] = (17.25f, 1f),
            ["Num Lock"] = (18.5f, 1f),
            ["Number Pad /"] = (19.5f, 1f),
            ["Number Pad *"] = (20.5f, 1f),
            ["Number Pad -"] = (21.5f, 1f),
            ["Tab"] = (0.25f, 2f),
            ["["] = (11.5f, 2f),
            ["]"] = (12.5f, 2f),
            ["\\"] = (13.75f, 2f),
            ["Delete"] = (15.25f, 2f),
            ["End"] = (16.25f, 2f),
            ["Page Down"] = (17.25f, 2f),
            ["Number Pad 7"] = (18.5f, 2f),
            ["Number Pad 8"] = (19.5f, 2f),
            ["Number Pad 9"] = (20.5f, 2f),
            ["Number Pad +"] = (21.5f, 2.5f),
            ["Caps Lock"] = (0.4f, 3f),
            [";"] = (10.75f, 3f),
            ["'"] = (11.75f, 3f),
            ["Enter"] = (13.4f, 3f),
            ["Number Pad 4"] = (18.5f, 3f),
            ["Number Pad 5"] = (19.5f, 3f),
            ["Number Pad 6"] = (20.5f, 3f),
            ["Left Shift"] = (0.6f, 4f),
            ["\\ (ISO)"] = (1.25f, 4f),
            [","] = (9.25f, 4f),
            ["."] = (10.25f, 4f),
            ["/"] = (11.25f, 4f),
            ["Right Shift"] = (13.1f, 4f),
            ["Up Arrow"] = (16.25f, 4f),
            ["Number Pad 1"] = (18.5f, 4f),
            ["Number Pad 2"] = (19.5f, 4f),
            ["Number Pad 3"] = (20.5f, 4f),
            ["Number Pad Enter"] = (21.5f, 4.5f),
            ["Left Control"] = (0.25f, 5f),
            ["Left Windows"] = (1.5f, 5f),
            ["Left Alt"] = (2.75f, 5f),
            ["Space"] = (6.4f, 5f),
            ["Right Alt"] = (10.25f, 5f),
            ["Right Windows"] = (11.5f, 5f),
            ["Menu"] = (12.75f, 5f),
            ["Right Control"] = (14f, 5f),
            ["Left Arrow"] = (15.25f, 5f),
            ["Down Arrow"] = (16.25f, 5f),
            ["Right Arrow"] = (17.25f, 5f),
            ["Number Pad 0"] = (19f, 5f),
            ["Number Pad ."] = (20.5f, 5f),
        };
        // Function row in its three groups of four.
        for (var i = 0; i < 12; i++) p[$"F{i + 1}"] = (2f + i + (i / 4) * 0.5f, 0f);
        for (var i = 0; i < 10; i++) p[i == 9 ? "0" : ((char)('1' + i)).ToString()] = (1f + i, 1f);
        AddNominalRun(p, "QWERTYUIOP", 1.5f, 2f);
        AddNominalRun(p, "ASDFGHJKL", 1.75f, 3f);
        AddNominalRun(p, "ZXCVBNM", 2.25f, 4f);
        return p;
    }

    private static void AddNominalRun(Dictionary<string, (float, float)> p, string letters, float x0, float y)
    {
        for (var i = 0; i < letters.Length; i++) p[letters[i].ToString()] = (x0 + i, y);
    }
}
