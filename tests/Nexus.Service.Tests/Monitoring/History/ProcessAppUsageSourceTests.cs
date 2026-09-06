using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Activity;
using Nexus.Service.Diagnostics.Storage;
using Nexus.Service.Models.Activity;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Sensors;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

public class ProcessAppUsageSourceTests
{
    private sealed class FakeNetworkProvider : INetworkProvider
    {
        public List<NetworkProcessInfo> Snapshot { get; set; } = new();
        public bool? LastAllowOnDemandSample { get; private set; }

        public IReadOnlyList<NetworkProcessInfo> GetSnapshot(bool allowOnDemandSample = true)
        {
            LastAllowOnDemandSample = allowOnDemandSample;
            return Snapshot;
        }

        public void SetInterval(int ms) { }
    }

    private sealed class StubSensorProvider : ISensorProvider
    {
        public List<GpuReadout> Gpus { get; } = new();
        public int GetGpusCalls { get; private set; }

        public string GetCpuModel() => "Stub CPU";
        public IReadOnlyList<HardwareSensor> GetCpuSensors() => Array.Empty<HardwareSensor>();
        public (bool Healthy, float DistanceToTJMax) GetCpuHealth() => (true, 20f);
        public IReadOnlyList<string> GetGpuModels() => Gpus.Select(g => g.Name).ToList();
        public IReadOnlyList<HardwareSensor> GetGpuSensors() => Array.Empty<HardwareSensor>();
        public IReadOnlyList<GpuReadout> GetGpus()
        {
            GetGpusCalls++;
            return Gpus;
        }
        public IReadOnlyList<HardwareSensor> GetMemorySensors() => Array.Empty<HardwareSensor>();
        public string GetMemoryTotalFormatted() => "32 GB";
        public string GetRamBrandModel() => "";
        public IReadOnlyDictionary<string, StorageComponent> GetStorageComponents(bool includeSmart = true) => new Dictionary<string, StorageComponent>();
        public IReadOnlyList<string> GetStoragePartitions() => Array.Empty<string>();
        public IReadOnlyList<StorageDriveInfo> GetStorageInfo() => Array.Empty<StorageDriveInfo>();
        public string GetStorageBrandModel() => "";
        public IReadOnlyList<HardwareSensor> GetMotherboardSensors() => Array.Empty<HardwareSensor>();
        public string GetMotherboardModel() => "Stub Board";
        public SensorExtras GetSensorExtras() => new();
        public string GetOsVersion() => "TestOS";
        public void SetPollingRate(int pollingRate) { }
        public Task ReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static ProcessInfo Proc(
        string name, double cpu, double mem, int pid = 0, double storage = 0,
        double storageRead = 0, double storageWrite = 0) =>
        new()
        {
            Pid = pid, Name = name, CpuPercent = cpu, MemoryMb = mem,
            StorageBytesPerSec = storage, StorageReadBytesPerSec = storageRead, StorageWriteBytesPerSec = storageWrite,
        };

    private static (ProcessAppUsageSource Source, ProcessMonitor Processes, GpuProcessMonitor GpuProcesses, StubSensorProvider Sensors, FakeNetworkProvider Network) Build()
    {
        var hub = new MultiplexHub();
        var processes = new ProcessMonitor(hub);
        var gpuProcesses = new GpuProcessMonitor(hub, new InMemoryConfigStore());
        var sensors = new StubSensorProvider();
        var network = new FakeNetworkProvider();
        var source = new ProcessAppUsageSource(processes, gpuProcesses, sensors, network);
        return (source, processes, gpuProcesses, sensors, network);
    }

    [Fact]
    public void Sample_ReturnsEmpty_WhenNoProcessesOrGpuEntries()
    {
        var (source, _, _, _, _) = Build();

        var result = source.Sample();

        Assert.Empty(result);
    }

