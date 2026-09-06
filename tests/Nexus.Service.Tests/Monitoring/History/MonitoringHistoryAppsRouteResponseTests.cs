using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

public class MonitoringHistoryAppsRouteResponseTests
{
    private static readonly IReadOnlyDictionary<string, long> NoLiveProcesses = new Dictionary<string, long>();

    [Fact]
    public void BuildAppsHistoryResponse_IsSupported_AndMapsNameAvgMax()
    {
        var topApps = new[] { new AppWindowStat("app.exe", 42.3, 90.1) };
        var series = new Dictionary<string, IReadOnlyList<AppRawPoint>> { ["app.exe"] = Array.Empty<AppRawPoint>() };

        var response = MonitoringHistoryRoutes.BuildAppsHistoryResponse(
            topApps, series, NoLiveProcesses, isGpuMetric: false, fromSec: 0, toSec: 100, maxPoints: 100);

        Assert.True(response.Supported);
        var app = Assert.Single(response.Apps);
        Assert.Equal("app.exe", app.Name);
        Assert.Equal(42.3, app.Avg);
        Assert.Equal(90.1, app.Max);
    }

    [Fact]
    public void BuildAppsHistoryResponse_ConvertsPointTimestampsToUtcMilliseconds()
    {
        var topApps = new[] { new AppWindowStat("app.exe", 10, 10) };
        var series = new Dictionary<string, IReadOnlyList<AppRawPoint>>
        {
            ["app.exe"] = new[] { new AppRawPoint(1000, 10, null) },
        };

        var response = MonitoringHistoryRoutes.BuildAppsHistoryResponse(
            topApps, series, NoLiveProcesses, isGpuMetric: false, fromSec: 1000, toSec: 1000, maxPoints: 100);

        var point = Assert.Single(Assert.Single(response.Apps).Points);
        Assert.Equal(1_000_000, point.T);
    }

    [Fact]
    public void BuildAppsHistoryResponse_RoundsAvgAndPointValuesToOneDecimal()
    {
        var topApps = new[] { new AppWindowStat("app.exe", 42.345, 90.111) };
        var series = new Dictionary<string, IReadOnlyList<AppRawPoint>>
        {
            ["app.exe"] = new[] { new AppRawPoint(0, 12.34, null) },
        };

        var response = MonitoringHistoryRoutes.BuildAppsHistoryResponse(
            topApps, series, NoLiveProcesses, isGpuMetric: false, fromSec: 0, toSec: 0, maxPoints: 100);

        var app = Assert.Single(response.Apps);
        Assert.Equal(42.3, app.Avg);
        Assert.Equal(12.3, app.Points.Single().Avg);
    }

    [Fact]
    public void BuildAppsHistoryResponse_AttachesStartedAtMs_WhenALiveProcessMatchesByName()
    {
        var topApps = new[] { new AppWindowStat("app.exe", 10, 10) };
        var series = new Dictionary<string, IReadOnlyList<AppRawPoint>> { ["app.exe"] = Array.Empty<AppRawPoint>() };
        var live = new Dictionary<string, long> { ["app.exe"] = 123_456 };

        var response = MonitoringHistoryRoutes.BuildAppsHistoryResponse(
            topApps, series, live, isGpuMetric: false, fromSec: 0, toSec: 0, maxPoints: 100);

        Assert.Equal(123_456, Assert.Single(response.Apps).StartedAtMs);
    }

    [Fact]
    public void BuildAppsHistoryResponse_OmitsStartedAtMs_WhenNoLiveProcessMatches()
    {
        var topApps = new[] { new AppWindowStat("exited.exe", 10, 10) };
        var series = new Dictionary<string, IReadOnlyList<AppRawPoint>> { ["exited.exe"] = Array.Empty<AppRawPoint>() };

        var response = MonitoringHistoryRoutes.BuildAppsHistoryResponse(
            topApps, series, NoLiveProcesses, isGpuMetric: false, fromSec: 0, toSec: 0, maxPoints: 100);

        Assert.Null(Assert.Single(response.Apps).StartedAtMs);
    }

