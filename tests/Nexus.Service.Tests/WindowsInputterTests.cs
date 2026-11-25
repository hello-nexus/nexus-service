#if WINDOWS
using Nexus.Service.Peripherals.Keeb;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>Numpad key names of WindowsInputter.ParseKey: the virtual key sits in the low byte and bit 8 marks keys SendInput must flag as extended.</summary>
public class WindowsInputterTests
{
    private const int ExtendedBit = 0x100;

    [Theory]
    [InlineData("Numpad0", 0x60, false)]
    [InlineData("Numpad9", 0x69, false)]
    [InlineData("NumpadMultiply", 0x6A, false)]
    [InlineData("NumpadAdd", 0x6B, false)]
    [InlineData("NumpadSubtract", 0x6D, false)]
    [InlineData("NumpadDecimal", 0x6E, false)]
    [InlineData("NumpadDivide", 0x6F, true)]
    [InlineData("NumpadEnter", 0x0D, true)]
    [InlineData("NumpadEqual", 0xBB, false)]
    [InlineData("NumLock", 0x90, false)]
    public void NumpadTokens_CarryTheVirtualKeyInTheLowByteAndTheExtendedBit(string key, int vk, bool extended)
    {
        var parsed = WindowsInputter.ParseKey(key);
        Assert.Equal(vk, parsed & 0xFF);
        Assert.Equal(extended, (parsed & ExtendedBit) != 0);
    }

    [Fact]
    public void KeypadEnter_DiffersFromEnterOnlyByTheExtendedBit()
    {
        Assert.Equal(0x0D, (int)WindowsInputter.ParseKey("Enter"));
        Assert.Equal(0x0D | ExtendedBit, (int)WindowsInputter.ParseKey("NumpadEnter"));
    }

    [Fact]
    public void UnknownKey_ReturnsZero() => Assert.Equal(0, WindowsInputter.ParseKey("NumpadBogus"));
}
#endif