    [Fact]
    public void Sample_AggregatesMultiplePidsWithTheSameName_ForCpu()
    {
        var (source, processes, _, _, _) = Build();
        processes.SetProcessesForTest(new[]
        {
            Proc("chrome", cpu: 5, mem: 100, pid: 1),
            Proc("chrome", cpu: 7, mem: 150, pid: 2),
            Proc("notepad", cpu: 1, mem: 20, pid: 3),
        });

        var cpu = source.Sample().Single(m => m.Metric == "cpu");

        var chrome = cpu.Apps.Single(a => a.Name == "chrome");
        Assert.Equal(12, chrome.Value);
    }

    [Fact]
    public void Sample_ReturnsMemoryMetric_SortedByMemoryMb_IndependentlyOfCpuOrder()
    {
        var (source, processes, _, _, _) = Build();
        processes.SetProcessesForTest(new[]
        {
            Proc("low-mem-high-cpu", cpu: 90, mem: 10),
            Proc("high-mem-low-cpu", cpu: 1, mem: 900),
        });

        var mem = source.Sample().Single(m => m.Metric == "memory");

        Assert.Equal("high-mem-low-cpu", mem.Apps.First().Name);
    }

    [Fact]
    public void Sample_AggregatesMultiplePidsWithTheSameName_ForStorage()
    {
        var (source, processes, _, _, _) = Build();
        processes.SetProcessesForTest(new[]
        {
            Proc("chrome", cpu: 5, mem: 100, pid: 1, storage: 1000),
            Proc("chrome", cpu: 7, mem: 150, pid: 2, storage: 2500),
            Proc("notepad", cpu: 1, mem: 20, pid: 3, storage: 10),
        });

        var storage = source.Sample().Single(m => m.Metric == "storage");

        var chrome = storage.Apps.Single(a => a.Name == "chrome");
        Assert.Equal(3500, chrome.Value);
    }

    [Fact]
    public void Sample_ReturnsStorageMetric_SortedByStorageBytesPerSec_IndependentlyOfCpuOrder()
    {
        var (source, processes, _, _, _) = Build();
        processes.SetProcessesForTest(new[]
        {
            Proc("low-storage-high-cpu", cpu: 90, mem: 10, storage: 10),
            Proc("high-storage-low-cpu", cpu: 1, mem: 10, storage: 5_000_000),
        });

        var storage = source.Sample().Single(m => m.Metric == "storage");

        Assert.Equal("high-storage-low-cpu", storage.Apps.First().Name);
    }

    [Fact]
    public void Sample_ExcludesAnAppWithExactlyZeroStorage_EvenWithRoomUnderTheCap()
    {
        var (source, processes, _, _, _) = Build();
        processes.SetProcessesForTest(new[]
        {
            Proc("idle.exe", cpu: 1, mem: 10, storage: 0),
            Proc("active.exe", cpu: 1, mem: 10, storage: 4096),
        });

        var storage = source.Sample().Single(m => m.Metric == "storage");

        Assert.DoesNotContain(storage.Apps, a => a.Name == "idle.exe");
        Assert.Contains(storage.Apps, a => a.Name == "active.exe");
    }

    [Fact]
    public void Sample_AggregatesMultiplePidsWithTheSameName_ForStorageReadAndWrite()
    {
        var (source, processes, _, _, _) = Build();
        processes.SetProcessesForTest(new[]
        {
            Proc("chrome", cpu: 5, mem: 100, pid: 1, storageRead: 1000, storageWrite: 200),
            Proc("chrome", cpu: 7, mem: 150, pid: 2, storageRead: 2500, storageWrite: 400),
            Proc("notepad", cpu: 1, mem: 20, pid: 3, storageRead: 10, storageWrite: 5),
        });

        var result = source.Sample();
        var storageRead = result.Single(m => m.Metric == "storage-read");
        var storageWrite = result.Single(m => m.Metric == "storage-write");

        Assert.Equal(3500, storageRead.Apps.Single(a => a.Name == "chrome").Value);
        Assert.Equal(600, storageWrite.Apps.Single(a => a.Name == "chrome").Value);
    }

