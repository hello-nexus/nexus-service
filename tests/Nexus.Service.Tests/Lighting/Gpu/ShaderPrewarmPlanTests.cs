using System.Linq;
using Nexus.Service.Lighting.Engine.Gpu;
using Xunit;

namespace Nexus.Service.Tests.Lighting.Gpu;

public class ShaderPrewarmPlanTests
{
    [Fact]
    public void Effects_sharing_a_source_compile_once_in_order()
    {
        var plan = new ShaderPrewarmPlan(new[] { ("a", "s1"), ("simple1", "s2"), ("simple2", "s2"), ("b", "s3") });
        var keys = new System.Collections.Generic.List<string>();
        while (plan.TryNext(out var k, out _))
        {
            keys.Add(k);
        }

        Assert.Equal(new[] { "a", "simple1", "b" }, keys);
        Assert.Equal(3, plan.Total);
        Assert.True(plan.Finished);
    }

    [Fact]
    public void Summary_counts_programs_failures_and_time()
    {
        var plan = new ShaderPrewarmPlan(new[] { ("a", "1"), ("b", "2"), ("c", "3") });
        plan.TryNext(out _, out _);
        plan.Record(true, 30);
        plan.TryNext(out _, out _);
        plan.Record(false, 12.5);

        Assert.Equal("[gpu] prewarm done: 1 of 3 programs compiled, 1 failed, 43ms total", plan.Summary(false));
        Assert.Contains("stopped", plan.Summary(true));
    }

    [Theory]
    [InlineData(1, 1, true, false)]
    [InlineData(1, 2, true, true)]
    [InlineData(1, 1, false, true)]
    public void A_lost_or_replaced_context_stops_the_run(int start, int now, bool available, bool stop)
    {
        Assert.Equal(stop, ShaderPrewarmPlan.ShouldStop(start, now, available));
    }

    [Fact]
    public void Pacing_constants_keep_the_warmup_a_trickle()
    {
        Assert.True(ShaderPrewarmPlan.Spacing >= System.TimeSpan.FromMilliseconds(100));
        Assert.True(ShaderPrewarmPlan.Settle >= System.TimeSpan.FromSeconds(1));
        Assert.Equal(2, GpuContextBounds.MaxBackgroundCompiles);
    }
}

internal static class GpuContextBounds
{
    public const int MaxBackgroundCompiles = GpuContext.MaxBackgroundCompiles;
}
