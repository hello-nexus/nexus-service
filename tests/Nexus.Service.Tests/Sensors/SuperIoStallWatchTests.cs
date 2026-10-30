using Nexus.Service.Sensors;
using Xunit;

namespace Nexus.Service.Tests.Sensors;

public class SuperIoStallWatchTests
{
    private static readonly float?[] Dead = { 0f, 0f, 0f };
    private static readonly float?[] Full = { 100f, 100f };
    private static readonly float?[] Spinning = { 0f, 900f, 0f };
    private static readonly float?[] Partial = { 100f, 60f };

    [Fact]
    public void IsStalled_AllFansZeroAndAllControlsFull() => Assert.True(SuperIoStallWatch.IsStalled(Dead, Full));

    [Fact]
    public void IsStalled_FalseWhenAnyFanSpins() => Assert.False(SuperIoStallWatch.IsStalled(Spinning, Full));

    [Fact]
    public void IsStalled_FalseWhenAnyControlBelowFull() => Assert.False(SuperIoStallWatch.IsStalled(Dead, Partial));

    [Fact]
    public void IsStalled_FalseWhenControlUnreadable() => Assert.False(SuperIoStallWatch.IsStalled(Dead, new float?[] { 100f, null }));

    [Theory]
    [InlineData(0, 2)]
    [InlineData(3, 0)]
    public void IsStalled_FalseWithoutFansOrControls(int fans, int controls) =>
        Assert.False(SuperIoStallWatch.IsStalled(new float?[fans], new float?[controls].Select(_ => (float?)100f).ToArray()));

    [Fact]
    public void ShouldReopen_NeedsAFanToHaveSpunEarlier()
    {
        var watch = new SuperIoStallWatch();
        Assert.False(watch.ShouldReopen("/lpc/a", Dead, Full, 1_000));
        Assert.False(watch.ShouldReopen("/lpc/a", Spinning, Full, 2_000));
        Assert.True(watch.ShouldReopen("/lpc/a", Dead, Full, 3_000));
    }

    [Fact]
    public void ShouldReopen_TracksChipsSeparately()
    {
        var watch = new SuperIoStallWatch();
        Assert.False(watch.ShouldReopen("/lpc/a", Spinning, Full, 1_000));
        Assert.False(watch.ShouldReopen("/lpc/b", Dead, Full, 2_000));
    }

    [Fact]
    public void ShouldReopen_RateLimitedToOncePerWindow()
    {
        var watch = new SuperIoStallWatch();
        watch.ShouldReopen("/lpc/a", Spinning, Full, 0);
        Assert.True(watch.ShouldReopen("/lpc/a", Dead, Full, 10_000));
        Assert.False(watch.ShouldReopen("/lpc/a", Dead, Full, 10_000 + SuperIoStallWatch.MinIntervalMs - 1));
        Assert.True(watch.ShouldReopen("/lpc/a", Dead, Full, 10_000 + SuperIoStallWatch.MinIntervalMs));
    }

    [Fact]
    public void NoteReopen_StartsTheWindow()
    {
        var watch = new SuperIoStallWatch();
        watch.ShouldReopen("/lpc/a", Spinning, Full, 0);
        watch.NoteReopen(50_000);
        Assert.False(watch.ShouldReopen("/lpc/a", Dead, Full, 60_000));
    }

    [Fact]
    public void Format_ListsEverySampleCompactly()
    {
        var text = SuperIoStallWatch.Format("/lpc/nct6701d/0", new[]
        {
            new SuperIoSample('T', 0, -1f),
            new SuperIoSample('V', 1, 2.04f),
            new SuperIoSample('F', 2, 0f),
            new SuperIoSample('C', 2, 100f),
            new SuperIoSample('F', 3, null),
        });
        Assert.Equal("/lpc/nct6701d/0 T0=-1 V1=2.04 F2=0 C2=100 F3=null", text);
    }
}