    [Fact]
    public void Sample_RanksStorageReadAndWrite_IndependentlyOfEachOtherAndOfCombinedStorage()
    {
        var (source, processes, _, _, _) = Build();
        processes.SetProcessesForTest(new[]
        {
            Proc("reader.exe", cpu: 1, mem: 10, storageRead: 5_000_000, storageWrite: 10),
            Proc("writer.exe", cpu: 1, mem: 10, storageRead: 10, storageWrite: 5_000_000),
        });

        var result = source.Sample();
        var storageRead = result.Single(m => m.Metric == "storage-read");
        var storageWrite = result.Single(m => m.Metric == "storage-write");

        Assert.Equal("reader.exe", storageRead.Apps.First().Name);
        Assert.Equal("writer.exe", storageWrite.Apps.First().Name);
    }

    [Fact]
    public void Sample_ExcludesAnAppWithExactlyZeroStorageReadOrWrite_EvenWithRoomUnderTheCap()
    {
        var (source, processes, _, _, _) = Build();
        processes.SetProcessesForTest(new[]
        {
            Proc("idle.exe", cpu: 1, mem: 10, storageRead: 0, storageWrite: 0),
            Proc("active.exe", cpu: 1, mem: 10, storageRead: 4096, storageWrite: 2048),
        });

        var result = source.Sample();
        var storageRead = result.Single(m => m.Metric == "storage-read");
        var storageWrite = result.Single(m => m.Metric == "storage-write");

        Assert.DoesNotContain(storageRead.Apps, a => a.Name == "idle.exe");
        Assert.DoesNotContain(storageWrite.Apps, a => a.Name == "idle.exe");
        Assert.Contains(storageRead.Apps, a => a.Name == "active.exe");
        Assert.Contains(storageWrite.Apps, a => a.Name == "active.exe");
    }

    [Fact]
    public void Sample_LimitsEachMetricToTopAppsPerSample()
    {
        var (source, processes, _, _, _) = Build();
        var procs = Enumerable.Range(1, MetricsHistory.TopAppsPerSample + 5)
            .Select(i => Proc($"app{i}", cpu: i, mem: i))
            .ToList();
        processes.SetProcessesForTest(procs);

        var cpu = source.Sample().Single(m => m.Metric == "cpu");

        Assert.Equal(MetricsHistory.TopAppsPerSample, cpu.Apps.Count);
        // The highest-cpu app (last one generated) wins the top slot.
        Assert.Equal($"app{MetricsHistory.TopAppsPerSample + 5}", cpu.Apps.First().Name);
    }

    [Fact]
    public void Sample_RecordsEveryMidRankApp_UpToTheRaisedCap()
    {
        // A count comfortably above the previous cutoff but under the
        // current cap: every one of them must appear, not just the apps
        // that would have ranked above that previous cutoff - this is the
        // gap the raised cap closes for a mid-ranked, intermittently-
        // fluctuating process.
        var (source, processes, _, _, _) = Build();
        var procs = Enumerable.Range(1, 30)
            .Select(i => Proc($"app{i}", cpu: i, mem: i))
            .ToList();
        processes.SetProcessesForTest(procs);

        var cpu = source.Sample().Single(m => m.Metric == "cpu");

        Assert.Equal(30, cpu.Apps.Count);
        Assert.Contains(cpu.Apps, a => a.Name == "app15");
    }

    [Fact]
    public void Sample_ExcludesAnAppWithExactlyZeroCpu_EvenWithRoomUnderTheCap()
    {
        var (source, processes, _, _, _) = Build();
        processes.SetProcessesForTest(new[]
        {
            Proc("idle.exe", cpu: 0, mem: 10),
            Proc("active.exe", cpu: 5, mem: 10),
        });

        var cpu = source.Sample().Single(m => m.Metric == "cpu");

        Assert.DoesNotContain(cpu.Apps, a => a.Name == "idle.exe");
        Assert.Contains(cpu.Apps, a => a.Name == "active.exe");
    }

