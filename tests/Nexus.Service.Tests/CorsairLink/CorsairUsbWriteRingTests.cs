using System.Diagnostics;
using Nexus.Service.Peripherals.CorsairLink;
using Xunit;

namespace Nexus.Service.Tests.CorsairLink;

public class CorsairUsbWriteRingTests
{
    [Fact]
    public void Overlapping_ReturnsFinishedAndInflightEntriesInsideTheWindow()
    {
        var ring = new CorsairUsbWriteRing(8);
        var before = Stopwatch.GetTimestamp();
        var done = ring.Begin("hubA", "colors", 0x07);
        ring.End(done, 10);
        ring.Begin("hubA", "poll", 0x02);
        var after = Stopwatch.GetTimestamp();

        var entries = ring.Overlapping(before, after);

        Assert.Equal(2, entries.Count);
        Assert.NotEqual(0, entries[0].EndTs);
        Assert.Equal(0, entries[1].EndTs);
    }

    [Fact]
    public void Overlapping_SkipsEntriesThatEndedBeforeTheWindow()
    {
        var ring = new CorsairUsbWriteRing(8);
        var t = ring.Begin("hubA", "colors", 0x07);
        ring.End(t, 0);
        var windowStart = Stopwatch.GetTimestamp() + Stopwatch.Frequency;

        Assert.Empty(ring.Overlapping(windowStart, windowStart + 10));
    }

    [Fact]
    public void Ring_KeepsOnlyTheNewestCapacityEntries()
    {
        var ring = new CorsairUsbWriteRing(4);
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < 10; i++) ring.End(ring.Begin("hubA", "colors", i), 0);

        var entries = ring.Overlapping(start, Stopwatch.GetTimestamp());

        Assert.Equal(new[] { 6, 7, 8, 9 }, entries.ConvertAll(e => e.Command));
    }

    [Fact]
    public void End_AfterWrapIsIgnored()
    {
        var ring = new CorsairUsbWriteRing(2);
        var stale = ring.Begin("hubA", "colors", 1);
        ring.Begin("hubA", "colors", 2);
        ring.Begin("hubA", "colors", 3);
        ring.End(stale, 99);

        var entries = ring.Overlapping(0, Stopwatch.GetTimestamp());

        Assert.All(entries, e => Assert.Equal(0, e.WriteTicks));
    }

    [Fact]
    public void FormatDump_CapsLinesAndNamesTheLongest()
    {
        var ring = new CorsairUsbWriteRing(64);
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < 30; i++) ring.End(ring.Begin("hubA", "colors", i), 1);

        var line = ring.FormatDump(start, Stopwatch.GetTimestamp());

        Assert.StartsWith("[corsair-usb] ", line);
        Assert.Contains("overlapping=30 shown=16", line);
        Assert.Equal(16, line.Split("[hub=").Length - 1);
    }

    [Fact]
    public void DumpIfDue_AllowsOneDumpPerInterval()
    {
        var ring = new CorsairUsbWriteRing(8);
        var now = Stopwatch.GetTimestamp();

        Assert.True(ring.DumpIfDue(now, now, 100_000));
        Assert.False(ring.DumpIfDue(now, now, 102_000));
        Assert.True(ring.DumpIfDue(now, now, 105_000));
    }
}
