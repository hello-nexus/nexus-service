using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Routes;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// GET /monitoring/history/apps against a fake IAppUsageHistoryStore (db
/// side) plus the real AppSampleBuffer singleton (tail side), verifying the
/// route merges the two rather than only ever answering from the db - the
/// gap a prior review round found (the apps route ignored the unflushed
/// tail entirely, so its right edge disagreed with /monitoring/history's).
/// </summary>
public sealed class AppsHistoryRouteTailMergeTests : IDisposable
{
    private readonly NexusAppFactory _baseFactory;
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;
    private readonly FakeAppUsageHistoryStore _store = new();

    public AppsHistoryRouteTailMergeTests()
    {
        _baseFactory = new NexusAppFactory();
        _factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAppUsageHistoryStore>();
                services.AddSingleton<IAppUsageHistoryStore>(_store);
            }));
    }

    public void Dispose()
    {
        _factory.Dispose();
        _baseFactory.Dispose();
    }

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private sealed class FakeAppUsageHistoryStore : IAppUsageHistoryStore
    {
        public List<AppWindowStat> TopApps = new();
        public Dictionary<string, List<AppRawPoint>> Series = new(StringComparer.OrdinalIgnoreCase);
        public List<long> SampledTicks = new();

        public void Append(IReadOnlyList<AppUsageTick> ticks, long? pruneCutoffSec) { }

        public IReadOnlyList<AppWindowStat> QueryTopApps(string metric, long fromSec, long toSec, int maxApps) =>
            TopApps.OrderByDescending(a => a.Avg).Take(maxApps).ToList();

        public IReadOnlyList<AppRawPoint> QueryAppSeries(string metric, string appName, long fromSec, long toSec) =>
            Series.TryGetValue(appName, out var points) ? points : Array.Empty<AppRawPoint>();

        public IReadOnlyList<long> QuerySampledTicks(string metric, long fromSec, long toSec) => SampledTicks;

        public long? QueryFirstSeen(string appName) => null;
    }

    [Fact]
    public async Task TailOnlyApp_WithNoDbHistory_AppearsInTheResponse()
    {
        // app.exe has been running only for the last few seconds - never
        // flushed to the db, only visible in the buffered tail.
        var appBuffer = _factory.Services.GetRequiredService<AppSampleBuffer>();
        appBuffer.Append(new AppUsageTick(5000,
            new[] { new AppMetricSample("cpu", new[] { new AppUsagePoint("app.exe", 80, null) }) }));

        var res = await Client().GetAsync("/monitoring/history/apps?from=0&to=6000000&series=cpu");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var apps = doc.RootElement.GetProperty("apps");
        Assert.Equal(1, apps.GetArrayLength());
        Assert.Equal("app.exe", apps[0].GetProperty("name").GetString());
        Assert.Equal(80, apps[0].GetProperty("avg").GetDouble());
    }

    [Fact]
    public async Task TailSample_MergesWithDbHistory_ForTheSameApp()
    {
        _store.SampledTicks = new List<long> { 1000 };
        _store.TopApps = new List<AppWindowStat> { new("app.exe", 20, 20) };
        _store.Series["app.exe"] = new List<AppRawPoint> { new(1000, 20, null) };

        var appBuffer = _factory.Services.GetRequiredService<AppSampleBuffer>();
        appBuffer.Append(new AppUsageTick(5000,
            new[] { new AppMetricSample("cpu", new[] { new AppUsagePoint("app.exe", 60, null) }) }));

        var res = await Client().GetAsync("/monitoring/history/apps?from=0&to=6000000&series=cpu");

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var app = doc.RootElement.GetProperty("apps")[0];
        Assert.Equal("app.exe", app.GetProperty("name").GetString());
        // Two sampled ticks total (db=1000, tail=5000): (20+60)/2 = 40.
        Assert.Equal(40, app.GetProperty("avg").GetDouble());
        Assert.Equal(2, app.GetProperty("points").GetArrayLength());
    }

    [Fact]
    public async Task BareGpuSeries_AggregatesTailTicksAcrossAdapters()
    {
        // No db-side gpu history; the app only ever shows up in the
        // buffered tail, split across two adapters in the same tick.
        var appBuffer = _factory.Services.GetRequiredService<AppSampleBuffer>();
        appBuffer.Append(new AppUsageTick(5000, new[]
        {
            new AppMetricSample("gpu:gpu-nvidia-0", new[] { new AppUsagePoint("game.exe", 30, 1000) }),
            new AppMetricSample("gpu:gpu-amd-0", new[] { new AppUsagePoint("game.exe", 10, 500) }),
        }));

        var res = await Client().GetAsync("/monitoring/history/apps?from=0&to=6000000&series=gpu");

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var apps = doc.RootElement.GetProperty("apps");
        Assert.Equal(1, apps.GetArrayLength());
        var app = apps[0];
        Assert.Equal("game.exe", app.GetProperty("name").GetString());
        Assert.Equal(40, app.GetProperty("avg").GetDouble());
        Assert.Equal(1500, app.GetProperty("vramAvgMb").GetDouble());
    }

    [Fact]
    public async Task BareVramSeries_AggregatesTailTicksAcrossAdapters()
    {
        var appBuffer = _factory.Services.GetRequiredService<AppSampleBuffer>();
        appBuffer.Append(new AppUsageTick(5000, new[]
        {
            new AppMetricSample("vram:gpu-nvidia-0", new[] { new AppUsagePoint("game.exe", 1000, null) }),
            new AppMetricSample("vram:gpu-amd-0", new[] { new AppUsagePoint("game.exe", 500, null) }),
        }));

        var res = await Client().GetAsync("/monitoring/history/apps?from=0&to=6000000&series=vram");

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var apps = doc.RootElement.GetProperty("apps");
        Assert.Equal(1, apps.GetArrayLength());
        var app = apps[0];
        Assert.Equal("game.exe", app.GetProperty("name").GetString());
        Assert.Equal(1500, app.GetProperty("avg").GetDouble());
    }

    [Fact]
    public async Task ProcessFilter_WorksForVramSeries()
    {
        _store.SampledTicks = new List<long> { 1000 };
        _store.Series["game.exe"] = new List<AppRawPoint> { new(1000, 2048, null) };

        var res = await Client().GetAsync("/monitoring/history/apps?from=0&to=6000000&series=vram&process=game.exe");

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var apps = doc.RootElement.GetProperty("apps");
        Assert.Equal(1, apps.GetArrayLength());
        Assert.Equal("game.exe", apps[0].GetProperty("name").GetString());
        Assert.Equal(2048, apps[0].GetProperty("avg").GetDouble());
    }

    [Fact]
    public async Task ProcessFilter_ReturnsExactlyThatProcess_BypassingTopN()
    {
        // "top.exe" would win the top-N ranking outright; the slideout asks
        // for "other.exe" by name, which must come back instead - not the
        // metric's top app.
        _store.SampledTicks = new List<long> { 1000 };
        _store.TopApps = new List<AppWindowStat> { new("top.exe", 90, 90) };
        _store.Series["top.exe"] = new List<AppRawPoint> { new(1000, 90, null) };
        _store.Series["other.exe"] = new List<AppRawPoint> { new(1000, 15, null) };

        var res = await Client().GetAsync("/monitoring/history/apps?from=0&to=6000000&series=cpu&process=other.exe");

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var apps = doc.RootElement.GetProperty("apps");
        Assert.Equal(1, apps.GetArrayLength());
        Assert.Equal("other.exe", apps[0].GetProperty("name").GetString());
        Assert.Equal(15, apps[0].GetProperty("avg").GetDouble());
    }

    [Fact]
    public async Task ProcessFilter_ReturnsEmpty_ForAProcessWithNoDataInTheWindow()
    {
        _store.SampledTicks = new List<long> { 1000 };
        _store.TopApps = new List<AppWindowStat> { new("top.exe", 90, 90) };
        _store.Series["top.exe"] = new List<AppRawPoint> { new(1000, 90, null) };

        var res = await Client().GetAsync("/monitoring/history/apps?from=0&to=6000000&series=cpu&process=never-seen.exe");

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("apps").GetArrayLength());
    }

    [Fact]
    public async Task ProcessFilter_StillMergesTheBufferedTail()
    {
        _store.SampledTicks = new List<long> { 1000 };
        _store.Series["app.exe"] = new List<AppRawPoint> { new(1000, 20, null) };

        var appBuffer = _factory.Services.GetRequiredService<AppSampleBuffer>();
        appBuffer.Append(new AppUsageTick(5000,
            new[] { new AppMetricSample("cpu", new[] { new AppUsagePoint("app.exe", 60, null) }) }));

        var res = await Client().GetAsync("/monitoring/history/apps?from=0&to=6000000&series=cpu&process=app.exe");

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var app = doc.RootElement.GetProperty("apps")[0];
        // Two sampled ticks total (db=1000, tail=5000): (20+60)/2 = 40.
        Assert.Equal(40, app.GetProperty("avg").GetDouble());
        Assert.Equal(2, app.GetProperty("points").GetArrayLength());
    }

    [Fact]
    public async Task NoProcessFilter_StillReturnsTheTopNBehavior()
    {
        _store.SampledTicks = new List<long> { 1000 };
        _store.TopApps = new List<AppWindowStat> { new("top.exe", 90, 90) };
        _store.Series["top.exe"] = new List<AppRawPoint> { new(1000, 90, null) };

        var res = await Client().GetAsync("/monitoring/history/apps?from=0&to=6000000&series=cpu");

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var apps = doc.RootElement.GetProperty("apps");
        Assert.Equal(1, apps.GetArrayLength());
        Assert.Equal("top.exe", apps[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task TailTick_WithNoAppsForTheMetric_DoesNotInflateTheSampledTickCount()
    {
        // An AppMetricSample with an empty Apps list would persist zero
        // app_cpu_seconds rows if flushed, so SqliteMetricsHistoryStore's
        // QuerySampledTicks would not count it - the tail must agree, or
        // the reported average would change the instant this tick flushes
        // even though no real data moved.
        _store.SampledTicks = new List<long> { 1000 };
        _store.TopApps = new List<AppWindowStat> { new("app.exe", 40, 40) };
        _store.Series["app.exe"] = new List<AppRawPoint> { new(1000, 40, null) };

        var appBuffer = _factory.Services.GetRequiredService<AppSampleBuffer>();
        appBuffer.Append(new AppUsageTick(5000,
            new[] { new AppMetricSample("cpu", Array.Empty<AppUsagePoint>()) }));

        var res = await Client().GetAsync("/monitoring/history/apps?from=0&to=6000000&series=cpu");

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var app = doc.RootElement.GetProperty("apps")[0];
        // One sampled tick (db=1000 only, the empty tail tick does not
        // count): 40/1 = 40, not 40/2 = 20.
        Assert.Equal(40, app.GetProperty("avg").GetDouble());
    }

    // Descending-avg apps named appN.exe, ascending N as avg descends - each
    // has one db point matching its avg, enough for MergeAppTail to produce
    // a non-empty series per candidate.
    private void SeedApps(int count)
    {
        _store.SampledTicks = new List<long> { 1000 };
        _store.TopApps = new List<AppWindowStat>();
        for (var i = 0; i < count; i++)
        {
            var name = $"app{i}.exe";
            var avg = count - i;
            _store.TopApps.Add(new AppWindowStat(name, avg, avg));
            _store.Series[name] = new List<AppRawPoint> { new(1000, avg, null) };
        }
    }

    [Fact]
    public async Task AbsentMaxApps_ReturnsEveryAppInTheWindow_ForCpu()
    {
        SeedApps(40);

        var res = await Client().GetAsync("/monitoring/history/apps?from=0&to=6000000&series=cpu");

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(40, doc.RootElement.GetProperty("apps").GetArrayLength());
    }

    [Fact]
    public async Task AbsentMaxApps_ReturnsEveryAppInTheWindow_ForMemory()
    {
        SeedApps(40);

        var res = await Client().GetAsync("/monitoring/history/apps?from=0&to=6000000&series=memory");

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(40, doc.RootElement.GetProperty("apps").GetArrayLength());
    }

    [Fact]
    public async Task ExplicitMaxApps_StillClampsToTheRequestedTopN()
    {
        SeedApps(40);

        var res = await Client().GetAsync("/monitoring/history/apps?from=0&to=6000000&series=cpu&maxApps=5");

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var apps = doc.RootElement.GetProperty("apps");
        Assert.Equal(5, apps.GetArrayLength());
        Assert.Equal(new[] { "app0.exe", "app1.exe", "app2.exe", "app3.exe", "app4.exe" },
            Enumerable.Range(0, 5).Select(i => apps[i].GetProperty("name").GetString()));
    }

    [Fact]
    public async Task AbsentMaxApps_StillClampsAtTheHardCap_WhenTheWindowHasMore()
    {
        // Seeding the store at exactly MaxMaxApps leaves QueryTopApps's own
        // Take(maxApps) a no-op (the fake already holds no more than that),
        // so tail-only apps push the candidate set past the cap instead -
        // only the route's own Take(clampedMaxApps) can be trimming it back
        // down when this asserts the cap held.
        const int tailOnlyAppCount = 10;
        SeedApps(MonitoringHistoryRoutes.MaxMaxApps);

        var appBuffer = _factory.Services.GetRequiredService<AppSampleBuffer>();
        var tailOnlyApps = Enumerable.Range(MonitoringHistoryRoutes.MaxMaxApps, tailOnlyAppCount)
            .Select(i => new AppUsagePoint($"app{i}.exe", 1, null))
            .ToArray();
        appBuffer.Append(new AppUsageTick(5000, new[] { new AppMetricSample("cpu", tailOnlyApps) }));

        var res = await Client().GetAsync("/monitoring/history/apps?from=0&to=6000000&series=cpu");

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(MonitoringHistoryRoutes.MaxMaxApps, doc.RootElement.GetProperty("apps").GetArrayLength());
    }
}