    [Fact]
    public void Sample_ExcludesAnAppWithExactlyZeroMemory_EvenWithRoomUnderTheCap()
    {
        var (source, processes, _, _, _) = Build();
        processes.SetProcessesForTest(new[]
        {
            Proc("empty.exe", cpu: 1, mem: 0),
            Proc("active.exe", cpu: 1, mem: 10),
        });

        var mem = source.Sample().Single(m => m.Metric == "memory");

        Assert.DoesNotContain(mem.Apps, a => a.Name == "empty.exe");
        Assert.Contains(mem.Apps, a => a.Name == "active.exe");
    }

    [Fact]
    public void Sample_ReadsTheNetworkSnapshot_WithOnDemandSamplingDisallowed()
    {
        // LinuxNetworkProvider runs a synchronous on-demand scan whenever its
        // snapshot is empty; this class polls every AppSampleIntervalSeconds
        // regardless of subscribers, so passing allowOnDemandSample: true
        // here would turn that occasional-caller fallback into continuous
        // scanning. Must always pass false.
        var (source, _, _, _, network) = Build();

        source.Sample();

        Assert.False(network.LastAllowOnDemandSample);
    }

    [Fact]
    public void Sample_ReturnsNoNetApps_OnTheFirstSample_WithNoPriorBaseline()
    {
        // The "net" AppMetricSample block itself is still added whenever the
        // provider has any entries at all (same unconditional-add shape as
        // cpu/memory/storage), but with no baseline to diff against yet its
        // Apps list is empty - AppUsageStore.Append skips writing rows for
        // an empty Apps list regardless.
        var (source, _, _, _, network) = Build();
        network.Snapshot.Add(new NetworkProcessInfo { Name = "chrome", BytesIn = 1000, BytesOut = 500 });

        var result = source.Sample();

        var net = result.SingleOrDefault(m => m.Metric == "net");
        Assert.True(net is null || net.Apps.Count == 0);
    }

    [Fact]
    public void Sample_ReportsNetDownAndUpSeparately_OnceABaselineExists()
    {
        // Two real-clock Sample() calls a moment apart can land with zero
        // elapsed ticks (ComputeNetSplitRatesCore then reports no baseline,
        // same as ComputeNetRatesCore) - this only checks that whenever a
        // rate IS reported, down and up came from independent deltas rather
        // than both being empty or both being the combined value.
        // ComputeNetSplitRatesCore's own tests above cover the exact
        // elapsed-time math deterministically.
        var (source, _, _, _, network) = Build();
        network.Snapshot.Add(new NetworkProcessInfo { Name = "chrome", BytesIn = 1000, BytesOut = 500 });
        source.Sample();

        network.Snapshot = new List<NetworkProcessInfo> { new() { Name = "chrome", BytesIn = 3000, BytesOut = 1500 } };
        var result = source.Sample();

        var netDown = result.SingleOrDefault(m => m.Metric == "net-down");
        var netUp = result.SingleOrDefault(m => m.Metric == "net-up");
        Assert.True(netDown is null || netDown.Apps.All(a => a.Value >= 0));
        Assert.True(netUp is null || netUp.Apps.All(a => a.Value >= 0));
        if (netDown is { Apps.Count: > 0 } && netUp is { Apps.Count: > 0 })
        {
            var chromeDown = netDown.Apps.Single(a => a.Name == "chrome").Value;
            var chromeUp = netUp.Apps.Single(a => a.Name == "chrome").Value;
            Assert.True(chromeDown > chromeUp);
        }
    }

