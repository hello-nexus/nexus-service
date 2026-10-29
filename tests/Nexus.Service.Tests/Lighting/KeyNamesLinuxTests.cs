using Nexus.Service.Lighting.KeyReactive;

namespace Nexus.Service.Tests.Lighting;

/// <summary>Linux evdev key codes (input-event-codes.h) to the canonical key names the overlay and LED labels use.</summary>
public class KeyNamesLinuxTests
{
    [Theory]
    [InlineData(1, "Escape")]
    [InlineData(2, "1")]
    [InlineData(11, "0")]
    [InlineData(16, "Q")]
    [InlineData(30, "A")]
    [InlineData(44, "Z")]
    [InlineData(28, "Enter")]
    [InlineData(57, "Space")]
    [InlineData(41, "`")]
    [InlineData(43, "\\")]
    [InlineData(86, "\\ (ISO)")]
    [InlineData(42, "Left Shift")]
    [InlineData(54, "Right Shift")]
    [InlineData(59, "F1")]
    [InlineData(68, "F10")]
    [InlineData(87, "F11")]
    [InlineData(88, "F12")]
    [InlineData(183, "F13")]
    [InlineData(193, "F23")]
    [InlineData(194, "F24")]
    [InlineData(69, "Num Lock")]
    [InlineData(71, "Number Pad 7")]
    [InlineData(83, "Number Pad .")]
    [InlineData(55, "Number Pad *")]
    [InlineData(96, "Number Pad Enter")]
    [InlineData(98, "Number Pad /")]
    [InlineData(97, "Right Control")]
    [InlineData(100, "Right Alt")]
    [InlineData(99, "Print Screen")]
    [InlineData(119, "Pause/Break")]
    [InlineData(102, "Home")]
    [InlineData(103, "Up Arrow")]
    [InlineData(105, "Left Arrow")]
    [InlineData(106, "Right Arrow")]
    [InlineData(108, "Down Arrow")]
    [InlineData(111, "Delete")]
    [InlineData(125, "Left Windows")]
    [InlineData(126, "Right Windows")]
    [InlineData(127, "Menu")]
    [InlineData(113, "Media Mute")]
    [InlineData(114, "Media Volume -")]
    [InlineData(115, "Media Volume +")]
    [InlineData(163, "Media Next")]
    [InlineData(164, "Media Play/Pause")]
    [InlineData(165, "Media Previous")]
    [InlineData(166, "Media Stop")]
    public void FromLinuxKeyCode_MapsToCanonicalName(int code, string expected)
        => Assert.Equal(expected, KeyNames.FromLinuxKeyCode(code));

    [Theory]
    [InlineData(0)]
    [InlineData(84)]
    [InlineData(89)]
    [InlineData(101)]
    [InlineData(112)]
    [InlineData(240)]
    [InlineData(272)]
    [InlineData(-1)]
    public void FromLinuxKeyCode_UnmappedCodeIsNull(int code)
        => Assert.Null(KeyNames.FromLinuxKeyCode(code));

    [Fact]
    public void FromLinuxKeyCode_EveryNameIsAKnownCanonicalName()
    {
        for (var code = 0; code < 300; code++)
        {
            if (KeyNames.FromLinuxKeyCode(code) is { } name)
            {
                Assert.True(KeyNames.IsKnown(name), $"code {code} mapped to unknown name {name}");
            }
        }
    }
}
