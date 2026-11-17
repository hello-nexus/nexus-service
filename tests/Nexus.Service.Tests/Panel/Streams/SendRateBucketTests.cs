using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Panel.Streams;
using Xunit;

namespace Nexus.Service.Tests.Panel.Streams;

public sealed class SendRateBucketTests
{
    private const long TicksPerSecond = 1_000_000;

    [Fact]
    public void A_fresh_bucket_lets_its_capacity_through_then_makes_the_next_send_wait()
    {
        var bucket = new SendRateBucket(50, 2, TicksPerSecond);

        Assert.Equal(0, bucket.WaitTicks(0));
        bucket.Consume(0);
        Assert.Equal(0, bucket.WaitTicks(0));
        bucket.Consume(0);

        // One token takes a frame period to come back.
        Assert.Equal(TicksPerSecond / 50, bucket.WaitTicks(0));
    }

    [Fact]
    public void An_idle_stretch_refills_to_capacity_and_no_further()
    {
        var bucket = new SendRateBucket(50, 2, TicksPerSecond);
        bucket.Consume(0);
        bucket.Consume(0);

        var later = 10 * TicksPerSecond;
        Assert.Equal(0, bucket.WaitTicks(later));
        bucket.Consume(later);
        bucket.Consume(later);
        Assert.True(bucket.WaitTicks(later) > 0);
    }

    [Theory]
    [InlineData(30, 1000)]
    [InlineData(60, 1000)]
    [InlineData(60, 240)]
    public void A_burst_of_arrivals_never_beats_the_rate_over_a_second(int fps, int arrivalsPerSecond)
    {
        var bucket = new SendRateBucket(fps, 2, TicksPerSecond);
        var sends = new List<long>();
        // The writer holds one frame and drops the older when a newer lands, so an arrival that
        // finds the bucket empty is skipped here the way a replaced frame is in the session.
        for (long t = 0; t < 5 * TicksPerSecond; t += TicksPerSecond / arrivalsPerSecond)
        {
            if (bucket.WaitTicks(t) == 0)
            {
                bucket.Consume(t);
                sends.Add(t);
            }
        }

        for (long start = 0; start + TicksPerSecond <= 5 * TicksPerSecond; start += TicksPerSecond / 10)
        {
            var inWindow = sends.Count(s => s >= start && s < start + TicksPerSecond);
            // The burst allowance is the bucket's capacity above the rate, never more.
            Assert.InRange(inWindow, 0, fps + 2);
        }
        var total = sends.Count;
        Assert.InRange(total, 5 * fps - 2, 5 * fps + 2);
    }

    [Fact]
    public void An_even_stream_at_the_rate_is_never_delayed()
    {
        var bucket = new SendRateBucket(60, 2, TicksPerSecond);
        var period = TicksPerSecond / 60;
        for (var i = 0; i < 600; i++)
        {
            var t = i * period + (i % 3 - 1) * 2000;
            Assert.Equal(0, bucket.WaitTicks(t));
            bucket.Consume(t);
        }
    }
}