    [Fact]
    public void Sample_ThenSampleAgain_NeverReportsANegativeNetRate()
    {
        // Sample() has no fixed cadence in a test, so two calls a moment
        // apart can land in the same millisecond tick (no elapsed time at
        // all, which ComputeNetRatesCore treats the same as no baseline) -
        // this only checks the second call never reports an impossible
        // negative rate, mirroring NetworkRateReaderTests' own tolerant
        // integration check for the same real-clock uncertainty.
        // ComputeNetRatesCore's own tests below cover the exact elapsed-time
        // math deterministically.
        var (source, _, _, _, network) = Build();
        network.Snapshot.Add(new NetworkProcessInfo { Name = "chrome", BytesIn = 1000, BytesOut = 500 });
        source.Sample();

        network.Snapshot = new List<NetworkProcessInfo> { new() { Name = "chrome", BytesIn = 3000, BytesOut = 1500 } };
        var net = source.Sample().SingleOrDefault(m => m.Metric == "net");

        Assert.True(net is null || net.Apps.All(a => a.Value >= 0));
    }

    [Fact]
    public void ComputeNetRatesCore_ReturnsEmpty_OnTheFirstCall_WithNoBaseline()
    {
        var current = new Dictionary<string, (long BytesIn, long BytesOut)> { ["chrome"] = (1000, 500) };

        var points = ProcessAppUsageSource.ComputeNetRatesCore(
            new Dictionary<string, (long, long)>(), prevTicksMs: -1, current, nowTicksMs: 1000);

        Assert.Empty(points);
    }

    [Fact]
    public void ComputeNetRatesCore_DividesTheCombinedInOutDelta_ByElapsedSeconds()
    {
        var prev = new Dictionary<string, (long, long)> { ["chrome"] = (1000, 500) };
        var current = new Dictionary<string, (long BytesIn, long BytesOut)> { ["chrome"] = (3000, 1500) };

        var points = ProcessAppUsageSource.ComputeNetRatesCore(prev, prevTicksMs: 0, current, nowTicksMs: 1000);

        var chrome = Assert.Single(points);
        Assert.Equal("chrome", chrome.Name);
        Assert.Equal(3000, chrome.Value); // (3000-1000)+(1500-500) bytes over 1 second
    }

    [Fact]
    public void ComputeNetRatesCore_DropsAnApp_OnANegativeDelta_FromACounterReset()
    {
        var prev = new Dictionary<string, (long, long)> { ["chrome"] = (5000, 2000) };
        var current = new Dictionary<string, (long BytesIn, long BytesOut)> { ["chrome"] = (100, 50) };

        var points = ProcessAppUsageSource.ComputeNetRatesCore(prev, prevTicksMs: 0, current, nowTicksMs: 1000);

        Assert.Empty(points);
    }

    [Fact]
    public void ComputeNetRatesCore_DropsAnAppWithNoBaselineEntry_ButKeepsOthers()
    {
        var prev = new Dictionary<string, (long, long)> { ["chrome"] = (1000, 500) };
        var current = new Dictionary<string, (long BytesIn, long BytesOut)>
        {
            ["chrome"] = (3000, 1500),
            ["new-app"] = (200, 100), // first seen this tick, no baseline yet
        };

        var points = ProcessAppUsageSource.ComputeNetRatesCore(prev, prevTicksMs: 0, current, nowTicksMs: 1000);

        var app = Assert.Single(points);
        Assert.Equal("chrome", app.Name);
    }

    [Fact]
    public void ComputeNetRatesCore_ReturnsEmpty_WhenTheElapsedGapExceedsTheThreshold()
    {
        var prev = new Dictionary<string, (long, long)> { ["chrome"] = (1000, 500) };
        var current = new Dictionary<string, (long BytesIn, long BytesOut)> { ["chrome"] = (3000, 1500) };
        var tooWide = MetricsHistory.AppSampleIntervalSeconds * 1000 * 3 + 1;

        var points = ProcessAppUsageSource.ComputeNetRatesCore(prev, prevTicksMs: 0, current, nowTicksMs: tooWide);

        Assert.Empty(points);
    }

