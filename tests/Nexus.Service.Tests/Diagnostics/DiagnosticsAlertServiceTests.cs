using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Diagnostics;
using Nexus.Service.Diagnostics.Cooling;
using Nexus.Service.Diagnostics.EventLog;
using Nexus.Service.Diagnostics.Gpu;
using Nexus.Service.Diagnostics.Memory;
using Nexus.Service.Diagnostics.Storage;
using Nexus.Service.Diagnostics.SystemInfo;
using Nexus.Service.Lifecycle;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics;

public class DiagnosticsAlertServiceTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Throws only where BuildHealth reaches it (GetGpuModels, evaluated after
    // every Snapshot()/LastResult() call in the Compute() argument list) - a
    // gate-off Tick that reaches this stub without throwing proves BuildHealth
    // was never called, the same shell GET /diagnostics/health returns.
    private sealed class ThrowingSensorProvider : ISensorProvider
    {
        public string GetCpuModel() => "";
        public IReadOnlyList<HardwareSensor> GetCpuSensors() => Array.Empty<HardwareSensor>();
        public (bool Healthy, float DistanceToTJMax) GetCpuHealth() => (true, 0f);
        public IReadOnlyList<string> GetGpuModels() => throw new InvalidOperationException("BuildHealth must not run while Diagnostics is off");
        public IReadOnlyList<HardwareSensor> GetGpuSensors() => Array.Empty<HardwareSensor>();
        public IReadOnlyList<GpuReadout> GetGpus() => Array.Empty<GpuReadout>();
        public IReadOnlyList<HardwareSensor> GetMemorySensors() => Array.Empty<HardwareSensor>();
        public string GetMemoryTotalFormatted() => "";
        public string GetRamBrandModel() => "";
        public IReadOnlyDictionary<string, StorageComponent> GetStorageComponents(bool includeSmart = true) => new Dictionary<string, StorageComponent>();
        public IReadOnlyList<string> GetStoragePartitions() => Array.Empty<string>();
        public IReadOnlyList<StorageDriveInfo> GetStorageInfo() => Array.Empty<StorageDriveInfo>();
        public string GetStorageBrandModel() => "";
        public IReadOnlyList<HardwareSensor> GetMotherboardSensors() => Array.Empty<HardwareSensor>();
        public string GetMotherboardModel() => "";
        public SensorExtras GetSensorExtras() => new();
        public string GetOsVersion() => "";
        public void SetPollingRate(int pollingRate) { }
        public Task ReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class EmptyMetricsHistoryStore : IMetricsHistoryStore
    {
        public void Append(IReadOnlyList<MetricSample> samples, long? pruneCutoffSec) { }
        public IReadOnlyList<MetricSample> Query(long fromSec, long toSec) => Array.Empty<MetricSample>();
        public IReadOnlyList<ScalarDecimatedSlot> QueryScalarsDecimated(long fromSec, long toSec, int stepSeconds) => Array.Empty<ScalarDecimatedSlot>();
        public IReadOnlyList<GpuDecimatedSlot> QueryGpuDecimated(long fromSec, long toSec, int stepSeconds) => Array.Empty<GpuDecimatedSlot>();
        public IReadOnlyList<TemperatureBucketRow> QueryTemperatureBuckets(long fromUtcMs, long toUtcMs) => Array.Empty<TemperatureBucketRow>();
        public IReadOnlyList<FanDecimatedSlot> QueryFanDecimated(long fromSec, long toSec, int stepSeconds) => Array.Empty<FanDecimatedSlot>();
        public IReadOnlyList<ComponentTempDecimatedSlot> QueryComponentTempDecimated(long fromSec, long toSec, int stepSeconds) => Array.Empty<ComponentTempDecimatedSlot>();
        public int ResetAll() => 0;
        public int BlankFpsSeries() => 0;
        public void Dispose() { }
    }

    [Fact]
    public void Tick_GateOff_NeverCallsBuildHealth_AndNeverNotifies()
    {
        var configStore = new Nexus.Service.Tests.InMemoryConfigStore();
        configStore.Update(s => s.Features.Diagnostics = false);
        var health = new DiagnosticsHealthModel(
            new UnsupportedSmartHealthSource(),
            new CoolingStallDetector(),
            new GpuHealthMonitor(),
            new EventLogMonitor(),
            new MemoryDiagnosticOrchestrator(),
            new PnpProblemScanner(),
            new ThrowingSensorProvider(),
            new EmptyMetricsHistoryStore(),
            configStore);
        var service = new DiagnosticsAlertService(health, configStore, new FeatureGates(configStore));
        var fired = false;
        service.AlertNeedsAttention += _ => fired = true;

        service.Tick();

        Assert.False(fired);
    }

    private static HealthComponent Component(string id, string kind, string status, string reasonSeverity, string code = "x.reason") =>
        new()
        {
            Id = id,
            Kind = kind,
            Name = id,
            Status = status,
            Reasons = new List<HealthComponentReason> { new(code, reasonSeverity, "summary", "detail") },
        };

    [Fact]
    public void MasterDisabled_NeverNotifies_EvenForActComponents()
    {
        var notifications = new DiagnosticsNotifications { Enabled = false, StorageHealth = true };
        var components = new[] { Component("storage:1", "storage", HealthStatuses.Act, HealthStatuses.Act) };

        var notices = DiagnosticsAlertService.EvaluateNotifications(
            components, notifications, new Dictionary<string, DateTime>(), T0);

        Assert.Empty(notices);
    }

    [Fact]
    public void DefaultSettings_AllTogglesOff_ProducesNoNotifications()
    {
        var notifications = new DiagnosticsNotifications();
        var components = new[]
        {
            Component("storage:1", "storage", HealthStatuses.Act, HealthStatuses.Act),
            Component("gpu:0", "gpu", HealthStatuses.Watch, HealthStatuses.Watch),
            Component("memory", "memory", HealthStatuses.Act, HealthStatuses.Act),
            Component("system", "system", HealthStatuses.Watch, HealthStatuses.Watch),
            Component("cooling:pump1", "cooling", HealthStatuses.Act, HealthStatuses.Act),
            Component("cooling", "cooling", HealthStatuses.Watch, HealthStatuses.Watch),
        };

        var notices = DiagnosticsAlertService.EvaluateNotifications(
            components, notifications, new Dictionary<string, DateTime>(), T0);

        Assert.Empty(notices);
    }

    [Fact]
    public void CategoryEnabled_ActComponent_Notifies()
    {
        var notifications = new DiagnosticsNotifications { Enabled = true, StorageHealth = true };
        var components = new[] { Component("storage:1", "storage", HealthStatuses.Act, HealthStatuses.Act) };

        var notices = DiagnosticsAlertService.EvaluateNotifications(
            components, notifications, new Dictionary<string, DateTime>(), T0);

        var notice = Assert.Single(notices);
        Assert.Equal("storage:1", notice.Title);
        Assert.Equal("summary", notice.Text);
    }

    [Fact]
    public void CategoryDisabled_MasterEnabled_DoesNotNotify()
    {
        var notifications = new DiagnosticsNotifications { Enabled = true, StorageHealth = false };
        var components = new[] { Component("storage:1", "storage", HealthStatuses.Act, HealthStatuses.Act) };

        var notices = DiagnosticsAlertService.EvaluateNotifications(
            components, notifications, new Dictionary<string, DateTime>(), T0);

        Assert.Empty(notices);
    }

    [Fact]
    public void WatchSeverity_NotifiesWhenCategoryApplies()
    {
        var notifications = new DiagnosticsNotifications { Enabled = true, Cooling = true };
        var components = new[] { Component("cooling:fan1", "cooling", HealthStatuses.Watch, HealthStatuses.Watch, "cooling.fanStall") };

        var notices = DiagnosticsAlertService.EvaluateNotifications(
            components, notifications, new Dictionary<string, DateTime>(), T0);

        Assert.Single(notices);
    }

    [Fact]
    public void CoolingAggregateId_MapsToHighTemp_NotCooling()
    {
        var notifications = new DiagnosticsNotifications { Enabled = true, Cooling = true, HighTemp = false };
        var aggregate = Component("cooling", "cooling", HealthStatuses.Watch, HealthStatuses.Watch, "cooling.sustainedHighTemp");

        var notices = DiagnosticsAlertService.EvaluateNotifications(
            new[] { aggregate }, notifications, new Dictionary<string, DateTime>(), T0);

        Assert.Empty(notices);
    }

    [Fact]
    public void PerDeviceStallId_MapsToCooling_NotHighTemp()
    {
        var notifications = new DiagnosticsNotifications { Enabled = true, Cooling = false, HighTemp = true };
        var stall = Component("cooling:pump1", "cooling", HealthStatuses.Act, HealthStatuses.Act, "cooling.pumpStall");

        var notices = DiagnosticsAlertService.EvaluateNotifications(
            new[] { stall }, notifications, new Dictionary<string, DateTime>(), T0);

        Assert.Empty(notices);
    }

    [Fact]
    public void WithinCooldownWindow_SecondCallDoesNotReNotify()
    {
        var notifications = new DiagnosticsNotifications { Enabled = true, StorageHealth = true, CooldownMinutes = 60 };
        var components = new[] { Component("storage:1", "storage", HealthStatuses.Act, HealthStatuses.Act) };
        var lastNotified = new Dictionary<string, DateTime>();

        var first = DiagnosticsAlertService.EvaluateNotifications(components, notifications, lastNotified, T0);
        var second = DiagnosticsAlertService.EvaluateNotifications(components, notifications, lastNotified, T0.AddMinutes(10));

        Assert.Single(first);
        Assert.Empty(second);
    }

    [Fact]
    public void PastCooldownWindow_ReNotifies()
    {
        var notifications = new DiagnosticsNotifications { Enabled = true, StorageHealth = true, CooldownMinutes = 60 };
        var components = new[] { Component("storage:1", "storage", HealthStatuses.Act, HealthStatuses.Act) };
        var lastNotified = new Dictionary<string, DateTime>();

        var first = DiagnosticsAlertService.EvaluateNotifications(components, notifications, lastNotified, T0);
        var second = DiagnosticsAlertService.EvaluateNotifications(components, notifications, lastNotified, T0.AddMinutes(61));

        Assert.Single(first);
        Assert.Single(second);
    }

    [Fact]
    public void OkStatusComponent_NeverNotifies()
    {
        var notifications = new DiagnosticsNotifications { Enabled = true, StorageHealth = true, Cooling = true, HighTemp = true, MemoryTest = true, SystemDevices = true, GpuThrottle = true };
        var components = new[]
        {
            new HealthComponent { Id = "storage:1", Kind = "storage", Name = "Drive", Status = HealthStatuses.Ok },
        };

        var notices = DiagnosticsAlertService.EvaluateNotifications(
            components, notifications, new Dictionary<string, DateTime>(), T0);

        Assert.Empty(notices);
    }

    [Fact]
    public void UnknownStatusComponent_NeverNotifies_EvenWithAnUnknownReason()
    {
        // An unmeasured component carries a reason to be explainable, never to alert.
        var notifications = new DiagnosticsNotifications { Enabled = true, GpuThrottle = true };
        var components = new[]
        {
            Component("gpu:0", "gpu", HealthStatuses.Unknown, HealthStatuses.Unknown, "gpu.noHealthSource"),
        };

        var notices = DiagnosticsAlertService.EvaluateNotifications(
            components, notifications, new Dictionary<string, DateTime>(), T0);

        Assert.Empty(notices);
    }
}
