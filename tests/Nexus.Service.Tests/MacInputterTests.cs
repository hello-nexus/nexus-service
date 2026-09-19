using Nexus.Service.Peripherals.Keeb;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Covers the web-key-name → macOS ANSI virtual-keycode mapping (the error-prone
/// part of MacInputter). 0xFFFF marks names with no CGKeyboardEvent equivalent on
/// macOS (Insert/NumLock/.../media keys), which the inputter skips.
/// </summary>
public class MacInputterTests
{
    [Theory]
    [InlineData("KeyA", 0)]
    [InlineData("KeyZ", 6)]
    [InlineData("KeyM", 46)]
    [InlineData("Digit0", 29)]
    [InlineData("Digit1", 18)]
    [InlineData("Digit9", 25)]
    [InlineData("F1", 122)]
    [InlineData("F10", 109)]
    [InlineData("F12", 111)]
    [InlineData("F13", 105)]
    [InlineData("F20", 90)]
    [InlineData("Space", 49)]
    [InlineData("Enter", 36)]
    [InlineData("Escape", 53)]
    [InlineData("ArrowUp", 126)]
    [InlineData("CapsLock", 57)]
    [InlineData("Period", 47)]
    [InlineData("Comma", 43)]
    [InlineData("Slash", 44)]
    [InlineData("Semicolon", 41)]
    [InlineData("Quote", 39)]
    [InlineData("BracketLeft", 33)]
    [InlineData("BracketRight", 30)]
    [InlineData("Backslash", 42)]
    [InlineData("Minus", 27)]
    [InlineData("Equal", 24)]
    [InlineData("Backquote", 50)]
    public void ParseKey_MapsToMacKeycode(string key, int code)
        => Assert.Equal((ushort)code, MacInputter.ParseKey(key));

    [Theory]
    [InlineData("")]
    [InlineData("Bogus")]
    [InlineData("Key1")]            // char after "Key" isn't A-Z
    [InlineData("F25")]             // out of F1..F24 range
    [InlineData("Digit")]           // too short to be a digit code
    [InlineData("Insert")]          // no macOS keycode
    [InlineData("NumLock")]         // no macOS keycode
    [InlineData("F21")]             // F21-F24 unmapped on macOS
    [InlineData("MediaPlayPause")]  // NX system event, not a CGKeyboardEvent
    public void ParseKey_UnmappedReturnsSentinel(string key)
        => Assert.Equal((ushort)0xFFFF, MacInputter.ParseKey(key));
}
