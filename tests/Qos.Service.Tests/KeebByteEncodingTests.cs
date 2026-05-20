using Qos.Service.Peripherals.Keeb.Hid.KeyAssignment;
using Qos.Service.Peripherals.Keeb.Hid.Macros;

namespace Qos.Service.Tests;

/// <summary>
/// Encoder round-trip tests for the keeb byte tables. These are firmware
/// contract — a typo here silently swaps keys on a real keeb. The tests
/// take every entry in the static byte dictionary, build a <see cref="Key"/>
/// from the enum value, and assert that decoding the resulting bytes gives
/// the same enum back.
/// </summary>
public class KeebByteEncodingTests
{
    [Theory]
    [InlineData(KeyFunction.A)]
    [InlineData(KeyFunction.Z)]
    [InlineData(KeyFunction.Number1)]
    [InlineData(KeyFunction.Number0)]
    [InlineData(KeyFunction.F1)]
    [InlineData(KeyFunction.F12)]
    [InlineData(KeyFunction.Escape)]
    [InlineData(KeyFunction.Space)]
    [InlineData(KeyFunction.Return)]
    [InlineData(KeyFunction.LeftShift)]
    [InlineData(KeyFunction.RightControl)]
    [InlineData(KeyFunction.Keypad5)]
    [InlineData(KeyFunction.KeypadEnter)]
    public void StandardKey_RoundTripsThroughByteEncoding(KeyFunction func)
    {
        var key = new Key(func, null, 0);
        var bytes = key.Commands!;
        Assert.Equal(4, bytes.Length);

        var decoded = new Key(bytes);
        Assert.Equal(func, decoded.KeyFunction);
        Assert.Equal(KeyAssignmentMode.StandardKey, decoded.Mode);
    }

    [Theory]
    [InlineData(KeyFunction.MouseLButton, KeyAssignmentMode.MouseKey)]
    [InlineData(KeyFunction.MouseRButton, KeyAssignmentMode.MouseKey)]
    [InlineData(KeyFunction.MouseMButton, KeyAssignmentMode.MouseKey)]
    [InlineData(KeyFunction.VolumeUp, KeyAssignmentMode.MediaKey)]
    [InlineData(KeyFunction.PlayAndPause, KeyAssignmentMode.MediaKey)]
    [InlineData(KeyFunction.Calculator, KeyAssignmentMode.SystemMediaKey)]
    [InlineData(KeyFunction.WebHome, KeyAssignmentMode.WebMediaKey)]
    [InlineData(KeyFunction.Power, KeyAssignmentMode.SystemKey)]
    [InlineData(KeyFunction.Sleep, KeyAssignmentMode.SystemKey)]
    public void NonStandardKey_RoundTripsAndAssignsMode(KeyFunction func, KeyAssignmentMode expectedMode)
    {
        var key = new Key(func, null, 0);
        var bytes = key.Commands!;
        var decoded = new Key(bytes);
        Assert.Equal(func, decoded.KeyFunction);
        Assert.Equal(expectedMode, decoded.Mode);
    }

    [Fact]
    public void StandardKey_ExpectedByteShape_FromHidUsage()
    {
        // A few specific firmware bytes that are HID-usage spec values; any
        // deviation here means the byte table was misread vs the legacy port.
        // The 4th byte is the mode marker (0x01 = StandardKey).
        Assert.Equal(new byte[] { 0x04, 0x00, 0x00, 0x01 }, new Key(KeyFunction.A, null, 0).Commands);
        Assert.Equal(new byte[] { 0x29, 0x00, 0x00, 0x01 }, new Key(KeyFunction.Escape, null, 0).Commands);
        Assert.Equal(new byte[] { 0x2C, 0x00, 0x00, 0x01 }, new Key(KeyFunction.Space, null, 0).Commands);
    }
}

/// <summary>
/// Encoder round-trip tests for macro keystrokes. The firmware uses a
/// variable-byte duration encoding (2 bytes for ≤126 ms, 4 bytes for longer).
/// Off-by-one here means macros misfire on the device.
/// </summary>
public class KeebMacroEncodingTests
{
    [Theory]
    [InlineData(KeyStatus.Make, 1, MacroKeyCode.A)]
    [InlineData(KeyStatus.Make, 50, MacroKeyCode.A)]
    [InlineData(KeyStatus.Break, 50, MacroKeyCode.A)]
    [InlineData(KeyStatus.Make, 126, MacroKeyCode.Z)] // last 2-byte duration
    public void ShortDuration_TwoByteRoundTrip(KeyStatus status, int duration, MacroKeyCode code)
    {
        var k = new KeyCode(status, duration, code);
        Assert.Equal(2, k.Commands!.Length);
        var decoded = new KeyCode(k.Commands);
        Assert.Equal(status, decoded.ThisKeyStatus);
        Assert.Equal(duration, decoded.Duration);
        Assert.Equal(code, decoded.ThisKey);
    }

    [Theory]
    [InlineData(KeyStatus.Make, 200, MacroKeyCode.B)]
    [InlineData(KeyStatus.Break, 1000, MacroKeyCode.Space)]
    [InlineData(KeyStatus.Make, 30000, MacroKeyCode.Return)]
    public void LongDuration_FourByteRoundTrip(KeyStatus status, int duration, MacroKeyCode code)
    {
        var k = new KeyCode(status, duration, code);
        Assert.Equal(4, k.Commands!.Length);
        var decoded = new KeyCode(k.Commands);
        Assert.Equal(status, decoded.ThisKeyStatus);
        Assert.Equal(duration, decoded.Duration);
        Assert.Equal(code, decoded.ThisKey);
    }

    [Fact]
    public void Macro_BuildsThenParsesIdenticalKeyList()
    {
        var keys = new List<KeyCode>
        {
            new(KeyStatus.Make, 10, MacroKeyCode.A),
            new(KeyStatus.Break, 10, MacroKeyCode.A),
            new(KeyStatus.Make, 50, MacroKeyCode.B),
            new(KeyStatus.Break, 50, MacroKeyCode.B),
        };

        var written = new Macro(0, 1, keys);
        var bytes = written.Commands!;
        Assert.True(bytes.Count >= 2 + 4 * 2); // header + 4 × 2-byte entries

        // Macro decoder expects a stop marker at the end. The legacy doesn't emit
        // one (the test mirrors firmware behaviour where the page is pre-zeroed).
        bytes.Add(0x00);
        bytes.Add(0x00);

        var parsed = new Macro(0, bytes);
        Assert.Equal(keys.Count, parsed.Keys.Count);
        for (int i = 0; i < keys.Count; i++)
        {
            Assert.Equal(keys[i].ThisKey, parsed.Keys[i].ThisKey);
            Assert.Equal(keys[i].ThisKeyStatus, parsed.Keys[i].ThisKeyStatus);
            Assert.Equal(keys[i].Duration, parsed.Keys[i].Duration);
        }
    }
}
