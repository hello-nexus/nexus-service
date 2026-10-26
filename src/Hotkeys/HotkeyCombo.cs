using System;
using System.Collections.Generic;

namespace Nexus.Service.Hotkeys;

/// <summary>
/// A parsed global hotkey combo. Canonical text is
/// <c>Ctrl+Alt+Shift+Meta+&lt;key&gt;</c> with the modifiers in that order, only
/// those present, and the key as a lowercase deck token (the tokens
/// DeckActionExecutor.CanonicalKey and nexus-web's HotkeyInput agree on).
/// Meta is Cmd on macOS and the Windows key on Windows.
/// </summary>
public readonly record struct HotkeyCombo(bool Ctrl, bool Alt, bool Shift, bool Meta, string Key)
{
    /// <summary>The key tokens a combo may use.</summary>
    private static readonly HashSet<string> NamedKeys = new(StringComparer.Ordinal)
    {
        "space", "enter", "tab", "escape", "backspace", "delete", "insert", "home", "end",
        "pageup", "pagedown", "up", "down", "left", "right", "printscreen",
        ".", ",", "/", ";", "'", "[", "]", "\\", "-", "=", "`",
    };

    /// <summary>
    /// Parses and validates a combo. Accepts any case, any modifier order and the
    /// aliases the deck hotkey action accepts. Null when the text is empty, has no
    /// or more than one key token, an unknown token, or would hijack plain typing
    /// (no Ctrl, Alt or Meta, unless the key is a function key).
    /// </summary>
    public static HotkeyCombo? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        bool ctrl = false, alt = false, shift = false, meta = false;
        string? key = null;
        foreach (var raw in text.Split('+'))
        {
            var tok = raw.Trim().ToLowerInvariant();
            if (tok.Length == 0)
            {
                // "ctrl++" style: the key is the plus sign, which no token covers.
                return null;
            }
            switch (tok)
            {
                case "ctrl":
                case "control":
                    ctrl = true;
                    continue;
                case "alt":
                case "option":
                case "opt":
                    alt = true;
                    continue;
                case "shift":
                    shift = true;
                    continue;
                case "meta":
                case "cmd":
                case "command":
                case "win":
                case "super":
                    meta = true;
                    continue;
            }
            var canonical = CanonicalKey(tok);
            if (canonical is null || key is not null)
            {
                return null;
            }
            key = canonical;
        }
        if (key is null)
        {
            return null;
        }
        if (!ctrl && !alt && !meta && !IsFunctionKey(key))
        {
            return null;
        }
        return new HotkeyCombo(ctrl, alt, shift, meta, key);
    }

    /// <summary>The canonical text for <paramref name="text"/>, or null when it does not parse.</summary>
    public static string? Canonicalize(string? text) => TryParse(text)?.ToString();

    public override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        if (Ctrl)
        {
            sb.Append("Ctrl+");
        }
        if (Alt)
        {
            sb.Append("Alt+");
        }
        if (Shift)
        {
            sb.Append("Shift+");
        }
        if (Meta)
        {
            sb.Append("Meta+");
        }
        return sb.Append(Key).ToString();
    }

    private static bool IsFunctionKey(string key) => key.Length >= 2 && key[0] == 'f' && char.IsAsciiDigit(key[1]);

    private static string? CanonicalKey(string tok)
    {
        if (tok.Length == 1 && (char.IsAsciiLetterLower(tok[0]) || char.IsAsciiDigit(tok[0])))
        {
            return tok;
        }
        if (tok.Length is 2 or 3 && tok[0] == 'f' && int.TryParse(tok.AsSpan(1), out var fn) && fn is >= 1 and <= 24
            && tok[1] != '0' && tok[1] != '+' && tok[1] != '-')
        {
            return "f" + fn;
        }
        var alias = tok switch
        {
            "esc" => "escape",
            "return" => "enter",
            "del" => "delete",
            _ => tok,
        };
        return NamedKeys.Contains(alias) ? alias : null;
    }

    /// <summary>
    /// macOS virtual key code (ANSI layout, Carbon kVK_*) for <paramref name="key"/>, or
    /// null when the key has no mac equivalent (f21 to f24, printscreen).
    /// </summary>
    public static int? MacKeyCode(string key) => MacKeyCodes.TryGetValue(key, out var code) ? code : null;

    /// <summary>Windows virtual key code for <paramref name="key"/>, or null for an unknown token.</summary>
    public static int? WindowsVirtualKey(string key)
    {
        if (key.Length == 1 && char.IsAsciiLetterLower(key[0]))
        {
            return char.ToUpperInvariant(key[0]);
        }
        if (key.Length == 1 && char.IsAsciiDigit(key[0]))
        {
            return key[0];
        }
        if (key.Length is 2 or 3 && key[0] == 'f' && int.TryParse(key.AsSpan(1), out var fn) && fn is >= 1 and <= 24)
        {
            return 0x70 + fn - 1;
        }
        return WindowsKeyCodes.TryGetValue(key, out var vk) ? vk : null;
    }

    /// <summary>Carbon modifier mask (cmdKey, shiftKey, optionKey, controlKey).</summary>
    public int MacModifiers => (Meta ? 0x100 : 0) | (Shift ? 0x200 : 0) | (Alt ? 0x800 : 0) | (Ctrl ? 0x1000 : 0);

    /// <summary>RegisterHotKey fsModifiers (MOD_ALT, MOD_CONTROL, MOD_SHIFT, MOD_WIN) plus MOD_NOREPEAT so a held key fires once.</summary>
    public int WindowsModifiers => (Alt ? 1 : 0) | (Ctrl ? 2 : 0) | (Shift ? 4 : 0) | (Meta ? 8 : 0) | 0x4000;

    private static readonly Dictionary<string, int> MacKeyCodes = new(StringComparer.Ordinal)
    {
        ["a"] = 0x00, ["s"] = 0x01, ["d"] = 0x02, ["f"] = 0x03, ["h"] = 0x04, ["g"] = 0x05,
        ["z"] = 0x06, ["x"] = 0x07, ["c"] = 0x08, ["v"] = 0x09, ["b"] = 0x0B, ["q"] = 0x0C,
        ["w"] = 0x0D, ["e"] = 0x0E, ["r"] = 0x0F, ["y"] = 0x10, ["t"] = 0x11,
        ["1"] = 0x12, ["2"] = 0x13, ["3"] = 0x14, ["4"] = 0x15, ["6"] = 0x16, ["5"] = 0x17,
        ["="] = 0x18, ["9"] = 0x19, ["7"] = 0x1A, ["-"] = 0x1B, ["8"] = 0x1C, ["0"] = 0x1D,
        ["]"] = 0x1E, ["o"] = 0x1F, ["u"] = 0x20, ["["] = 0x21, ["i"] = 0x22, ["p"] = 0x23,
        ["enter"] = 0x24, ["l"] = 0x25, ["j"] = 0x26, ["'"] = 0x27, ["k"] = 0x28, [";"] = 0x29,
        ["\\"] = 0x2A, [","] = 0x2B, ["/"] = 0x2C, ["n"] = 0x2D, ["m"] = 0x2E, ["."] = 0x2F,
        ["tab"] = 0x30, ["space"] = 0x31, ["`"] = 0x32, ["backspace"] = 0x33, ["escape"] = 0x35,
        ["f17"] = 0x40, ["f18"] = 0x4F, ["f19"] = 0x50, ["f20"] = 0x5A,
        ["f5"] = 0x60, ["f6"] = 0x61, ["f7"] = 0x62, ["f3"] = 0x63, ["f8"] = 0x64, ["f9"] = 0x65,
        ["f11"] = 0x67, ["f13"] = 0x69, ["f16"] = 0x6A, ["f14"] = 0x6B, ["f10"] = 0x6D,
        ["f12"] = 0x6F, ["f15"] = 0x71, ["insert"] = 0x72, ["home"] = 0x73, ["pageup"] = 0x74,
        ["delete"] = 0x75, ["f4"] = 0x76, ["end"] = 0x77, ["f2"] = 0x78, ["pagedown"] = 0x79,
        ["f1"] = 0x7A, ["left"] = 0x7B, ["right"] = 0x7C, ["down"] = 0x7D, ["up"] = 0x7E,
    };

    private static readonly Dictionary<string, int> WindowsKeyCodes = new(StringComparer.Ordinal)
    {
        ["backspace"] = 0x08, ["tab"] = 0x09, ["enter"] = 0x0D, ["escape"] = 0x1B, ["space"] = 0x20,
        ["pageup"] = 0x21, ["pagedown"] = 0x22, ["end"] = 0x23, ["home"] = 0x24,
        ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
        ["printscreen"] = 0x2C, ["insert"] = 0x2D, ["delete"] = 0x2E,
        [";"] = 0xBA, ["="] = 0xBB, [","] = 0xBC, ["-"] = 0xBD, ["."] = 0xBE, ["/"] = 0xBF,
        ["`"] = 0xC0, ["["] = 0xDB, ["\\"] = 0xDC, ["]"] = 0xDD, ["'"] = 0xDE,
    };
}
