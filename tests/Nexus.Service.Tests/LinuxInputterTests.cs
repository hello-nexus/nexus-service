using Nexus.Service.Peripherals.Keeb;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>Covers the web-key-name → Linux KEY_* mapping (the error-prone part of LinuxInputter).</summary>
public class LinuxInputterTests
{
    [Theory]
    [InlineData("KeyA", 30)]
    [InlineData("KeyZ", 44)]
    [InlineData("KeyM", 50)]
    [InlineData("Digit0", 11)]
    [InlineData("Digit1", 2)]
    [InlineData("Digit9", 10)]
    [InlineData("F1", 59)]
    [InlineData("F10", 68)]
    [InlineData("F12", 88)]
    [InlineData("F13", 183)]
    [InlineData("F24", 194)]
    [InlineData("Space", 57)]
    [InlineData("Enter", 28)]
    [InlineData("Escape", 1)]
    [InlineData("ArrowUp", 103)]
    [InlineData("MediaPlayPause", 164)]
    [InlineData("AudioVolumeMute", 113)]
    [InlineData("Numpad0", 82)]
    [InlineData("Numpad7", 71)]
    [InlineData("NumpadAdd", 78)]
    [InlineData("NumpadEnter", 96)]
    public void ParseKey_MapsToLinuxKeyCode(string key, int code)
        => Assert.Equal(code, LinuxInputter.ParseKey(key));

    [Theory]
    [InlineData("")]
    [InlineData("Bogus")]
    [InlineData("Key1")]  // char after "Key" isn't A-Z
    [InlineData("F25")]   // out of F1..F24 range
    [InlineData("Digit")] // too short to be a digit code
    public void ParseKey_UnknownReturnsZero(string key)
        => Assert.Equal(0, LinuxInputter.ParseKey(key));
}
