using Nexus.Service.Monitoring.History;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

public class DiskRateReaderTests
{
    [Fact]
    public void ComputeRate_ReturnsNull_OnTheFirstRead()
    {
        var rate = DiskRateReader.ComputeRate(
            lastTicks: -1, lastBytesRead: 0, lastBytesWritten: 0,
            nowTicks: 1000, nowBytesRead: 5000, nowBytesWritten: 2000);

        Assert.Null(rate.ReadBytesPerSec);
        Assert.Null(rate.WriteBytesPerSec);
    }

    [Fact]
    public void ComputeRate_DividesByteDeltaByElapsedSeconds()
    {
        var rate = DiskRateReader.ComputeRate(
            lastTicks: 0, lastBytesRead: 1000, lastBytesWritten: 500,
            nowTicks: 1000, nowBytesRead: 3000, nowBytesWritten: 1500);

        Assert.Equal(2000.0, rate.ReadBytesPerSec);
        Assert.Equal(1000.0, rate.WriteBytesPerSec);
    }

    [Fact]
    public void ComputeRate_ReturnsNull_WhenElapsedExceedsFiveSeconds()
    {
        var rate = DiskRateReader.ComputeRate(
            lastTicks: 0, lastBytesRead: 1000, lastBytesWritten: 500,
            nowTicks: 5001, nowBytesRead: 3000, nowBytesWritten: 1500);

        Assert.Null(rate.ReadBytesPerSec);
        Assert.Null(rate.WriteBytesPerSec);
    }

    [Fact]
    public void ComputeRate_ReturnsNull_WhenElapsedIsZeroOrNegative()
    {
        var zero = DiskRateReader.ComputeRate(
            lastTicks: 1000, lastBytesRead: 0, lastBytesWritten: 0,
            nowTicks: 1000, nowBytesRead: 100, nowBytesWritten: 100);
        var negative = DiskRateReader.ComputeRate(
            lastTicks: 2000, lastBytesRead: 0, lastBytesWritten: 0,
            nowTicks: 1000, nowBytesRead: 100, nowBytesWritten: 100);

        Assert.Null(zero.ReadBytesPerSec);
        Assert.Null(negative.ReadBytesPerSec);
    }

    [Fact]
    public void ComputeRate_ReturnsNull_OnANegativeByteDelta()
    {
        // A counter reset (drive re-enumerated) makes the delta negative.
        var rate = DiskRateReader.ComputeRate(
            lastTicks: 0, lastBytesRead: 5000, lastBytesWritten: 5000,
            nowTicks: 1000, nowBytesRead: 100, nowBytesWritten: 100);

        Assert.Null(rate.ReadBytesPerSec);
        Assert.Null(rate.WriteBytesPerSec);
    }

    [Fact]
    public void ComputeRate_AllowsElapsedExactlyFiveSeconds()
    {
        var rate = DiskRateReader.ComputeRate(
            lastTicks: 0, lastBytesRead: 0, lastBytesWritten: 0,
            nowTicks: 5000, nowBytesRead: 5000, nowBytesWritten: 0);

        Assert.Equal(1000.0, rate.ReadBytesPerSec);
    }

    [Fact]
    public void Read_ReturnsNullOnTheFirstCall_ThenANonNegativeOrNullRateOnTheSecond()
    {
        var reader = new DiskRateReader();

        var first = reader.Read();
        Assert.Null(first.ReadBytesPerSec);
        Assert.Null(first.WriteBytesPerSec);

        var second = reader.Read();
        // Unsupported off Windows (both stay null there); on Windows a live
        // second read returns a delta-derived rate that can only be
        // non-negative or null (see ComputeRate's negative-delta reset guard).
        Assert.True(second.ReadBytesPerSec is null || second.ReadBytesPerSec >= 0);
        Assert.True(second.WriteBytesPerSec is null || second.WriteBytesPerSec >= 0);
    }

    [Fact]
    public void Read_WithLiveActivityOff_ReturnsNullRatesEveryCall()
    {
        var reader = new DiskRateReader(() => false);

        Assert.Null(reader.Read().ReadBytesPerSec);
        var second = reader.Read();
        Assert.Null(second.ReadBytesPerSec);
        Assert.Null(second.WriteBytesPerSec);
    }
}
