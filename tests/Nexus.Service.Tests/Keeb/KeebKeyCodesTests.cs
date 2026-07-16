using Nexus.Service.Peripherals.Hyte.Keeb;
using Xunit;

namespace Nexus.Service.Tests.Keeb;

/// <summary>
/// Golden-vector coverage for the HYTE Keeb TKL keycode tables, transcribed from
/// hyte-refs/hyte-documents/firmware-protocol/Keeb/KeyCodeDoc/keyassignment.md.
/// Every byte drives real firmware, so each case asserts the exact 4-byte matrix
/// code (or single HID usage byte) against the doc.
/// </summary>
public class KeebKeyCodesTests
{
    // ── MatrixCode: one vector per category ──

    [Fact]
    public void StandardKey_letter_A_is_04_00_00_01()
        => Assert.Equal(new byte[] { 0x04, 0x00, 0x00, 0x01 }, KeebKeyCodes.MatrixCode("StandardKey", "A", null));

    [Fact]
    public void StandardKey_letter_Z_is_1D_00_00_01()
        => Assert.Equal(new byte[] { 0x1D, 0x00, 0x00, 0x01 }, KeebKeyCodes.MatrixCode("StandardKey", "Z", null));

    [Fact]
    public void StandardKey_Number1_is_1E_00_00_01()
        => Assert.Equal(new byte[] { 0x1E, 0x00, 0x00, 0x01 }, KeebKeyCodes.MatrixCode("StandardKey", "Number1", null));

    [Fact]
    public void StandardKey_Number0_is_27_00_00_01()
        => Assert.Equal(new byte[] { 0x27, 0x00, 0x00, 0x01 }, KeebKeyCodes.MatrixCode("StandardKey", "Number0", null));

    [Fact]
    public void StandardKey_F5_is_3E_00_00_01()
        => Assert.Equal(new byte[] { 0x3E, 0x00, 0x00, 0x01 }, KeebKeyCodes.MatrixCode("StandardKey", "F5", null));

    [Fact]
    public void StandardKey_F13_is_68_00_00_01()
        => Assert.Equal(new byte[] { 0x68, 0x00, 0x00, 0x01 }, KeebKeyCodes.MatrixCode("StandardKey", "F13", null));

    [Fact]
    public void StandardKey_LeftShift_modifier_is_E1_00_00_01()
        => Assert.Equal(new byte[] { 0xE1, 0x00, 0x00, 0x01 }, KeebKeyCodes.MatrixCode("StandardKey", "LeftShift", null));

    [Fact]
    public void StandardKey_NonUsBackslash_is_Europe2_64()
        => Assert.Equal(new byte[] { 0x64, 0x00, 0x00, 0x01 }, KeebKeyCodes.MatrixCode("StandardKey", "NonUsBackslash", null));

