using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Diagnostics;
using Nexus.Service.Diagnostics.Cooling;
using Nexus.Service.Diagnostics.Gpu;
using Nexus.Service.Diagnostics.Storage;
using Nexus.Service.Diagnostics.SystemInfo;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics;

public class ThermalGuardHealthComponentTests
{
    private static readonly DateTime Now = new(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc);

    private static HealthComponentReason ReasonFor(ThermalGuardTripRecord trip)
    {
        var result = DiagnosticsHealthModel.Compute(
            smart: new SmartSnapshot { Supported = true, Drives = Array.Empty<SmartDriveInfo>() },
            cooling: new CoolingStallSnapshot(true, Array.Empty<CoolingStallDevice>()),
            gpu: GpuHealthSnapshot.Unsupported,
            counts30d: new Dictionary<string, int>(),
            lastMemoryTest: null,
            pnp: new PnpProblemSnapshot(true, Array.Empty<PnpProblemDevice>()),
            knownGpuModels: Array.Empty<string>(),
            windowsSupported: true,
            generatedAtUtc: Now,
            thermalTrip: trip);
        var component = Assert.Single(result.Components, c => c.Id == "cooling:thermal-guard");
        return Assert.Single(component.Reasons);
    }

    private static long Ms(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    [Fact]
    public void ActiveAndRecentTrips_ShareTheShortSummary_WithCauseAndStateInTheDetail()
    {
        var active = ReasonFor(new ThermalGuardTripRecord { AtUtcMs = Ms(Now.AddMinutes(-5)), PeakC = 96.4, Reason = "cooling-loss" });
        var recent = ReasonFor(new ThermalGuardTripRecord
        {
            AtUtcMs = Ms(Now.AddHours(-3)), EndedAtUtcMs = Ms(Now.AddHours(-2)), PeakC = 96.4, Reason = "limit",
        });

        Assert.Equal("cooling.thermalGuardTrip", active.Code);
        Assert.Equal("Thermal guard tripped, peak 96 C", active.Summary);
        Assert.Equal(active.Summary, recent.Summary);
        Assert.Equal(HealthStatuses.Act, active.Severity);
        Assert.Equal(HealthStatuses.Watch, recent.Severity);
        Assert.Contains("fans were not cooling the CPU", active.Detail);
        Assert.Contains("active=True", active.Detail);
        Assert.Contains("CPU reached its temperature limit", recent.Detail);
        Assert.Contains("active=False", recent.Detail);
    }
}