    [Fact]
    public void BuildAppsHistoryResponse_AttachesVramAvgMb_ForAGpuMetric()
    {
        var topApps = new[] { new AppWindowStat("game.exe", 40, 40) };
        var series = new Dictionary<string, IReadOnlyList<AppRawPoint>>
        {
            ["game.exe"] = new[] { new AppRawPoint(0, 40, 1000), new AppRawPoint(1, 40, 2000) },
        };

        var response = MonitoringHistoryRoutes.BuildAppsHistoryResponse(
            topApps, series, NoLiveProcesses, isGpuMetric: true, fromSec: 0, toSec: 1, maxPoints: 100);

        Assert.Equal(1500, Assert.Single(response.Apps).VramAvgMb);
    }

    [Fact]
    public void BuildAppsHistoryResponse_OmitsVramAvgMb_ForANonGpuMetric()
    {
        var topApps = new[] { new AppWindowStat("app.exe", 10, 10) };
        var series = new Dictionary<string, IReadOnlyList<AppRawPoint>>
        {
            ["app.exe"] = new[] { new AppRawPoint(0, 10, null) },
        };

        var response = MonitoringHistoryRoutes.BuildAppsHistoryResponse(
            topApps, series, NoLiveProcesses, isGpuMetric: false, fromSec: 0, toSec: 0, maxPoints: 100);

        Assert.Null(Assert.Single(response.Apps).VramAvgMb);
    }

    [Fact]
    public void BuildAppsHistoryResponse_PreservesTheStoresTopAppsOrder()
    {
        var topApps = new[]
        {
            new AppWindowStat("first", 90, 90),
            new AppWindowStat("second", 40, 40),
        };
        var series = new Dictionary<string, IReadOnlyList<AppRawPoint>>
        {
            ["first"] = Array.Empty<AppRawPoint>(),
            ["second"] = Array.Empty<AppRawPoint>(),
        };

        var response = MonitoringHistoryRoutes.BuildAppsHistoryResponse(
            topApps, series, NoLiveProcesses, isGpuMetric: false, fromSec: 0, toSec: 0, maxPoints: 100);

        Assert.Equal(new[] { "first", "second" }, response.Apps.Select(a => a.Name));
    }

    [Fact]
    public void BuildAppsHistoryResponse_ReturnsEmptyApps_WhenNoTopAppsFound()
    {
        var response = MonitoringHistoryRoutes.BuildAppsHistoryResponse(
            System.Array.Empty<AppWindowStat>(),
            new Dictionary<string, IReadOnlyList<AppRawPoint>>(),
            NoLiveProcesses, isGpuMetric: false, fromSec: 0, toSec: 0, maxPoints: 100);

        Assert.True(response.Supported);
        Assert.Empty(response.Apps);
    }

    [Fact]
    public void BuildAppsHistoryResponse_DecimatesPointsAccordingToMaxPoints()
    {
        var topApps = new[] { new AppWindowStat("app.exe", 10, 10) };
        var raw = new List<AppRawPoint>();
        for (var t = 0; t < 100; t++)
        {
            raw.Add(new AppRawPoint(t, 10, null));
        }
        var series = new Dictionary<string, IReadOnlyList<AppRawPoint>> { ["app.exe"] = raw };

        var response = MonitoringHistoryRoutes.BuildAppsHistoryResponse(
            topApps, series, NoLiveProcesses, isGpuMetric: false, fromSec: 0, toSec: 99, maxPoints: 10);

        Assert.True(Assert.Single(response.Apps).Points.Count <= 10);
    }

