using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Monitoring.History.Binary;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History.Binary;

/// <summary>
/// The app-usage tier's cached segment writer: repeated flushes into one
/// day append without re-validating the file, a torn tail left by a crash
/// is still truncated on the first touch after a reopen, a segment deleted
/// underneath a running store is revalidated instead of trusted, and the
/// single-pass QueryWindow/QueryAppSeriesBatch answers match the separate
/// per-call queries they replace on the route.
/// </summary>
public class AppUsageStoreWriterTests : IDisposable
{
    private readonly string _dir;
    private BinaryMetricsHistoryStore _store;

    public AppUsageStoreWriterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-appusagewriter-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _store = new BinaryMetricsHistoryStore(_dir);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private IAppUsageHistoryStore Apps => _store;

    private void Reopen()
    {
        _store.Dispose();
        _store = new BinaryMetricsHistoryStore(_dir);
    }

    private string CpuSegPath(long ts) => Path.Combine(_dir, "apps", "cpu", $"{ts / 86_400}.seg");

    private static AppUsageTick CpuTick(long ts, params (string Name, double Value)[] apps) =>
        new(ts, new[] { new AppMetricSample("cpu", apps.Select(a => new AppUsagePoint(a.Name, a.Value, null)).ToList()) });

    private static AppUsageTick MultiAdapterGpuTick(long ts, string gid0, (string Name, double Value, double? Vram) app0, string gid1, (string Name, double Value, double? Vram) app1) =>
        new(ts, new[]
        {
            new AppMetricSample($"gpu:{gid0}", new[] { new AppUsagePoint(app0.Name, app0.Value, app0.Vram) }),
            new AppMetricSample($"gpu:{gid1}", new[] { new AppUsagePoint(app1.Name, app1.Value, app1.Vram) }),
        });

    [Fact]
    public void RepeatedFlushesIntoOneDay_AppendEveryTick_AndOnlyTheNewIdsToTheIdMap()
    {
        for (var i = 0; i < 20; i++)
        {
            Apps.Append(new[] { CpuTick(1000 + i * 5, ("a.exe", 10), ($"app{i}.exe", 1)) }, null);
        }

        var points = Apps.QueryAppSeries("cpu", "a.exe", 0, 10_000);
        Assert.Equal(20, points.Count);
        Assert.Equal(Enumerable.Range(0, 20).Select(i => 1000L + i * 5), points.Select(p => p.TsSec));

        // 21 distinct names (a.exe + app0..app19) -> 21 four-byte id records;
        // a writer re-flushing already-flushed ids would inflate this.
        var idsPath = Path.ChangeExtension(CpuSegPath(1000), ".ids");
        Assert.Equal(21 * 4, new FileInfo(idsPath).Length);
    }

    [Fact]
    public void TornTailFromACrash_IsTruncatedOnTheFirstFlushAfterReopen()
    {
        Apps.Append(new[] { CpuTick(1000, ("a.exe", 10)) }, null);
        Reopen();

        // A crash mid-append: half a record after the last good one.
        var segPath = CpuSegPath(1000);
        var cleanLength = new FileInfo(segPath).Length;
        using (var fs = new FileStream(segPath, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Seek(0, SeekOrigin.End);
            fs.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7 });
        }

        Apps.Append(new[] { CpuTick(1005, ("a.exe", 20)) }, null);

