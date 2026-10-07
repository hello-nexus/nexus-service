#if DEV_TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Conflicts;
using Nexus.Service.Dev;
using Nexus.Service.Diagnostics;
using Nexus.Service.Diagnostics.EventLog;
using Nexus.Service.Lifecycle;
using Nexus.Service.Models.Conflicts;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Models.Devices;
using Nexus.Service.Models.Update;
using Nexus.Service.Persistence;
using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests.Dev;

public class DevSimEventsTests
{
    private static readonly string[] ContractIds =
    {
        "guard.limitTrip", "guard.coolingLossTrip", "guard.escalated", "guard.watchdogLatched", "guard.endedTrip",
        "guard.pendingHeal", "guard.gpuHandback",
        "health.fanStall", "health.pumpStall", "health.sustainedHighTemp", "health.gpuThrottle", "health.gpuTdr",
        "health.smartWarning", "health.nvmeCritical", "health.memoryWhea",
        "incident.bsod", "incident.appCrash", "incident.dirtyShutdown",
        "app.updateAvailable", "device.firmwareUpdate", "app.conflict", "device.disconnect",
    };

    private static DevSimEvents Sim(long now = 1_000_000) => new(() => now, registerAsCurrent: false);

    [Fact]
    public void TheCatalog_IsTheFixedContract_WithValidCategories()
    {
        Assert.Equal(ContractIds.OrderBy(x => x), DevSimEvents.Catalog.Select(c => c.Id).OrderBy(x => x));
        Assert.All(DevSimEvents.Catalog, c =>
        {
            Assert.Contains(c.Category, new[] { "guard", "health", "incident", "device" });
            Assert.False(string.IsNullOrWhiteSpace(c.Label));
        });
        Assert.Equal(7, DevSimEvents.Catalog.Count(c => c.Category == "guard"));
        Assert.Equal(8, DevSimEvents.Catalog.Count(c => c.Category == "health"));
        Assert.Equal(3, DevSimEvents.Catalog.Count(c => c.Category == "incident"));
    }

    [Fact]
    public void Start_IsIdempotent_RejectsUnknownIds_AndRecordsTheStartTime()
    {
        var sim = Sim(42);
        Assert.False(sim.Start("nope", out _));

        Assert.True(sim.Start("guard.limitTrip", out var first));
        Assert.True(first);
        Assert.True(sim.Start("guard.limitTrip", out var second));
        Assert.False(second);

        Assert.Equal(new[] { ("guard.limitTrip", 42L) }, sim.Active());
    }

    [Fact]
    public void StopAndClear_EndSimulations_AndChangedFiresOnlyOnRealChanges()
    {
        var sim = Sim();
        var changes = 0;
        sim.Changed += () => changes++;

        sim.Start("guard.limitTrip", out _);
        sim.Start("guard.limitTrip", out _);
        sim.Start("health.fanStall", out _);
        Assert.Equal(2, changes);

        Assert.False(sim.Stop("nope"));
        Assert.True(sim.Stop("health.fanStall"));
        Assert.True(sim.Stop("health.fanStall"));
        Assert.Equal(3, changes);

        sim.Clear();
        sim.Clear();
        Assert.Equal(4, changes);
        Assert.Empty(sim.Active());
    }

    // ── Thermal guard overlay ──

    private static ThermalGuardResponse RealGuard() => new() { State = "normal", LimitC = 95, LimitSource = "spec" };

    [Fact]
    public void LimitTrip_ShowsAnActiveLimitTripAtLimitPlusFour()
    {
        var sim = Sim(500);
        sim.Start("guard.limitTrip", out _);
        var r = RealGuard();

        sim.ApplyGuard(r);

        Assert.Equal("tripped", r.State);
        Assert.Equal(99, r.GuardTempC);
        Assert.Equal("limit", r.LastTrip!.Reason);
        Assert.Equal(99, r.LastTrip.PeakC);
        Assert.Null(r.LastTrip.EndedAtUtcMs);
        Assert.False(r.LastTrip.Acknowledged);
    }

    [Fact]
    public void CoolingLossTrip_Escalation_Watchdog_AndGpuHandback_Overlay()
    {
        var sim = Sim();
        sim.Start("guard.coolingLossTrip", out _);
        var loss = RealGuard();
        sim.ApplyGuard(loss);
        Assert.Equal("tripped", loss.State);
        Assert.Equal("cooling-loss", loss.LastTrip!.Reason);

        sim.Clear();
        sim.Start("guard.escalated", out _);
        var esc = RealGuard();
        sim.ApplyGuard(esc);
        Assert.Equal("escalated", esc.State);
        Assert.True(esc.LastTrip!.Escalated);

        sim.Clear();
        sim.Start("guard.watchdogLatched", out _);
        var latched = RealGuard();
        sim.ApplyGuard(latched);
        Assert.True(latched.WatchdogLatched);
        Assert.Equal("normal", latched.State);

        sim.Clear();
        sim.Start("guard.gpuHandback", out _);
        var gpu = RealGuard();
        sim.ApplyGuard(gpu);
        Assert.Equal("handedBack", Assert.Single(gpu.Gpus).State);
    }

