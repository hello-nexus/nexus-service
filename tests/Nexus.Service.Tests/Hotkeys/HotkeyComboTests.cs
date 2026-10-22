using Nexus.Service.Hotkeys;
using Xunit;

namespace Nexus.Service.Tests.Hotkeys;

public sealed class HotkeyComboTests
{
    [Theory]
    [InlineData("ctrl+shift+m", "Ctrl+Shift+m")]
    [InlineData("Meta+Alt+N", "Alt+Meta+n")]
    [InlineData("cmd+option+=", "Alt+Meta+=")]
    [InlineData("Win+Control+Left", "Ctrl+Meta+left")]
    [InlineData("shift+ctrl+alt+meta+F5", "Ctrl+Alt+Shift+Meta+f5")]
    [InlineData("ctrl+esc", "Ctrl+escape")]
    [InlineData("ctrl+return", "Ctrl+enter")]
    [InlineData("alt+del", "Alt+delete")]
    [InlineData("ctrl+,", "Ctrl+,")]
    [InlineData("ctrl+alt+`", "Ctrl+Alt+`")]
    [InlineData(" Ctrl + Alt + k ", "Ctrl+Alt+k")]
    public void Canonicalize_orders_modifiers_and_lowercases_the_key(string input, string expected)
    {
        Assert.Equal(expected, HotkeyCombo.Canonicalize(input));
    }

    [Theory]
    [InlineData("f5")]
    [InlineData("F13")]
    [InlineData("shift+f24")]
    public void Function_keys_need_no_modifier(string input)
    {
        Assert.NotNull(HotkeyCombo.TryParse(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ctrl")]
    [InlineData("ctrl+alt")]
    [InlineData("n")]
    [InlineData("shift+n")]
    [InlineData("ctrl+nope")]
    [InlineData("ctrl+a+b")]
    [InlineData("ctrl++")]
    [InlineData("ctrl+f25")]
    [InlineData("ctrl+f0")]
    [InlineData("ctrl+f01")]
    public void Invalid_combos_do_not_parse(string? input)
    {
        Assert.Null(HotkeyCombo.TryParse(input));
    }

    [Fact]
    public void Canonical_text_round_trips()
    {
        var parsed = HotkeyCombo.TryParse("ctrl+alt+shift+meta+pagedown");
        Assert.NotNull(parsed);
        Assert.Equal("Ctrl+Alt+Shift+Meta+pagedown", parsed.ToString());
        Assert.Equal(parsed, HotkeyCombo.TryParse(parsed.ToString()));
    }

    [Fact]
    public void Mac_modifiers_map_to_carbon_masks()
    {
        var combo = HotkeyCombo.TryParse("Alt+Meta+n")!.Value;
        Assert.Equal(0x100 | 0x800, combo.MacModifiers);
        Assert.Equal(0x1000 | 0x200, HotkeyCombo.TryParse("Ctrl+Shift+a")!.Value.MacModifiers);
    }

    [Fact]
    public void Windows_modifiers_map_to_register_hotkey_flags_and_never_repeat()
    {
        var combo = HotkeyCombo.TryParse("Ctrl+Alt+Shift+Meta+n")!.Value;
        Assert.Equal(1 | 2 | 4 | 8 | 0x4000, combo.WindowsModifiers);
        Assert.Equal(0x4000 | 2, HotkeyCombo.TryParse("Ctrl+n")!.Value.WindowsModifiers);
    }

    [Theory]
    [InlineData("a", 0x00)]
    [InlineData("n", 0x2D)]
    [InlineData("l", 0x25)]
    [InlineData("=", 0x18)]
    [InlineData("-", 0x1B)]
    [InlineData("0", 0x1D)]
    [InlineData("space", 0x31)]
    [InlineData("f1", 0x7A)]
    [InlineData("left", 0x7B)]
    public void Mac_key_codes(string key, int expected)
    {
        Assert.Equal(expected, HotkeyCombo.MacKeyCode(key));
    }

    [Theory]
    [InlineData("f21")]
    [InlineData("printscreen")]
    public void Keys_without_a_mac_code_report_null(string key)
    {
        Assert.Null(HotkeyCombo.MacKeyCode(key));
    }

    [Theory]
    [InlineData("a", 0x41)]
    [InlineData("n", 0x4E)]
    [InlineData("0", 0x30)]
    [InlineData("f1", 0x70)]
    [InlineData("f24", 0x87)]
    [InlineData("=", 0xBB)]
    [InlineData("-", 0xBD)]
    [InlineData("printscreen", 0x2C)]
    [InlineData("left", 0x25)]
    public void Windows_virtual_keys(string key, int expected)
    {
        Assert.Equal(expected, HotkeyCombo.WindowsVirtualKey(key));
    }

    [Fact]
    public void Every_accepted_key_token_has_a_windows_code()
    {
        foreach (var token in new[] { "space", "enter", "tab", "escape", "backspace", "delete", "insert", "home", "end",
                     "pageup", "pagedown", "up", "down", "left", "right", "printscreen", ".", ",", "/", ";", "'", "[", "]", "\\", "-", "=", "`" })
        {
            Assert.NotNull(HotkeyCombo.TryParse("ctrl+" + token));
            Assert.NotNull(HotkeyCombo.WindowsVirtualKey(token));
        }
    }
}