    [Fact]
    public void AggregateGpuMetrics_SumsAnAppsValueAcrossAdapters()
    {
        var tick = new AppUsageTick(1000, new[]
        {
            new AppMetricSample("gpu:gpu-nvidia-0", new[] { new AppUsagePoint("game.exe", 30, 1000) }),
            new AppMetricSample("gpu:gpu-amd-0", new[] { new AppUsagePoint("game.exe", 10, 500) }),
        });

        var merged = MonitoringHistoryRoutes.AggregateGpuMetrics(tick);

        var point = Assert.Single(merged!.Apps);
        Assert.Equal("game.exe", point.Name);
        Assert.Equal(40, point.Value);
        Assert.Equal(1500, point.VramMb);
    }

    [Fact]
    public void AggregateGpuMetrics_KeepsAppsSeenOnOnlyOneAdapterSeparate()
    {
        var tick = new AppUsageTick(1000, new[]
        {
            new AppMetricSample("gpu:gpu-nvidia-0", new[] { new AppUsagePoint("game.exe", 30, 1000) }),
            new AppMetricSample("gpu:gpu-amd-0", new[] { new AppUsagePoint("other.exe", 5, null) }),
        });

        var merged = MonitoringHistoryRoutes.AggregateGpuMetrics(tick);

        Assert.Equal(2, merged!.Apps.Count);
        var game = merged.Apps.Single(a => a.Name == "game.exe");
        Assert.Equal(30, game.Value);
        Assert.Equal(1000, game.VramMb);
        var other = merged.Apps.Single(a => a.Name == "other.exe");
        Assert.Equal(5, other.Value);
        Assert.Null(other.VramMb);
    }

    [Fact]
    public void AggregateGpuMetrics_ReturnsNull_WhenTheTickHasNoGpuSample()
    {
        var tick = new AppUsageTick(1000, new[]
        {
            new AppMetricSample("cpu", new[] { new AppUsagePoint("app.exe", 10, null) }),
        });

        Assert.Null(MonitoringHistoryRoutes.AggregateGpuMetrics(tick));
    }

    [Fact]
    public void ResolveTailMetric_ForBareGpuSeries_AggregatesEveryAdapterSample()
    {
        var tick = new AppUsageTick(1000, new[]
        {
            new AppMetricSample("gpu:gpu-nvidia-0", new[] { new AppUsagePoint("game.exe", 30, null) }),
            new AppMetricSample("gpu:gpu-amd-0", new[] { new AppUsagePoint("game.exe", 10, null) }),
        });

        var resolved = MonitoringHistoryRoutes.ResolveTailMetric(tick, "gpu");

        Assert.Equal(40, Assert.Single(resolved!.Apps).Value);
    }

    [Fact]
    public void ResolveTailMetric_ForASpecificGpuSeries_MatchesOnlyThatAdapter()
    {
        var tick = new AppUsageTick(1000, new[]
        {
            new AppMetricSample("gpu:gpu-nvidia-0", new[] { new AppUsagePoint("game.exe", 30, null) }),
            new AppMetricSample("gpu:gpu-amd-0", new[] { new AppUsagePoint("game.exe", 10, null) }),
        });

        var resolved = MonitoringHistoryRoutes.ResolveTailMetric(tick, "gpu:gpu-nvidia-0");

        Assert.Equal(30, Assert.Single(resolved!.Apps).Value);
    }

    [Fact]
    public void AggregateVramMetrics_SumsAnAppsValueAcrossAdapters()
    {
        var tick = new AppUsageTick(1000, new[]
        {
            new AppMetricSample("vram:gpu-nvidia-0", new[] { new AppUsagePoint("game.exe", 1000, null) }),
            new AppMetricSample("vram:gpu-amd-0", new[] { new AppUsagePoint("game.exe", 500, null) }),
        });

        var merged = MonitoringHistoryRoutes.AggregateVramMetrics(tick);

        var point = Assert.Single(merged!.Apps);
        Assert.Equal("game.exe", point.Name);
        Assert.Equal(1500, point.Value);
        Assert.Null(point.VramMb);
    }

