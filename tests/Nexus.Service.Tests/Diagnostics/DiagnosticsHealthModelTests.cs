using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Diagnostics;
using Nexus.Service.Diagnostics.Cooling;
using Nexus.Service.Diagnostics.EventLog;
using Nexus.Service.Diagnostics.Gpu;
using Nexus.Service.Diagnostics.Memory;
using Nexus.Service.Diagnostics.Storage;
using Nexus.Service.Diagnostics.SystemInfo;
using Nexus.Service.Diagnostics.Temperature;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics;

public class DiagnosticsHealthModelTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly SmartSnapshot EmptySmart = new() { Supported = true, Drives = Array.Empty<SmartDriveInfo>() };
    private static readonly CoolingStallSnapshot EmptyCooling = new(true, Array.Empty<CoolingStallDevice>());
    private static readonly PnpProblemSnapshot EmptyPnp = new(true, Array.Empty<PnpProblemDevice>());

    private static DiagnosticsHealthResponse Compute(
        SmartSnapshot? smart = null,
        CoolingStallSnapshot? cooling = null,
        GpuHealthSnapshot? gpu = null,
        IReadOnlyDictionary<string, int>? counts30d = null,
        MemoryTestResult? lastMemoryTest = null,
        PnpProblemSnapshot? pnp = null,
        IReadOnlyList<string>? knownGpuModels = null,
        IReadOnlyList<TemperatureEpisode>? tempEpisodes = null,
        DateTime? generatedAtUtc = null,
        DiagnosticsSettings? diagnostics = null)
    {
        return DiagnosticsHealthModel.Compute(
            smart: smart ?? EmptySmart,
            cooling: cooling ?? EmptyCooling,
            gpu: gpu ?? GpuHealthSnapshot.Unsupported,
            counts30d: counts30d ?? new Dictionary<string, int>(),
            lastMemoryTest: lastMemoryTest,
            pnp: pnp ?? EmptyPnp,
            knownGpuModels: knownGpuModels ?? Array.Empty<string>(),
            windowsSupported: true,
            generatedAtUtc: generatedAtUtc ?? T0,
            tempEpisodes: tempEpisodes,
            diagnostics: diagnostics);
    }

    [Fact]
    public void StorageDriveAct_PropagatesToComponentAndOverall()
    {
        var smart = new SmartSnapshot
        {
            Supported = true,
            Drives = new List<SmartDriveInfo>
            {
                new()
                {
                    Id = "storage:ABC123",
                    Name = "Test SSD",
                    Status = "warning",
                    DetailedReasons = new List<SmartReason>
                    {
                        new("smart.reallocated", ReasonSeverity.Act, "5 reallocated sectors", "detail"),
                    },
                },
            },
        };

        var result = Compute(smart: smart);

        var storage = Assert.Single(result.Components, c => c.Kind == "storage");
        Assert.Equal(HealthStatuses.Act, storage.Status);
        Assert.Equal(HealthStatuses.Act, result.Overall);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(25)]
    public void GpuTdrAndDriverErrorCounts_NeverAffectStatus(int count)
    {
        var gpu = new GpuHealthSnapshot(true, new List<GpuInfo>
        {
            new("Test GPU", "1.0", 50, 100, new GpuThrottleInfo(Array.Empty<string>(), null, null, null, null)),
        });
        var counts = new Dictionary<string, int>
        {
            [DiagnosticEventCatalog.SourceTdr] = count,
            [DiagnosticEventCatalog.SourceGpuDriver] = count,
        };

        var result = Compute(gpu: gpu, counts30d: counts);

        var gpuComponent = Assert.Single(result.Components, c => c.Kind == "gpu");
        Assert.Equal(HealthStatuses.Ok, gpuComponent.Status);
        Assert.Empty(gpuComponent.Reasons);
    }

    [Fact]
    public void DirtyShutdownsBugchecksAndWhea_NeverAffectStatus_RegardlessOfCount()
    {
        var counts = new Dictionary<string, int>
        {
            [DiagnosticEventCatalog.SourceDirtyShutdown] = 1000,
            [DiagnosticEventCatalog.SourceBugcheck] = 1000,
            [DiagnosticEventCatalog.SourceWhea] = 1000,
        };

        var result = Compute(counts30d: counts);

        var system = Assert.Single(result.Components, c => c.Kind == "system");
        Assert.Equal(HealthStatuses.Ok, system.Status);
        Assert.Empty(system.Reasons);

        var memory = Assert.Single(result.Components, c => c.Kind == "memory");
        Assert.Equal(HealthStatuses.Ok, memory.Status);
        Assert.Empty(memory.Reasons);

        Assert.Equal(HealthStatuses.Ok, result.Overall);
    }

    [Fact]
    public void IgnoredComponent_IsDroppedFromComponentsAndOverall()
    {
        var smart = new SmartSnapshot
        {
            Supported = true,
            Drives = new List<SmartDriveInfo>
            {
                new()
                {
                    Id = "storage:Z52AFCNF",
                    Name = "ST2000VX008-2E3164",
                    Status = "caution",
                    DetailedReasons = new List<SmartReason>
                    {
                        new("smart.commandTimeout", ReasonSeverity.Watch, "1 command timeout", "SMART attribute 188 raw value is 1."),
                    },
                },
                new() { Id = "storage:OTHER", Name = "Healthy", Status = "good" },
            },
        };
        var cooling = new CoolingStallSnapshot(true, new List<CoolingStallDevice>
        {
            new("pump-1", "Pump", "pump", 0, 60, CoolingStallStatuses.Stalled, T0),
        });
        var settings = new DiagnosticsSettings
        {
            IgnoredComponents = new List<string> { "storage:Z52AFCNF", "cooling:pump-1" },
        };

        var result = Compute(smart: smart, cooling: cooling, diagnostics: settings);

        Assert.DoesNotContain(result.Components, c => c.Id == "storage:Z52AFCNF");
        Assert.DoesNotContain(result.Components, c => c.Id == "cooling:pump-1");
        Assert.Contains(result.Components, c => c.Id == "storage:OTHER");
        Assert.Equal(HealthStatuses.Ok, result.Overall);
    }

    [Fact]
    public void IgnoredDrive_TemperatureEpisode_DoesNotReachTheCoolingAggregate()
    {
        var episodes = new List<TemperatureEpisode>
        {
            new("storage:Z52AFCNF", "ST2000VX008", T0.AddMinutes(-10), T0, PeakC: 74.5, ThresholdC: 70, Kind: "storage"),
            new("storage:OTHER", "Healthy", T0.AddMinutes(-10), T0, PeakC: 72.0, ThresholdC: 70, Kind: "storage"),
        };
        var settings = new DiagnosticsSettings { IgnoredComponents = new List<string> { "storage:Z52AFCNF" } };

        var result = Compute(tempEpisodes: episodes, diagnostics: settings);

        var cooling = Assert.Single(result.Components, c => c.Id == "cooling");
        var reason = Assert.Single(cooling.Reasons);
        Assert.Contains("componentId=storage:OTHER", reason.Detail);
        Assert.Equal(HealthStatuses.Watch, result.Overall);
    }

    [Fact]
    public void BuildHealth_RecomputesWhenTheIgnoreListChanges_WithinTheCacheTtl()
    {
        var store = new InMemoryConfigStore();
        var model = new DiagnosticsHealthModel(
            new SmartHealthMonitor(), new CoolingStallDetector(), new GpuHealthMonitor(), new EventLogMonitor(),
            new MemoryDiagnosticOrchestrator(), new PnpProblemScanner(), new Mcp.McpTestHarness.StubSensorProvider(),
            new InMemoryMetricsHistoryStore(), store);

        var first = model.BuildHealth();
        Assert.Same(first, model.BuildHealth());

        store.Update(s => s.Diagnostics.IgnoredComponents = new List<string> { "storage:Z52AFCNF" });
        var afterIgnore = model.BuildHealth();
        Assert.NotSame(first, afterIgnore);
        Assert.Same(afterIgnore, model.BuildHealth());

        store.Update(s => s.Diagnostics.IgnoredComponents = null!);
        Assert.NotSame(afterIgnore, model.BuildHealth());
    }

    [Fact]
    public void IgnoringEveryComponent_OffWindows_KeepsTheGridSupported()
    {
        var smart = new SmartSnapshot
        {
            Supported = true,
            Drives = new List<SmartDriveInfo> { new() { Id = "storage:ONLY", Name = "Only", Status = "caution" } },
        };
        var settings = new DiagnosticsSettings { IgnoredComponents = new List<string> { "storage:ONLY" } };

        var result = DiagnosticsHealthModel.Compute(
            smart: smart,
            cooling: EmptyCooling,
            gpu: GpuHealthSnapshot.Unsupported,
            counts30d: new Dictionary<string, int>(),
            lastMemoryTest: null,
            pnp: EmptyPnp,
            knownGpuModels: Array.Empty<string>(),
            windowsSupported: false,
            generatedAtUtc: T0,
            diagnostics: settings);

        Assert.True(result.Supported);
        Assert.Empty(result.Components);
        Assert.Equal(HealthStatuses.Ok, result.Overall);
    }

    [Fact]
    public void OverallStatus_IsWorstAcrossAllComponents()
    {
        // pnp problems -> system component at "watch"; a bad storage drive -> "act".
        // Overall must reflect the worst of the two, not the last one computed.
        var smart = new SmartSnapshot
        {
            Supported = true,
            Drives = new List<SmartDriveInfo>
            {
                new()
                {
                    Id = "storage:XYZ",
                    Name = "Failing Drive",
                    Status = "bad",
                    DetailedReasons = new List<SmartReason>
                    {
                        new("nvme.criticalWarning", ReasonSeverity.Act, "critical", "detail"),
                    },
                },
            },
        };
        var pnp = new PnpProblemSnapshot(true, new List<PnpProblemDevice> { new("Bad Device", "PCI\\1234", 43, "CM_PROB_FAILED_POST_START") });

        var result = Compute(smart: smart, pnp: pnp);

        var system = Assert.Single(result.Components, c => c.Kind == "system");
        Assert.Equal(HealthStatuses.Watch, system.Status);
        var storage = Assert.Single(result.Components, c => c.Kind == "storage");
        Assert.Equal(HealthStatuses.Act, storage.Status);
        Assert.Equal(HealthStatuses.Act, result.Overall);
    }

    [Fact]
    public void Cooling_StalledPump_ProducesActComponent_PlusHealthyAggregate()
    {
        var cooling = new CoolingStallSnapshot(true, new List<CoolingStallDevice>
        {
            new("pump1", "Q60 Pump", "pump", 0, 60, CoolingStallStatuses.Stalled, T0),
            new("fan1", "Front Fan", "fan", 1200, 50, CoolingStallStatuses.Ok, null),
        });

        var result = Compute(cooling: cooling);

        var stalled = Assert.Single(result.Components, c => c.Id == "cooling:pump1");
        Assert.Equal(HealthStatuses.Act, stalled.Status);
        Assert.Equal("cooling.pumpStall", Assert.Single(stalled.Reasons).Code);

        var aggregate = Assert.Single(result.Components, c => c.Id == "cooling");
        Assert.Equal(HealthStatuses.Ok, aggregate.Status);
        Assert.Equal(HealthStatuses.Act, result.Overall);
    }

    [Fact]
    public void Cooling_EvaluatedEvenWhenNotWindowsSupported()
    {
        var cooling = new CoolingStallSnapshot(true, new List<CoolingStallDevice>
        {
            new("pump1", "Q60 Pump", "pump", 0, 60, CoolingStallStatuses.Stalled, T0),
        });

        var result = DiagnosticsHealthModel.Compute(
            smart: SmartSnapshotUnsupported(),
            cooling: cooling,
            gpu: GpuHealthSnapshot.Unsupported,
            counts30d: new Dictionary<string, int>(),
            lastMemoryTest: null,
            pnp: PnpProblemSnapshot.Unsupported,
            knownGpuModels: Array.Empty<string>(),
            windowsSupported: false,
            generatedAtUtc: T0);

        // A working sub-domain (cooling) produced a component, so the grid is
        // supported off-Windows too.
        Assert.True(result.Supported);
        Assert.Single(result.Components, c => c.Kind == "cooling" && c.Status == HealthStatuses.Act);
        Assert.DoesNotContain(result.Components, c => c.Kind is "storage" or "gpu" or "memory" or "system");
        Assert.Equal(HealthStatuses.Act, result.Overall);
    }

    [Fact]
    public void StorageAndGpu_ContributeOffWindows_WhenTheirSnapshotsAreSupported()
    {
        var smart = new SmartSnapshot
        {
            Supported = true,
            Drives = new List<SmartDriveInfo>
            {
                new() { Id = "storage:NVME0", Name = "Linux NVMe", Status = "good", DetailedReasons = new List<SmartReason>() },
            },
        };
        var gpu = new GpuHealthSnapshot(true, new List<GpuInfo>
        {
            new("Linux GPU", "1.0", 50, 100, new GpuThrottleInfo(Array.Empty<string>(), null, null, null, null)),
        });

        var result = DiagnosticsHealthModel.Compute(
            smart: smart,
            cooling: EmptyCooling,
            gpu: gpu,
            counts30d: new Dictionary<string, int>(),
            lastMemoryTest: null,
            pnp: EmptyPnp,
            knownGpuModels: Array.Empty<string>(),
            windowsSupported: false,
            generatedAtUtc: T0);

        // SMART (smartctl) and GPU (NVML) health surface off Windows; memory
        // and system/pnp stay Windows-only, so no tile appears for them.
        Assert.True(result.Supported);
        Assert.Single(result.Components, c => c.Kind == "storage");
        Assert.Single(result.Components, c => c.Kind == "gpu");
        Assert.DoesNotContain(result.Components, c => c.Kind is "memory" or "system");
    }

    [Fact]
    public void GpuPlaceholder_FromKnownModels_StaysWindowsOnly()
    {
        // Off Windows with no live GPU health (NVML unsupported) but a model
        // name present (system_profiler/lspci), the Unknown placeholder tile
        // must NOT appear - it would flip Supported true with no real signal.
        var offWindows = DiagnosticsHealthModel.Compute(
            smart: SmartSnapshotUnsupported(),
            cooling: new CoolingStallSnapshot(true, new List<CoolingStallDevice>()),
            gpu: GpuHealthSnapshot.Unsupported,
            counts30d: new Dictionary<string, int>(),
            lastMemoryTest: null,
            pnp: PnpProblemSnapshot.Unsupported,
            knownGpuModels: new[] { "Apple M1 Max" },
            windowsSupported: false,
            generatedAtUtc: T0);
        Assert.DoesNotContain(offWindows.Components, c => c.Kind == "gpu");
        Assert.False(offWindows.Supported);

        // On Windows the same inputs still produce the Unknown placeholder.
        var onWindows = DiagnosticsHealthModel.Compute(
            smart: SmartSnapshotUnsupported(),
            cooling: new CoolingStallSnapshot(true, new List<CoolingStallDevice>()),
            gpu: GpuHealthSnapshot.Unsupported,
            counts30d: new Dictionary<string, int>(),
            lastMemoryTest: null,
            pnp: PnpProblemSnapshot.Unsupported,
            knownGpuModels: new[] { "NVIDIA GeForce RTX 4080" },
            windowsSupported: true,
            generatedAtUtc: T0);
        Assert.Single(onWindows.Components, c => c.Kind == "gpu");
    }

    [Fact]
    public void GpuPlaceholder_CarriesAnUnknownReason_ExplainingTheMissingSource()
    {
        var result = Compute(knownGpuModels: new[] { "AMD Radeon RX 6800" });

        var gpu = Assert.Single(result.Components, c => c.Kind == "gpu");
        Assert.Equal(HealthStatuses.Unknown, gpu.Status);
        var reason = Assert.Single(gpu.Reasons);
        Assert.Equal("gpu.noHealthSource", reason.Code);
        Assert.Equal(HealthStatuses.Unknown, reason.Severity);
        Assert.NotEmpty(reason.Summary);
        Assert.NotEmpty(reason.Detail);
    }

    [Fact]
    public void GpuPlaceholder_DoesNotMaskHealthySiblings_InOverall()
    {
        // A probe with no source is not a finding about the machine.
        var result = Compute(knownGpuModels: new[] { "AMD Radeon RX 6800" });

        Assert.Contains(result.Components, c => c.Kind == "gpu" && c.Status == HealthStatuses.Unknown);
        Assert.Contains(result.Components, c => c.Status == HealthStatuses.Ok);
        Assert.Equal(HealthStatuses.Ok, result.Overall);
    }

    [Fact]
    public void DriveWithoutSmart_CarriesAnUnknownReason_AndLeavesHealthyDrivesHealthy()
    {
        var smart = new SmartSnapshot
        {
            Supported = true,
            Drives = new List<SmartDriveInfo>
            {
                new() { Id = "storage:NVME1", Name = "Healthy NVMe", Status = "good" },
                new() { Id = "storage:USB1", Name = "USB Enclosure", Status = "unknown" },
            },
        };

        var result = Compute(smart: smart);

        var unmeasured = Assert.Single(result.Components, c => c.Id == "storage:USB1");
        Assert.Equal(HealthStatuses.Unknown, unmeasured.Status);
        var reason = Assert.Single(unmeasured.Reasons);
        Assert.Equal("storage.noSmartSource", reason.Code);
        Assert.Equal(HealthStatuses.Unknown, reason.Severity);
        Assert.Equal(HealthStatuses.Ok, result.Overall);
    }

    [Fact]
    public void EveryUnknownComponent_ExplainsItself()
    {
        // The aggregation lets a healthy sibling outrank an unknown, so an
        // unknown that carries no reason would vanish from the UI silently.
        var smart = new SmartSnapshot
        {
            Supported = true,
            Drives = new List<SmartDriveInfo>
            {
                new() { Id = "storage:USB1", Name = "USB Enclosure", Status = "unknown" },
                new() { Id = "storage:ODD", Name = "", Status = "unrecognized-vendor-word" },
            },
        };

        var result = Compute(smart: smart, knownGpuModels: new[] { "AMD Radeon RX 6800" });

        Assert.Contains(result.Components, c => c.Status == HealthStatuses.Unknown);
        Assert.All(
            result.Components.Where(c => c.Status == HealthStatuses.Unknown),
            c => Assert.NotEmpty(c.Reasons));
    }

    [Fact]
    public void OverallUnknown_OnlyWhenNoComponentProducedARealSignal()
    {
        // Storage unsupported, memory/system gated off: the placeholder is the
        // only component, so there is nothing to be healthy about.
        var diagnostics = new DiagnosticsSettings();
        diagnostics.Components.Ram = false;
        diagnostics.Components.System = false;

        var result = Compute(
            smart: SmartSnapshotUnsupported(),
            knownGpuModels: new[] { "AMD Radeon RX 6800" },
            diagnostics: diagnostics);

        var gpu = Assert.Single(result.Components);
        Assert.Equal(HealthStatuses.Unknown, gpu.Status);
        Assert.Equal(HealthStatuses.Unknown, result.Overall);
    }

    [Fact]
    public void NotSupported_OffWindows_WhenNoSubDomainProducedAComponent()
    {
        var result = DiagnosticsHealthModel.Compute(
            smart: SmartSnapshotUnsupported(),
            cooling: new CoolingStallSnapshot(true, new List<CoolingStallDevice>()),
            gpu: GpuHealthSnapshot.Unsupported,
            counts30d: new Dictionary<string, int>(),
            lastMemoryTest: null,
            pnp: PnpProblemSnapshot.Unsupported,
            knownGpuModels: Array.Empty<string>(),
            windowsSupported: false,
            generatedAtUtc: T0);

        Assert.False(result.Supported);
        Assert.Empty(result.Components);
    }

    [Fact]
    public void MemoryTestFailed_IsAct()
    {
        var lastTest = new MemoryTestResult(T0, MemoryTestResult.Failed, "1202 error");

        var result = Compute(lastMemoryTest: lastTest);

        var memory = Assert.Single(result.Components, c => c.Kind == "memory");
        Assert.Equal(HealthStatuses.Act, memory.Status);
    }

    private static SmartSnapshot SmartSnapshotUnsupported() => new() { Supported = false, Drives = Array.Empty<SmartDriveInfo>() };

    [Fact]
    public void SustainedHighTemp_OngoingEpisode_AddsWatchReasonToCoolingAggregate()
    {
        // Ends exactly at "now" - still hot at generation time.
        var episode = new TemperatureEpisode("gpu:0", "RTX 5080", T0.AddHours(-1), T0, 91.5, 85, "gpu");

        var result = Compute(tempEpisodes: new[] { episode });

        var aggregate = Assert.Single(result.Components, c => c.Id == "cooling");
        Assert.Equal(HealthStatuses.Watch, aggregate.Status);
        var reason = Assert.Single(aggregate.Reasons);
        Assert.Equal("cooling.sustainedHighTemp", reason.Code);
        Assert.Equal(HealthStatuses.Watch, reason.Severity);
        Assert.Contains("RTX 5080", reason.Summary);
        Assert.Equal(HealthStatuses.Watch, result.Overall);
    }

    [Fact]
    public void SustainedHighTemp_EpisodeEndedBeforeTheRecencyWindow_ProducesNoReason()
    {
        // Default linger is 0, so the recency window is one bucket (5 min);
        // an episode that cooled 20 minutes ago is well outside it.
        var episode = new TemperatureEpisode("gpu:0", "RTX 5080", T0.AddHours(-1), T0.AddMinutes(-20), 91.5, 85, "gpu");

        var result = Compute(tempEpisodes: new[] { episode });

        Assert.DoesNotContain(result.Components, c => c.Id == "cooling");
        Assert.Equal(HealthStatuses.Ok, result.Overall);
    }

    [Fact]
    public void SustainedHighTemp_NoEpisodes_AggregateOmittedWhenNoCoolingDevicesEither()
    {
        var result = Compute();

        Assert.DoesNotContain(result.Components, c => c.Id == "cooling");
    }

    [Fact]
    public void SustainedHighTemp_CombinesWithStalledPump_WorstStatusWins()
    {
        var cooling = new CoolingStallSnapshot(true, new List<CoolingStallDevice>
        {
            new("pump1", "Q60 Pump", "pump", 0, 60, CoolingStallStatuses.Stalled, T0),
        });
        var episode = new TemperatureEpisode("cpu", "CPU", T0.AddHours(-1), T0, 92, 90, "cpu");

        var result = Compute(cooling: cooling, tempEpisodes: new[] { episode });

        var stalled = Assert.Single(result.Components, c => c.Id == "cooling:pump1");
        Assert.Equal(HealthStatuses.Act, stalled.Status);
        var aggregate = Assert.Single(result.Components, c => c.Id == "cooling");
        Assert.Equal(HealthStatuses.Watch, aggregate.Status);
        Assert.Equal(HealthStatuses.Act, result.Overall);
    }

    [Fact]
    public void WarningLingerMinutes_KeepsAPastEpisodeVisibleWithinTheLingerWindow()
    {
        var diagnostics = new DiagnosticsSettings { WarningLingerMinutes = 30 };
        var episode = new TemperatureEpisode("gpu:0", "RTX 5080", T0.AddHours(-1), T0.AddMinutes(-20), 91.5, 85, "gpu");

        var result = Compute(tempEpisodes: new[] { episode }, diagnostics: diagnostics);

        var aggregate = Assert.Single(result.Components, c => c.Id == "cooling");
        Assert.Equal(HealthStatuses.Watch, aggregate.Status);
    }

    [Fact]
    public void WarningLingerMinutes_DoesNotKeepAnEpisodeVisibleBeyondTheLingerWindow()
    {
        var diagnostics = new DiagnosticsSettings { WarningLingerMinutes = 30 };
        var episode = new TemperatureEpisode("gpu:0", "RTX 5080", T0.AddHours(-1), T0.AddMinutes(-40), 91.5, 85, "gpu");

        var result = Compute(tempEpisodes: new[] { episode }, diagnostics: diagnostics);

        Assert.DoesNotContain(result.Components, c => c.Id == "cooling");
    }

    [Fact]
    public void DisabledStorageComponent_ExcludedFromStatusAndOverall()
    {
        var smart = new SmartSnapshot
        {
            Supported = true,
            Drives = new List<SmartDriveInfo>
            {
                new()
                {
                    Id = "storage:ABC123",
                    Name = "Test SSD",
                    Status = "bad",
                    DetailedReasons = new List<SmartReason>
                    {
                        new("nvme.criticalWarning", ReasonSeverity.Act, "critical", "detail"),
                    },
                },
            },
        };
        var diagnostics = new DiagnosticsSettings();
        diagnostics.Components.Storage = false;

        var result = Compute(smart: smart, diagnostics: diagnostics);

        Assert.DoesNotContain(result.Components, c => c.Kind == "storage");
        Assert.Equal(HealthStatuses.Ok, result.Overall);
    }

    [Fact]
    public void DisabledCpuComponent_ExcludesCpuTempEpisodeFromCoolingAggregate()
    {
        var diagnostics = new DiagnosticsSettings();
        diagnostics.Components.Cpu = false;
        var episode = new TemperatureEpisode("cpu", "CPU", T0.AddHours(-1), T0, 95, 90, "cpu");

        var result = Compute(tempEpisodes: new[] { episode }, diagnostics: diagnostics);

        Assert.DoesNotContain(result.Components, c => c.Id == "cooling");
        Assert.Equal(HealthStatuses.Ok, result.Overall);
    }

    [Fact]
    public void DisabledCoolingComponent_ExcludesStallDevices_ButNotOtherKindsTempWarnings()
    {
        var cooling = new CoolingStallSnapshot(true, new List<CoolingStallDevice>
        {
            new("pump1", "Q60 Pump", "pump", 0, 60, CoolingStallStatuses.Stalled, T0),
        });
        var diagnostics = new DiagnosticsSettings();
        diagnostics.Components.Cooling = false;
        var episode = new TemperatureEpisode("gpu:0", "RTX 5080", T0.AddHours(-1), T0, 95, 85, "gpu");

        var result = Compute(cooling: cooling, tempEpisodes: new[] { episode }, diagnostics: diagnostics);

        Assert.DoesNotContain(result.Components, c => c.Id == "cooling:pump1");
        var aggregate = Assert.Single(result.Components, c => c.Id == "cooling");
        Assert.Equal(HealthStatuses.Watch, aggregate.Status);
    }

    [Fact]
    public void DisabledGpuComponent_ExcludesGpuThrottleComponent()
    {
        var gpu = new GpuHealthSnapshot(true, new List<GpuInfo>
        {
            new("Test GPU", "1.0", 50, 100, new GpuThrottleInfo(new[] { "hwThermal" }, null, null, null, null)),
        });
        var diagnostics = new DiagnosticsSettings();
        diagnostics.Components.Gpu = false;

        var result = Compute(gpu: gpu, diagnostics: diagnostics);

        Assert.DoesNotContain(result.Components, c => c.Kind == "gpu");
        Assert.Equal(HealthStatuses.Ok, result.Overall);
    }
}