    [Fact]
    public void ComputeNetSplitRatesCore_ReturnsEmpty_OnTheFirstCall_WithNoBaseline()
    {
        var current = new Dictionary<string, (long BytesIn, long BytesOut)> { ["chrome"] = (1000, 500) };

        var points = ProcessAppUsageSource.ComputeNetSplitRatesCore(
            new Dictionary<string, (long, long)>(), prevTicksMs: -1, current, nowTicksMs: 1000);

        Assert.Empty(points);
    }

    [Fact]
    public void ComputeNetSplitRatesCore_DividesEachDirectionsDelta_ByElapsedSeconds_Separately()
    {
        var prev = new Dictionary<string, (long, long)> { ["chrome"] = (1000, 500) };
        var current = new Dictionary<string, (long BytesIn, long BytesOut)> { ["chrome"] = (3000, 1500) };

        var points = ProcessAppUsageSource.ComputeNetSplitRatesCore(prev, prevTicksMs: 0, current, nowTicksMs: 1000);

        var chrome = Assert.Single(points);
        Assert.Equal("chrome", chrome.Name);
        Assert.Equal(2000, chrome.DownBytesPerSec); // 3000-1000 bytes over 1 second
        Assert.Equal(1000, chrome.UpBytesPerSec); // 1500-500 bytes over 1 second
    }

    [Fact]
    public void ComputeNetSplitRatesCore_DropsAnApp_OnANegativeDeltaInEitherDirection()
    {
        var prev = new Dictionary<string, (long, long)> { ["chrome"] = (5000, 2000) };
        var current = new Dictionary<string, (long BytesIn, long BytesOut)> { ["chrome"] = (100, 50) };

        var points = ProcessAppUsageSource.ComputeNetSplitRatesCore(prev, prevTicksMs: 0, current, nowTicksMs: 1000);

        Assert.Empty(points);
    }

    [Fact]
    public void ComputeNetSplitRatesCore_DropsAnAppWithNoBaselineEntry_ButKeepsOthers()
    {
        var prev = new Dictionary<string, (long, long)> { ["chrome"] = (1000, 500) };
        var current = new Dictionary<string, (long BytesIn, long BytesOut)>
        {
            ["chrome"] = (3000, 1500),
            ["new-app"] = (200, 100), // first seen this tick, no baseline yet
        };

        var points = ProcessAppUsageSource.ComputeNetSplitRatesCore(prev, prevTicksMs: 0, current, nowTicksMs: 1000);

        var app = Assert.Single(points);
        Assert.Equal("chrome", app.Name);
    }

    [Fact]
    public void ComputeNetSplitRatesCore_ReturnsEmpty_WhenTheElapsedGapExceedsTheThreshold()
    {
        var prev = new Dictionary<string, (long, long)> { ["chrome"] = (1000, 500) };
        var current = new Dictionary<string, (long BytesIn, long BytesOut)> { ["chrome"] = (3000, 1500) };
        var tooWide = MetricsHistory.AppSampleIntervalSeconds * 1000 * 3 + 1;

        var points = ProcessAppUsageSource.ComputeNetSplitRatesCore(prev, prevTicksMs: 0, current, nowTicksMs: tooWide);

        Assert.Empty(points);
    }

    [Fact]
    public void Sample_MapsGpuEntries_ToMetricId_ViaMatchingAdapterLuid()
    {
        var (source, _, gpuProcesses, sensors, _) = Build();
        sensors.Gpus.Add(new GpuReadout { Id = "/gpu-nvidia/0", Name = "RTX 5080", AdapterLuid = "10:20" });
        gpuProcesses.SetSnapshotForTest(new[]
        {
            new GpuProcessEntry { Name = "game.exe", GpuPercent = 40, DedicatedMb = 2048, AdapterLuid = "10:20" },
        });

        var gpu = source.Sample().Single(m => m.Metric == "gpu:gpu-nvidia-0");

        var entry = gpu.Apps.Single();
        Assert.Equal("game.exe", entry.Name);
        Assert.Equal(40, entry.Value);
        Assert.Equal(2048, entry.VramMb);
    }

