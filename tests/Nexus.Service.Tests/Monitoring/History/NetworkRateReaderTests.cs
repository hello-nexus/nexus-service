using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using Nexus.Service.Monitoring.History;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

public class NetworkRateReaderTests
{
    [Fact]
    public void ComputeRate_ReturnsNull_OnTheFirstRead()
    {
        var rate = NetworkRateReader.ComputeRate(
            lastTicks: -1, lastBytesIn: 0, lastBytesOut: 0,
            nowTicks: 1000, nowBytesIn: 5000, nowBytesOut: 2000);

        Assert.Null(rate.InBytesPerSec);
        Assert.Null(rate.OutBytesPerSec);
    }

    [Fact]
    public void ComputeRate_DividesByteDeltaByElapsedSeconds()
    {
        var rate = NetworkRateReader.ComputeRate(
            lastTicks: 0, lastBytesIn: 1000, lastBytesOut: 500,
            nowTicks: 1000, nowBytesIn: 3000, nowBytesOut: 1500);

        Assert.Equal(2000.0, rate.InBytesPerSec);
        Assert.Equal(1000.0, rate.OutBytesPerSec);
    }

    [Fact]
    public void ComputeRate_ReturnsNull_WhenElapsedExceedsFiveSeconds()
    {
        var rate = NetworkRateReader.ComputeRate(
            lastTicks: 0, lastBytesIn: 1000, lastBytesOut: 500,
            nowTicks: 5001, nowBytesIn: 3000, nowBytesOut: 1500);

        Assert.Null(rate.InBytesPerSec);
        Assert.Null(rate.OutBytesPerSec);
    }

    [Fact]
    public void ComputeRate_ReturnsNull_WhenElapsedIsZeroOrNegative()
    {
        var zero = NetworkRateReader.ComputeRate(
            lastTicks: 1000, lastBytesIn: 0, lastBytesOut: 0,
            nowTicks: 1000, nowBytesIn: 100, nowBytesOut: 100);
        var negative = NetworkRateReader.ComputeRate(
            lastTicks: 2000, lastBytesIn: 0, lastBytesOut: 0,
            nowTicks: 1000, nowBytesIn: 100, nowBytesOut: 100);

        Assert.Null(zero.InBytesPerSec);
        Assert.Null(negative.InBytesPerSec);
    }

    [Fact]
    public void ComputeRate_ReturnsNull_OnANegativeByteDelta()
    {
        // A counter reset (NIC re-enumerated) makes the delta negative.
        var rate = NetworkRateReader.ComputeRate(
            lastTicks: 0, lastBytesIn: 5000, lastBytesOut: 5000,
            nowTicks: 1000, nowBytesIn: 100, nowBytesOut: 100);

        Assert.Null(rate.InBytesPerSec);
        Assert.Null(rate.OutBytesPerSec);
    }

    [Fact]
    public void ComputeRate_AllowsElapsedExactlyFiveSeconds()
    {
        var rate = NetworkRateReader.ComputeRate(
            lastTicks: 0, lastBytesIn: 0, lastBytesOut: 0,
            nowTicks: 5000, nowBytesIn: 5000, nowBytesOut: 0);

        Assert.Equal(1000.0, rate.InBytesPerSec);
    }

    [Fact]
    public void Read_ReturnsNullOnTheFirstCall_ThenAComputedRateOnTheSecond()
    {
        var reader = new NetworkRateReader();

        var first = reader.Read();
        Assert.Null(first.InBytesPerSec);

        var second = reader.Read();
        // Real NIC counters only increase (or stay flat) between two live
        // reads a moment apart, so this asserts the reader produced a
        // non-negative result rather than an exact value.
        Assert.True(second.InBytesPerSec is null || second.InBytesPerSec >= 0);
    }

    // Read()-level coverage through the injected enumerator and clock: the
    // adapter list must be enumerated at all, reused inside the refresh
    // window, and re-enumerated when it came back empty.

    private sealed class FakeStats : IPv4InterfaceStatistics
    {
        public FakeStats(long bytesIn, long bytesOut) { BytesReceived = bytesIn; BytesSent = bytesOut; }
        public override long BytesReceived { get; }
        public override long BytesSent { get; }
        public override long IncomingPacketsDiscarded => 0;
        public override long IncomingPacketsWithErrors => 0;
        public override long IncomingUnknownProtocolPackets => 0;
        public override long NonUnicastPacketsReceived => 0;
        public override long NonUnicastPacketsSent => 0;
        public override long OutgoingPacketsDiscarded => 0;
        public override long OutgoingPacketsWithErrors => 0;
        public override long OutputQueueLength => 0;
        public override long UnicastPacketsReceived => 0;
        public override long UnicastPacketsSent => 0;
    }

    private sealed class FakeNic : NetworkInterface
    {
        public long BytesIn;
        public long BytesOut;
        public override IPv4InterfaceStatistics GetIPv4Statistics() => new FakeStats(BytesIn, BytesOut);
        public override NetworkInterfaceType NetworkInterfaceType => NetworkInterfaceType.Ethernet;
    }

    private sealed class Rig
    {
        public readonly FakeNic Nic = new();
        public readonly List<NetworkInterface[]> Enumerations = new();
        public long NowMs;
        public bool ReturnEmpty;

        public NetworkRateReader Reader()
        {
            return new NetworkRateReader(
                () =>
                {
                    var result = ReturnEmpty ? Array.Empty<NetworkInterface>() : new NetworkInterface[] { Nic };
                    Enumerations.Add(result);
                    return result;
                },
                () => NowMs);
        }
    }

    [Fact]
    public void Read_EnumeratesAdaptersOnTheFirstCall_AndReportsARateOnTheSecond()
    {
        var rig = new Rig();
        var reader = rig.Reader();

        rig.Nic.BytesIn = 1000;
        rig.Nic.BytesOut = 500;
        var first = reader.Read();

        rig.NowMs += 1000;
        rig.Nic.BytesIn = 3000;
        rig.Nic.BytesOut = 1500;
        var second = reader.Read();

        Assert.Null(first.InBytesPerSec);
        Assert.Equal(2000, second.InBytesPerSec);
        Assert.Equal(1000, second.OutBytesPerSec);
        Assert.Single(rig.Enumerations);
    }

    [Fact]
    public void Read_ReusesTheAdapterList_UntilTheRefreshWindowElapses()
    {
        var rig = new Rig();
        var reader = rig.Reader();

        for (var i = 0; i <= 30; i++)
        {
            rig.NowMs = i * 1000;
            rig.Nic.BytesIn += 100;
            reader.Read();
        }

        Assert.Equal(2, rig.Enumerations.Count); // t=0 and t=30s
    }

    [Fact]
    public void Read_RetriesEnumerationNextTick_WhenItReturnedNoAdapters()
    {
        var rig = new Rig { ReturnEmpty = true };
        var reader = rig.Reader();

        reader.Read();
        rig.ReturnEmpty = false;
        rig.NowMs = 1000;
        reader.Read();
        rig.NowMs = 2000;
        rig.Nic.BytesIn = 4000;
        var third = reader.Read();

        Assert.Equal(2, rig.Enumerations.Count);
        Assert.Equal(4000, third.InBytesPerSec);
    }
}