    [Fact]
    public void StandardKey_None_is_zeros()
        => Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00 }, KeebKeyCodes.MatrixCode("StandardKey", "None", null));

    [Fact]
    public void StandardKey_PassThrough_is_the_transparent_category()
        => Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0xF8 }, KeebKeyCodes.MatrixCode("StandardKey", "PassThrough", null));

    [Fact]
    public void MediaKey_PlayAndPause_is_CD_00_00_03()
        => Assert.Equal(new byte[] { 0xCD, 0x00, 0x00, 0x03 }, KeebKeyCodes.MatrixCode("MediaKey", "PlayAndPause", null));

    [Fact]
    public void MediaKey_VolumeUp_is_E9_00_00_03()
        => Assert.Equal(new byte[] { 0xE9, 0x00, 0x00, 0x03 }, KeebKeyCodes.MatrixCode("MediaKey", "VolumeUp", null));

    [Fact]
    public void SystemMediaKey_Calculator_is_92_01_00_03()
        => Assert.Equal(new byte[] { 0x92, 0x01, 0x00, 0x03 }, KeebKeyCodes.MatrixCode("SystemMediaKey", "Calculator", null));

    [Fact]
    public void WebMediaKey_WebSearch_is_21_02_00_03()
        => Assert.Equal(new byte[] { 0x21, 0x02, 0x00, 0x03 }, KeebKeyCodes.MatrixCode("WebMediaKey", "WebSearch", null));

    [Fact]
    public void MouseKey_LButton_is_F0_00_00_02()
        => Assert.Equal(new byte[] { 0xF0, 0x00, 0x00, 0x02 }, KeebKeyCodes.MatrixCode("MouseKey", "MouseLButton", null));

    [Fact]
    public void MouseKey_WheelUp_carries_value_in_byte1()
        => Assert.Equal(new byte[] { 0xF5, 0x03, 0x00, 0x02 }, KeebKeyCodes.MatrixCode("MouseKey", "MouseWheelUp", 3));

    [Fact]
    public void MouseKey_button_ignores_value()
        => Assert.Equal(new byte[] { 0xF1, 0x00, 0x00, 0x02 }, KeebKeyCodes.MatrixCode("MouseKey", "MouseRButton", 7));

    [Fact]
    public void SystemKey_Power_is_01_00_00_04()
        => Assert.Equal(new byte[] { 0x01, 0x00, 0x00, 0x04 }, KeebKeyCodes.MatrixCode("SystemKey", "Power", null));

    [Fact]
    public void MacroKey_Macro3_normal_is_index2_type1_cat05()
        => Assert.Equal(new byte[] { 0x02, 0x00, 0x01, 0x05 }, KeebKeyCodes.MatrixCode("MacroKey", "Macro3", null));

    [Fact]
    public void MacroKey_Macro16_hold_and_play_is_index15_type3()
        => Assert.Equal(new byte[] { 0x0F, 0x00, 0x03, 0x05 }, KeebKeyCodes.MatrixCode("MacroKey", "Macro16", 3));

    [Fact]
    public void MacroKey_Macro1_repeat_is_index0_type2()
        => Assert.Equal(new byte[] { 0x00, 0x00, 0x02, 0x05 }, KeebKeyCodes.MatrixCode("MacroKey", "Macro1", 2));

    [Fact]
    public void LayerKey_MOSwitch_input2_is_15_02_00_F0()
        => Assert.Equal(new byte[] { 0x15, 0x02, 0x00, 0xF0 }, KeebKeyCodes.MatrixCode("LayerKey", "MOSwitch", 2));

    [Fact]
    public void LayerKey_TGSwitch_default_layer_is_16_00_00_F0()
        => Assert.Equal(new byte[] { 0x16, 0x00, 0x00, 0xF0 }, KeebKeyCodes.MatrixCode("LayerKey", "TGSwitch", null));

    [Fact]
    public void RGBKey_unknown_function_is_zeros()
        => Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00 }, KeebKeyCodes.MatrixCode("RGBKey", "BrightnessUp", null));

    [Fact]
    public void RGBKey_EffectLoop_is_the_vendor_0A_code()
        => Assert.Equal(new byte[] { 0x03, 0x00, 0x00, 0x0A }, KeebKeyCodes.MatrixCode("RGBKey", "RGBEffectLoop", null));

    [Fact]
    public void SoftwareKey_is_zeros_software_handled()
        => Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00 }, KeebKeyCodes.MatrixCode("SoftwareKey", "Whatever", null));

    [Fact]
    public void Unknown_mode_is_zeros()
        => Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00 }, KeebKeyCodes.MatrixCode("Nonsense", "Whatever", 9));

    [Fact]
    public void Unknown_func_is_zeros()
        => Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00 }, KeebKeyCodes.MatrixCode("StandardKey", "NotAKey", null));

    // ── MacroHid: JS KeyboardEvent.code → HID usage byte ──

    [Theory]
    [InlineData("KeyA", 0x04)]
    [InlineData("KeyZ", 0x1D)]
    [InlineData("Digit1", 0x1E)]
    [InlineData("Digit0", 0x27)]
    [InlineData("Enter", 0x28)]
    [InlineData("Space", 0x2C)]
    [InlineData("F5", 0x3E)]
    [InlineData("ShiftLeft", 0xE1)]
    [InlineData("ArrowUp", 0x52)]
    [InlineData("MetaRight", 0xE7)]
    [InlineData("NumpadDecimal", 0x63)]
    [InlineData("ContextMenu", 0x65)]
    [InlineData("NotARealCode", 0x00)]
    public void MacroHid_maps_event_code_to_hid_usage(string code, byte expected)
        => Assert.Equal(expected, KeebKeyCodes.MacroHid(code));
}