    [Fact]
    public void Sample_ReadsGpusOnce_WhileEveryAdapterLuidIsAlreadyMapped()
    {
        var (source, _, gpuProcesses, sensors, _) = Build();
        sensors.Gpus.Add(new GpuReadout { Id = "/gpu-nvidia/0", Name = "RTX 5080", AdapterLuid = "10:20" });
        gpuProcesses.SetSnapshotForTest(new[]
        {
            new GpuProcessEntry { Name = "game.exe", GpuPercent = 40, DedicatedMb = 2048, AdapterLuid = "10:20" },
            new GpuProcessEntry { Name = "soft.exe", GpuPercent = 1, DedicatedMb = 1, AdapterLuid = "99:99" },
        });

        source.Sample();
        source.Sample();
        source.Sample();

        // The unmapped 99:99 adapter does not force a rebuild either.
        Assert.Equal(1, sensors.GetGpusCalls);
    }

    [Fact]
    public void Sample_ReadsGpusAgain_WhileTheLastReadMappedNothing()
    {
        // Sensors not enumerated yet at the first sample (boot): the empty
        // map must not be trusted once the adapters do appear.
        var (source, _, gpuProcesses, sensors, _) = Build();
        gpuProcesses.SetSnapshotForTest(new[]
        {
            new GpuProcessEntry { Name = "game.exe", GpuPercent = 40, DedicatedMb = 2048, AdapterLuid = "10:20" },
        });
        source.Sample();

        sensors.Gpus.Add(new GpuReadout { Id = "/gpu-nvidia/0", Name = "RTX 5080", AdapterLuid = "10:20" });
        var result = source.Sample();

        Assert.Equal(2, sensors.GetGpusCalls);
        Assert.Contains(result, m => m.Metric == "gpu:gpu-nvidia-0");
    }

    [Fact]
    public void Sample_ReadsGpusAgain_WhenANewAdapterLuidAppears()
    {
        var (source, _, gpuProcesses, sensors, _) = Build();
        sensors.Gpus.Add(new GpuReadout { Id = "/gpu-nvidia/0", Name = "RTX 5080", AdapterLuid = "10:20" });
        gpuProcesses.SetSnapshotForTest(new[]
        {
            new GpuProcessEntry { Name = "game.exe", GpuPercent = 40, DedicatedMb = 2048, AdapterLuid = "10:20" },
        });
        source.Sample();

        sensors.Gpus.Add(new GpuReadout { Id = "/gpu-amd/0", Name = "RX 7800", AdapterLuid = "30:40" });
        gpuProcesses.SetSnapshotForTest(new[]
        {
            new GpuProcessEntry { Name = "game.exe", GpuPercent = 40, DedicatedMb = 2048, AdapterLuid = "10:20" },
            new GpuProcessEntry { Name = "other.exe", GpuPercent = 20, DedicatedMb = 512, AdapterLuid = "30:40" },
        });

        var result = source.Sample();

        Assert.Equal(2, sensors.GetGpusCalls);
        Assert.Contains(result, m => m.Metric == "gpu:gpu-amd-0");
    }

    [Fact]
    public void Sample_SkipsGpuEntries_WhenAdapterLuidHasNoMatchingScalarGpu()
    {
        var (source, _, gpuProcesses, sensors, _) = Build();
        sensors.Gpus.Add(new GpuReadout { Id = "/gpu-nvidia/0", Name = "RTX 5080", AdapterLuid = "10:20" });
        gpuProcesses.SetSnapshotForTest(new[]
        {
            new GpuProcessEntry { Name = "game.exe", GpuPercent = 40, DedicatedMb = 2048, AdapterLuid = "99:99" },
        });

        var result = source.Sample();

        Assert.DoesNotContain(result, m => m.Metric.StartsWith("gpu:", StringComparison.Ordinal));
    }