        var points = Apps.QueryAppSeries("cpu", "a.exe", 0, 10_000);
        Assert.Equal(new long[] { 1000, 1005 }, points.Select(p => p.TsSec));
        Assert.Equal(cleanLength * 2, new FileInfo(segPath).Length); // debris gone, two equal records
    }

    [Fact]
    public void SegmentDeletedUnderARunningStore_IsRevalidated_NotTrusted()
    {
        Apps.Append(new[] { CpuTick(1000, ("a.exe", 10)) }, null);
        File.Delete(CpuSegPath(1000));

        Apps.Append(new[] { CpuTick(1005, ("a.exe", 20)) }, null);

        var points = Apps.QueryAppSeries("cpu", "a.exe", 0, 10_000);
        var point = Assert.Single(points);
        Assert.Equal(1005, point.TsSec);
        Assert.Equal(20, point.Value);
    }

    [Fact]
    public void IdMapDeletedUnderARunningStore_IsRewritten_SoLaterRecordsStillResolve()
    {
        Apps.Append(new[] { CpuTick(1000, ("a.exe", 10)) }, null);
        File.Delete(Path.ChangeExtension(CpuSegPath(1000), ".ids"));

        Apps.Append(new[] { CpuTick(1005, ("a.exe", 20), ("b.exe", 5)) }, null);

        Assert.Equal(new long[] { 1000, 1005 }, Apps.QueryAppSeries("cpu", "a.exe", 0, 10_000).Select(p => p.TsSec));
        Assert.Equal(new long[] { 1005 }, Apps.QueryAppSeries("cpu", "b.exe", 0, 10_000).Select(p => p.TsSec));
    }

    [Fact]
    public void IdMapWithAPartialRecordUnderARunningStore_IsRewritten_NotAppendedTo()
    {
        Apps.Append(new[] { CpuTick(1000, ("a.exe", 10)) }, null);
        var idsPath = Path.ChangeExtension(CpuSegPath(1000), ".ids");
        using (var fs = new FileStream(idsPath, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Seek(0, SeekOrigin.End);
            fs.Write(new byte[] { 9, 9 });
        }

        Apps.Append(new[] { CpuTick(1005, ("b.exe", 5)) }, null);

        Assert.Equal(8, new FileInfo(idsPath).Length);
        var top = Apps.QueryTopApps("cpu", 0, 10_000, 15);
        Assert.Equal(new[] { "a.exe", "b.exe" }, top.Select(a => a.Name).OrderBy(n => n));
    }

    [Fact]
    public void SegmentTruncatedUnderARunningStore_IsRevalidated_AndAppendsCleanly()
    {
        Apps.Append(new[] { CpuTick(1000, ("a.exe", 10)), CpuTick(1005, ("a.exe", 15)) }, null);

        // Chop the file mid-way through the second record.
        var segPath = CpuSegPath(1000);
        using (var fs = new FileStream(segPath, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.SetLength(fs.Length - 3);
        }

        Apps.Append(new[] { CpuTick(1010, ("a.exe", 20)) }, null);

        var points = Apps.QueryAppSeries("cpu", "a.exe", 0, 10_000);
        Assert.Equal(new long[] { 1000, 1010 }, points.Select(p => p.TsSec));
    }

    [Fact]
    public void QueryWindow_MatchesQuerySampledTicksAndQueryTopApps()
    {
        SeedTwoDaysTwoAdapters();

        foreach (var metric in new[] { "cpu", "gpu", "gpu:gpu-0", "vram", "bogus" })
        {
            var window = Apps.QueryWindow(metric, 0, long.MaxValue, 15);

            Assert.Equal(Apps.QuerySampledTicks(metric, 0, long.MaxValue), window.SampledTicks);
            Assert.Equal(Apps.QueryTopApps(metric, 0, long.MaxValue, 15), window.TopApps);
        }
    }

    [Fact]
    public void QueryWindow_HonorsMaxApps_AndTheWindowBounds()
    {
        SeedTwoDaysTwoAdapters();

        var window = Apps.QueryWindow("cpu", 86_400, 86_400 + 10, 1);

        Assert.Equal(Apps.QuerySampledTicks("cpu", 86_400, 86_400 + 10), window.SampledTicks);
        Assert.Equal(Apps.QueryTopApps("cpu", 86_400, 86_400 + 10, 1), window.TopApps);
        Assert.Single(window.TopApps);
    }

    [Fact]
    public void QueryAppSeriesBatch_MatchesPerNameQueryAppSeries_IncludingUnknownAndCasing()
    {
        SeedTwoDaysTwoAdapters();

        var names = new[] { "A.EXE", "b.exe", "game.exe", "never.exe" };
        foreach (var metric in new[] { "cpu", "gpu", "gpu:gpu-1", "vram" })
        {
            var batch = Apps.QueryAppSeriesBatch(metric, names, 0, long.MaxValue);

            Assert.Equal(names.Length, batch.Count);
            foreach (var name in names)
            {
                Assert.Equal(Apps.QueryAppSeries(metric, name, 0, long.MaxValue), batch[name]);
            }
        }
    }

    [Fact]
    public void QueryAppSeriesBatch_DedupesNamesThatResolveToOneApp()
    {
        Apps.Append(new[] { CpuTick(1000, ("Chrome.exe", 10)) }, null);

        var batch = Apps.QueryAppSeriesBatch("cpu", new[] { "chrome.exe", "CHROME.EXE" }, 0, 10_000);

        Assert.Single(batch["chrome.exe"]);
        Assert.Single(batch["CHROME.EXE"]);
    }

    // Two UTC days of cpu data for a.exe/b.exe plus a two-adapter gpu tick,
    // so the bare gpu/vram cross-adapter pre-aggregation is exercised.
    private void SeedTwoDaysTwoAdapters()
    {
        var scalar = new MetricSample(1000, null, null, null, null, null,
            new[] { new GpuReading("gpu-0", "RTX", "", 50, 60), new GpuReading("gpu-1", "RX", "", 50, 60) },
            Array.Empty<FanReading>());
        ((IMetricsHistoryStore)_store).Append(new[] { scalar }, null);

        Apps.Append(new[]
        {
            CpuTick(1000, ("a.exe", 10), ("b.exe", 40)),
            CpuTick(1005, ("a.exe", 30)),
            CpuTick(86_400 + 5, ("b.exe", 5), ("a.exe", 5)),
            MultiAdapterGpuTick(1000, "gpu-0", ("game.exe", 30, 1000), "gpu-1", ("game.exe", 10, 500)),
            MultiAdapterGpuTick(86_400 + 5, "gpu-0", ("game.exe", 5, 900), "gpu-1", ("a.exe", 5, null)),
            new AppUsageTick(1005, new[]
            {
                new AppMetricSample("vram:gpu-0", new[] { new AppUsagePoint("game.exe", 1500, null) }),
                new AppMetricSample("vram:gpu-1", new[] { new AppUsagePoint("game.exe", 500, null) }),
            }),
        }, null);
    }
}
