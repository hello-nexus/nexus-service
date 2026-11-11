using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Diagnostics.Temperature;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics;

public class TemperatureRouteResponseTests
{
    private const long BucketMs = TemperatureInsights.NativeBucketMinutes * 60_000L;
    private static readonly long T0Ms =
        new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static List<TemperatureBucketRow> SeriesOf(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new TemperatureBucketRow("cpu", "cpu", "CPU", T0Ms + i * BucketMs, 50, 55, 10))
            .ToList();

    [Fact]
    public void BuildTemperatureResponse_ReportsTheEffectiveTierWidth()
    {
        var rows = SeriesOf(24 * 12);

        var response = DiagnosticsHealthRoutes.BuildTemperatureResponse(rows, tierWidthMinutes: 30);

        Assert.True(response.Supported);
        Assert.Equal(30, response.BucketMinutes);
    }

    [Fact]
    public void BuildTemperatureResponse_IncludesRetentionDays()
    {
        var rows = SeriesOf(10);

        var response = DiagnosticsHealthRoutes.BuildTemperatureResponse(rows, tierWidthMinutes: 5);

        Assert.Equal(TemperatureInsights.RetentionDays, response.RetentionDays);
    }

    [Fact]
    public void BuildTemperatureResponse_MergesSeriesPointsToTheTierWidth()
    {
        // 14 days of raw 5-min buckets tiered at 60 min -> 336 points.
        var rows = SeriesOf(336 * 12);

        var response = DiagnosticsHealthRoutes.BuildTemperatureResponse(rows, tierWidthMinutes: 60);

        var series = Assert.Single(response.Series);
        Assert.Equal(336, series.Points.Count);
    }

    [Fact]
    public void BuildTemperatureResponse_RawTierLeavesOnePointPerBucket()
    {
        var rows = SeriesOf(24 * 12);

        var response = DiagnosticsHealthRoutes.BuildTemperatureResponse(rows, tierWidthMinutes: TemperatureInsights.NativeBucketMinutes);

        var series = Assert.Single(response.Series);
        Assert.Equal(rows.Count, series.Points.Count);
    }

    [Fact]
    public void BuildTemperatureResponse_EpisodesCoverTheQueriedRows()
    {
        var rows = new List<TemperatureBucketRow>
        {
            new("cpu", "cpu", "CPU", T0Ms, 91, 91, 10),
            new("cpu", "cpu", "CPU", T0Ms + BucketMs, 92, 92, 10),
        };

        var response = DiagnosticsHealthRoutes.BuildTemperatureResponse(rows, tierWidthMinutes: 5);

        var episode = Assert.Single(response.Episodes);
        Assert.Equal("cpu", episode.ComponentId);
    }

    [Fact]
    public void BuildTemperatureResponse_BandsFollowTheUsersLimits()
    {
        var rows = new List<TemperatureBucketRow>
        {
            new("cpu", "cpu", "CPU", T0Ms, 96, 97, 10),
            new("cpu", "cpu", "CPU", T0Ms + BucketMs, 96, 97, 10),
        };

        var lowered = DiagnosticsHealthRoutes.BuildTemperatureResponse(rows, 5, new Dictionary<string, double> { ["cpu"] = 95 });
        var raised = DiagnosticsHealthRoutes.BuildTemperatureResponse(rows, 5, new Dictionary<string, double> { ["cpu"] = 100 });

        Assert.Equal(95, Assert.Single(lowered.Episodes).ThresholdC);
        Assert.Empty(raised.Episodes);
    }

    [Fact]
    public void BuiltInLimits_ReadsTheHighOfEachBuiltInRuleByHistoryKind()
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            Nexus.Service.Diagnostics.Alerts.BuiltInSensorRules.Ensure(s.Diagnostics, newInstall: true);
            s.Diagnostics.SensorAlerts.Single(r => r.Id == "builtin-storage").High = 66;
        });

        var limits = DiagnosticsHealthRoutes.BuiltInLimits(store);

        Assert.Equal(new Dictionary<string, double> { ["cpu"] = 100, ["gpu"] = 90, ["storage"] = 66, ["ram"] = 75 }, limits);

        store.Update(s => s.Diagnostics.SensorAlerts.Single(r => r.Id == "builtin-gpu").Enabled = false);

        Assert.DoesNotContain("gpu", DiagnosticsHealthRoutes.BuiltInLimits(store).Keys);
    }
}