    [Fact]
    public void Sample_MapsGpuEntries_ToVramMetricId_ViaMatchingAdapterLuid()
    {
        var (source, _, gpuProcesses, sensors, _) = Build();
        sensors.Gpus.Add(new GpuReadout { Id = "/gpu-nvidia/0", Name = "RTX 5080", AdapterLuid = "10:20" });
        gpuProcesses.SetSnapshotForTest(new[]
        {
            new GpuProcessEntry { Name = "game.exe", GpuPercent = 40, DedicatedMb = 2048, AdapterLuid = "10:20" },
        });

        var vram = source.Sample().Single(m => m.Metric == "vram:gpu-nvidia-0");

        var entry = vram.Apps.Single();
        Assert.Equal("game.exe", entry.Name);
        Assert.Equal(2048, entry.Value);
        Assert.Null(entry.VramMb);
    }

    [Fact]
    public void Sample_RanksVramMetric_ByDedicatedMb_IndependentlyOfGpuPercent()
    {
        // "idle-hog" barely touches the GPU but holds far more VRAM than
        // "busy-light" - the vram ranking must put it first even though the
        // gpu (load) ranking would not.
        var (source, _, gpuProcesses, sensors, _) = Build();
        sensors.Gpus.Add(new GpuReadout { Id = "/gpu-nvidia/0", Name = "RTX 5080", AdapterLuid = "10:20" });
        gpuProcesses.SetSnapshotForTest(new[]
        {
            new GpuProcessEntry { Name = "idle-hog", GpuPercent = 1, DedicatedMb = 4096, AdapterLuid = "10:20" },
            new GpuProcessEntry { Name = "busy-light", GpuPercent = 50, DedicatedMb = 100, AdapterLuid = "10:20" },
        });

        var vram = source.Sample().Single(m => m.Metric == "vram:gpu-nvidia-0");
        var gpu = source.Sample().Single(m => m.Metric == "gpu:gpu-nvidia-0");

        Assert.Equal("idle-hog", vram.Apps.First().Name);
        Assert.Equal("busy-light", gpu.Apps.First().Name);
    }

    [Fact]
    public void Sample_ExcludesAnEntryWithExactlyZeroDedicatedMb_FromTheVramMetric()
    {
        var (source, _, gpuProcesses, sensors, _) = Build();
        sensors.Gpus.Add(new GpuReadout { Id = "/gpu-nvidia/0", Name = "RTX 5080", AdapterLuid = "10:20" });
        gpuProcesses.SetSnapshotForTest(new[]
        {
            new GpuProcessEntry { Name = "no-vram.exe", GpuPercent = 5, DedicatedMb = 0, AdapterLuid = "10:20" },
            new GpuProcessEntry { Name = "some-vram.exe", GpuPercent = 5, DedicatedMb = 10, AdapterLuid = "10:20" },
        });

        var vram = source.Sample().Single(m => m.Metric == "vram:gpu-nvidia-0");

        Assert.DoesNotContain(vram.Apps, a => a.Name == "no-vram.exe");
        Assert.Contains(vram.Apps, a => a.Name == "some-vram.exe");
    }

    [Fact]
    public void Sample_SkipsVramEntries_WhenAdapterLuidHasNoMatchingScalarGpu()
    {
        var (source, _, gpuProcesses, sensors, _) = Build();
        sensors.Gpus.Add(new GpuReadout { Id = "/gpu-nvidia/0", Name = "RTX 5080", AdapterLuid = "10:20" });
        gpuProcesses.SetSnapshotForTest(new[]
        {
            new GpuProcessEntry { Name = "game.exe", GpuPercent = 40, DedicatedMb = 2048, AdapterLuid = "99:99" },
        });

        var result = source.Sample();

        Assert.DoesNotContain(result, m => m.Metric.StartsWith("vram:", StringComparison.Ordinal));
    }
}
