using System;
using System.Linq;
using Nexus.Service.Peripherals.LogitechAudio;
using Xunit;

namespace Nexus.Service.Tests.Peripherals.LogitechAudio;

public class LogitechSpeakerTestSoundsTests
{
    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void PinkNoise_IsOneSecondAtTheTestLevel_AndStartsAndEndsSilent(int rate)
    {
        var noise = LogitechSpeakerTestSounds.PinkNoise(rate);

        Assert.Equal(rate, noise.Length);
        // Levelled to 0.35 before the end fades, so the loudest sample can land in a fade.
        Assert.InRange(noise.Max(Math.Abs), 0.25f, 0.3501f);
        Assert.Equal(0f, noise[0]);
        Assert.Equal(0f, noise[^1]);
    }

    [Fact]
    public void Dong_IsALowStrikeUnderOneSecond()
    {
        var dong = LogitechSpeakerTestSounds.Dong(48000);

        Assert.Equal(43200, dong.Length);
        Assert.InRange(dong.Max(Math.Abs), 0.3f, 0.36f);
        // 55 Hz: a full period every ~873 samples, so the sign changes about 110 times a second.
        var crossings = dong.Zip(dong.Skip(1)).Count(p => p.First < 0 != p.Second < 0);
        Assert.InRange(crossings, 80, 110);
    }
}