    [Fact]
    public void EndedTrip_IsEndedAndUnacknowledged_AndFeedsTheDiagnosticsComponent()
    {
        var sim = Sim(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        sim.Start("guard.endedTrip", out _);
        var r = RealGuard();

        sim.ApplyGuard(r);

        Assert.NotNull(r.LastTrip!.EndedAtUtcMs);
        Assert.False(r.LastTrip.Acknowledged);
        Assert.Equal("normal", r.State);

        var overlaid = DiagnosticsHealthModel.ApplySimulation(
            new DiagnosticsHealthResponse { Overall = HealthStatuses.Ok, Supported = true }, sim);
        var component = Assert.Single(overlaid.Components, c => c.Id == "cooling:thermal-guard");
        Assert.Equal(HealthStatuses.Watch, component.Status);
        Assert.Equal(HealthStatuses.Watch, overlaid.Overall);
    }

    [Fact]
    public void PendingHeal_ReportsAnUndoableHealWithTwoSampleChannels()
    {
        var sim = Sim(7);
        sim.Start("guard.pendingHeal", out _);
        var r = RealGuard();

        sim.ApplyGuard(r);

        Assert.True(r.Heal.UndoAvailable);
        Assert.Equal(2, r.Heal.Channels.Count);
        Assert.Equal(7, r.Heal.HealedAtUtcMs);
    }

    [Fact]
    public void WithNothingActive_TheGuardResponseIsUntouched()
    {
        var r = RealGuard();
        Sim().ApplyGuard(r);
        Assert.Equal("normal", r.State);
        Assert.Null(r.LastTrip);
        Assert.False(r.WatchdogLatched);
        Assert.False(r.Heal.UndoAvailable);
        Assert.Empty(r.Gpus);
    }

    // ── Health overlay ──

    [Theory]
    [InlineData("health.fanStall", "cooling.fanStall", "cooling", HealthStatuses.Act)]
    [InlineData("health.pumpStall", "cooling.pumpStall", "cooling", HealthStatuses.Act)]
    [InlineData("health.sustainedHighTemp", "cooling.sustainedHighTemp", "cooling", HealthStatuses.Watch)]
    [InlineData("health.gpuThrottle", "gpu.thermalThrottle", "gpu", HealthStatuses.Watch)]
    [InlineData("health.gpuTdr", "gpu.tdr", "gpu", HealthStatuses.Act)]
    [InlineData("health.smartWarning", "smart.reallocated", "storage", HealthStatuses.Watch)]
    [InlineData("health.nvmeCritical", "nvme.criticalWarning", "storage", HealthStatuses.Act)]
    [InlineData("health.memoryWhea", "memory.wheaErrors", "memory", HealthStatuses.Act)]
    public void EachHealthSim_AddsAComponentWithTheRealReasonCode(string id, string code, string kind, string status)
    {
        var sim = Sim();
        sim.Start(id, out _);

        var overlaid = DiagnosticsHealthModel.ApplySimulation(
            new DiagnosticsHealthResponse { Overall = HealthStatuses.Ok, Supported = true }, sim);

        var component = Assert.Single(overlaid.Components);
        Assert.Equal(kind, component.Kind);
        Assert.Equal(status, component.Status);
        Assert.Equal(code, Assert.Single(component.Reasons).Code);
        Assert.Equal(status, overlaid.Overall);
    }

    [Fact]
    public void SustainedHighTemp_MergesIntoTheRealCoolingComponent_AndOverallIsRecomputed()
    {
        var sim = Sim();
        sim.Start("health.sustainedHighTemp", out _);
        sim.Start("health.fanStall", out _);
        var real = new DiagnosticsHealthResponse
        {
            Overall = HealthStatuses.Ok,
            Supported = true,
            Components = new List<HealthComponent>
            {
                new() { Id = "cooling", Kind = "cooling", Name = "Cooling (2)", Status = HealthStatuses.Ok, Reasons = new List<HealthComponentReason>() },
            },
        };

        var overlaid = DiagnosticsHealthModel.ApplySimulation(real, sim);

        var cooling = Assert.Single(overlaid.Components, c => c.Id == "cooling");
        Assert.Equal("Cooling (2)", cooling.Name);
        Assert.Equal(HealthStatuses.Watch, cooling.Status);
        Assert.Single(cooling.Reasons);
        Assert.Contains(overlaid.Components, c => c.Id == "cooling:sim-fan");
        Assert.Equal(HealthStatuses.Act, overlaid.Overall);
        // The input was not mutated.
        Assert.Equal(HealthStatuses.Ok, real.Overall);
        Assert.Empty(real.Components[0].Reasons);
    }

    [Fact]
    public void WithNoHealthSimActive_TheResponseIsReturnedAsIs()
    {
        var real = new DiagnosticsHealthResponse { Overall = HealthStatuses.Ok, Supported = true };
        Assert.Same(real, DiagnosticsHealthModel.ApplySimulation(real, Sim()));
        Assert.Same(real, DiagnosticsHealthModel.ApplySimulation(real, null));
    }

    // ── Incidents ──

    [Fact]
    public void IncidentSims_AddRowsWithTheRealSources()
    {
        var sim = Sim();
        sim.Start("incident.bsod", out _);
        sim.Start("incident.appCrash", out _);
        sim.Start("incident.dirtyShutdown", out _);

        var rows = sim.Incidents(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(
            new[] { "bugcheck", "appCrash", "dirtyShutdown" }.OrderBy(x => x),
            rows.Select(r => r.Source).OrderBy(x => x));
        Assert.Equal("SimulatedApp.exe", rows.Single(r => r.Source == "appCrash").App!.Name);
        Assert.Equal(DiagnosticSeverity.Critical, rows.Single(r => r.Source == "bugcheck").Severity);
        Assert.Empty(Sim().Incidents(DateTime.UtcNow));
    }

    // ── Device and app state ──

    [Fact]
    public void UpdateAvailable_ReportsAnAvailableVersion_WithoutTouchingTheRealStatus()
    {
        var sim = Sim();
        var real = new UpdateStatusResponse { CurrentVersion = "3.0.0", LatestVersion = "3.0.0", UpdateAvailable = false };
        Assert.Same(real, sim.ApplyUpdate(real));

        sim.Start("app.updateAvailable", out _);
        var shown = sim.ApplyUpdate(real);

        Assert.True(shown.UpdateAvailable);
        Assert.Equal("99.0.0-sim", shown.LatestVersion);
        Assert.Equal("3.0.0", shown.CurrentVersion);
        Assert.False(real.UpdateAvailable);
    }

    [Fact]
    public void FirmwareUpdate_AddsOneDeviceWithAnUpdate()
    {
        var sim = Sim();
        var items = new List<FirmwareStatusItem>();
        sim.ApplyFirmware(items);
        Assert.Empty(items);

        sim.Start("device.firmwareUpdate", out _);
        sim.ApplyFirmware(items);

        var item = Assert.Single(items);
        Assert.True(item.UpdateAvailable);
        Assert.NotEqual(item.CurrentVersion, item.AvailableVersion);
    }

    [Fact]
    public void Conflict_AddsOneRgbAppOnce_AndLeavesTheRealListAlone()
    {
        var sim = Sim();
        var real = new List<DetectedConflict>();
        Assert.Same(real, sim.ApplyConflicts(real));

        sim.Start("app.conflict", out _);
        var shown = sim.ApplyConflicts(real);
        var again = sim.ApplyConflicts(shown);

        var conflict = Assert.Single(shown);
        Assert.Equal("lighting", conflict.Category);
        Assert.Single(again);
        Assert.Empty(real);
    }

    [Fact]
    public void DeviceDisconnect_ReportsOnlyTheFirstPresentDeviceDisconnected()
    {
        var sim = Sim();
        var devices = new List<DeviceListItem>
        {
            new() { Id = "a", Connected = false },
            new() { Id = "b", Connected = true },
            new() { Id = "c", Connected = true },
        };
        sim.ApplyDevices(devices);
        Assert.Equal(new[] { false, true, true }, devices.Select(d => d.Connected));

        sim.Start("device.disconnect", out _);
        sim.ApplyDevices(devices);

        Assert.Equal(new[] { false, false, true }, devices.Select(d => d.Connected));

        var none = new List<DeviceListItem> { new() { Id = "x", Connected = false } };
        sim.ApplyDevices(none);
        Assert.False(none[0].Connected);
    }

    // ── Alerts ──

    private static (DiagnosticsAlertService Service, List<DiagnosticsAlertNotice> Notices, InMemoryConfigStore Store) Alerts()
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            var n = s.Diagnostics.Notifications;
            n.Enabled = true;
            n.HighTemp = true;
            n.Cooling = true;
            n.StorageHealth = true;
            n.MemoryTest = true;
            n.SystemDevices = true;
            n.GpuThrottle = true;
        });
        var service = new DiagnosticsAlertService(null!, store, FeatureGates.AllEnabled);
        var notices = new List<DiagnosticsAlertNotice>();
        service.AlertNeedsAttention += notices.Add;
        return (service, notices, store);
    }

    [Fact]
    public void StartingAHealthSim_RaisesItsNoticeOnce_ThroughTheRealEvaluation()
    {
        var (alerts, notices, _) = Alerts();
        var sim = Sim();
        sim.Start("health.fanStall", out _);

        DevSimRoutes.RaiseAlert(sim, alerts, "health.fanStall");

        var notice = Assert.Single(notices);
        Assert.Equal("cooling", notice.Kind);
        Assert.Equal("Simulated Fan #1", notice.Title);
    }

    [Fact]
    public void TheNotificationSwitches_ApplyToSimulatedAlerts()
    {
        var (alerts, notices, store) = Alerts();
        store.Update(s => s.Diagnostics.Notifications.Cooling = false);
        var sim = Sim();
        DevSimRoutes.RaiseAlert(sim, alerts, "health.fanStall");
        Assert.Empty(notices);

        store.Update(s =>
        {
            s.Diagnostics.Notifications.Cooling = true;
            s.Diagnostics.Notifications.Enabled = false;
        });
        DevSimRoutes.RaiseAlert(sim, alerts, "guard.limitTrip");
        DevSimRoutes.RaiseAlert(sim, alerts, "health.gpuTdr");
        Assert.Empty(notices);
    }

    [Fact]
    public void GuardSims_RaiseTheThermalGuardNotice_AndDeepLinkLikeTheRealOne()
    {
        var (alerts, notices, _) = Alerts();
        var sim = Sim();

        DevSimRoutes.RaiseAlert(sim, alerts, "guard.limitTrip");
        DevSimRoutes.RaiseAlert(sim, alerts, "guard.watchdogLatched");
        DevSimRoutes.RaiseAlert(sim, alerts, "guard.pendingHeal");
        DevSimRoutes.RaiseAlert(sim, alerts, "guard.gpuHandback"); // the real handback raises no alert

        Assert.Equal(3, notices.Count);
        Assert.All(notices, n => Assert.Equal("thermalGuard", n.Kind));
        Assert.Equal("/system/diagnostics/cooling", DiagnosticsAlertService.AlertPath("thermalGuard"));
        Assert.Equal("CPU thermal guard tripped", notices[0].Title);
    }

    [Fact]
    public void IncidentAndDeviceSims_RaiseOrSkipTheirNotice()
    {
        var (alerts, notices, _) = Alerts();
        var sim = Sim();

        DevSimRoutes.RaiseAlert(sim, alerts, "incident.bsod");
        DevSimRoutes.RaiseAlert(sim, alerts, "app.updateAvailable");

        var notice = Assert.Single(notices);
        Assert.Equal("system", notice.Kind);
    }

    [Fact]
    public void TheRouteDescription_ListsTheCatalogAndTheActiveSims()
    {
        var sim = Sim(9);
        sim.Start("health.fanStall", out _);

        var body = DevSimRoutes.Describe(sim);

        Assert.Equal(22, body.Catalog.Count);
        var active = Assert.Single(body.Active);
        Assert.Equal("health.fanStall", active.Id);
        Assert.Equal(9, active.StartedAtUtcMs);
    }

    [Fact]
    public void NoSimulationEverWritesSettings()
    {
        // The simulator holds no settings reference at all; every overlay edits a copy of what a read path produced.
        var store = new InMemoryConfigStore();
        var before = System.Text.Json.JsonSerializer.Serialize(store.Load(), Nexus.Service.Serialization.PersistenceJsonContext.Default.NexusSettings);
        var sim = Sim();
        foreach (var entry in DevSimEvents.Catalog)
        {
            sim.Start(entry.Id, out _);
        }

        sim.ApplyGuard(RealGuard());
        sim.ApplyDevices(new List<DeviceListItem> { new() { Connected = true } });
        DiagnosticsHealthModel.ApplySimulation(new DiagnosticsHealthResponse(), sim);
        sim.Clear();

        var after = System.Text.Json.JsonSerializer.Serialize(store.Load(), Nexus.Service.Serialization.PersistenceJsonContext.Default.NexusSettings);
        Assert.Equal(before, after);
    }
}
#endif