    [Fact]
    public void AggregateVramMetrics_KeepsAppsSeenOnOnlyOneAdapterSeparate()
    {
        var tick = new AppUsageTick(1000, new[]
        {
            new AppMetricSample("vram:gpu-nvidia-0", new[] { new AppUsagePoint("game.exe", 1000, null) }),
            new AppMetricSample("vram:gpu-amd-0", new[] { new AppUsagePoint("other.exe", 200, null) }),
        });

        var merged = MonitoringHistoryRoutes.AggregateVramMetrics(tick);

        Assert.Equal(2, merged!.Apps.Count);
        Assert.Equal(1000, merged.Apps.Single(a => a.Name == "game.exe").Value);
        Assert.Equal(200, merged.Apps.Single(a => a.Name == "other.exe").Value);
    }

    [Fact]
    public void AggregateVramMetrics_ReturnsNull_WhenTheTickHasNoVramSample()
    {
        var tick = new AppUsageTick(1000, new[]
        {
            new AppMetricSample("gpu:gpu-nvidia-0", new[] { new AppUsagePoint("app.exe", 10, null) }),
        });

        Assert.Null(MonitoringHistoryRoutes.AggregateVramMetrics(tick));
    }

    [Fact]
    public void ResolveTailMetric_ForBareVramSeries_AggregatesEveryAdapterSample()
    {
        var tick = new AppUsageTick(1000, new[]
        {
            new AppMetricSample("vram:gpu-nvidia-0", new[] { new AppUsagePoint("game.exe", 1000, null) }),
            new AppMetricSample("vram:gpu-amd-0", new[] { new AppUsagePoint("game.exe", 500, null) }),
        });

        var resolved = MonitoringHistoryRoutes.ResolveTailMetric(tick, "vram");

        Assert.Equal(1500, Assert.Single(resolved!.Apps).Value);
    }

    [Fact]
    public void ResolveTailMetric_ForASpecificVramSeries_MatchesOnlyThatAdapter()
    {
        var tick = new AppUsageTick(1000, new[]
        {
            new AppMetricSample("vram:gpu-nvidia-0", new[] { new AppUsagePoint("game.exe", 1000, null) }),
            new AppMetricSample("vram:gpu-amd-0", new[] { new AppUsagePoint("game.exe", 500, null) }),
        });

        var resolved = MonitoringHistoryRoutes.ResolveTailMetric(tick, "vram:gpu-nvidia-0");

        Assert.Equal(1000, Assert.Single(resolved!.Apps).Value);
    }

    [Fact]
    public void MergeAppTail_CollapsesDuplicateDbTimestamps_LaterOccurrenceWins()
    {
        // A batch re-flushed after a partial store failure holds a tick
        // twice; the merged series must carry it once.
        var dbPoints = new[] { new AppRawPoint(1000, 10, null), new AppRawPoint(1005, 20, null), new AppRawPoint(1000, 11, null) };

        var merged = MonitoringHistoryRoutes.MergeAppTail(dbPoints, Array.Empty<(long, AppMetricSample)>(), "app.exe");

        Assert.Equal(new long[] { 1000, 1005 }, merged.Select(p => p.TsSec));
        Assert.Equal(11, merged[0].Value);
    }

    [Fact]
    public void MergeAppTail_MergesDbPointsWithTailPoints_TailWinningOnOverlap()
    {
        var dbPoints = new[] { new AppRawPoint(1000, 10, null) };
        var tailForMetric = new List<(long TsSec, AppMetricSample Metric)>
        {
            (1000, new AppMetricSample("cpu", new[] { new AppUsagePoint("app.exe", 99, null) })),
            (1005, new AppMetricSample("cpu", new[] { new AppUsagePoint("app.exe", 20, null) })),
        };

        var merged = MonitoringHistoryRoutes.MergeAppTail(dbPoints, tailForMetric, "app.exe");

        Assert.Equal(new long[] { 1000, 1005 }, merged.Select(p => p.TsSec));
        Assert.Equal(99, merged[0].Value); // tail wins over the db's 10 at the same ts
        Assert.Equal(20, merged[1].Value);
    }
}
