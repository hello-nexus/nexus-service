using System.Collections.Generic;
using Nexus.Service.Lifecycle;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;
using Xunit;

namespace Nexus.Service.Tests.Sensors;

[Collection("SmartLiveConsumers")]
public class SmartPollDemandTests
{
    private static (InMemoryConfigStore Store, FeatureGates Gates) Build()
    {
        var store = new InMemoryConfigStore();
        return (store, new FeatureGates(store));
    }

    private static void Pillars(InMemoryConfigStore store, bool monitoring, bool diagnostics, bool cooling) =>
        store.Update(s =>
        {
            s.Features.Monitoring = monitoring;
            s.Features.Diagnostics = diagnostics;
            s.Features.Cooling = cooling;
        });

    private static void DriveCurve(InMemoryConfigStore store, string input) =>
        store.Update(s => s.Cooling.Curves.Add(new CurveDocument
        {
            Id = "c",
            Name = "c",
            Type = "Flat",
            Input = new CurveInputDocument { Id = input, Type = "Temperature" },
        }));

    private static SmartPollDemand Demand(InMemoryConfigStore store, FeatureGates gates) =>
        SmartPollDemand.Capture(gates, store.Load());

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void ActiveFollowsMonitoringAndDiagnostics(bool monitoring, bool diagnostics, bool active)
    {
        var (store, gates) = Build();
        Pillars(store, monitoring, diagnostics, cooling: false);
        var demand = Demand(store, gates);

        Assert.Equal(active, demand.IsActive(new[] { "/nvme/0" }));
        Assert.Equal(active, demand.IsNeededFor("/nvme/0"));
    }

    [Theory]
    [InlineData("/nvme/0", "/nvme/0/temperature/0")]
    [InlineData("/ata/0", "/ata/0/temperature/0")]
    [InlineData("/scsi/2", "/scsi/2/temperature/0")]
    [InlineData("/usb/1", "/usb/1/temperature/0")]
    public void ADriveTemperatureCurve_KeepsThatDriveReadingWhileCoolingIsOn(string drive, string sensor)
    {
        var (store, gates) = Build();
        Pillars(store, false, false, cooling: true);
        DriveCurve(store, sensor);
        var demand = Demand(store, gates);

        Assert.True(demand.IsActive(new[] { drive }));
        Assert.True(demand.IsNeededFor(drive));
        Assert.False(demand.IsNeededFor("/nvme/9"));
        Assert.False(demand.IsActive(new[] { "/nvme/9" }));
    }

    [Fact]
    public void ADriveCurve_DoesNotCountWhileCoolingIsOff_OrForACpuCurve()
    {
        var (store, gates) = Build();
        Pillars(store, false, false, cooling: false);
        DriveCurve(store, "/ata/0/temperature/0");
        Assert.False(Demand(store, gates).IsActive(new[] { "/ata/0" }));

        var (cpuStore, cpuGates) = Build();
        Pillars(cpuStore, false, false, cooling: true);
        DriveCurve(cpuStore, "/intelcpu/0/temperature/0");
        Assert.False(Demand(cpuStore, cpuGates).IsActive(new[] { "/ata/0" }));
    }

    [Fact]
    public void SwitchingAPillarBackOn_IsSeenByTheNextCapture()
    {
        var (store, gates) = Build();
        Pillars(store, false, false, cooling: false);
        Assert.False(Demand(store, gates).IsNeededFor("/ata/0"));

        store.Update(s => s.Features.Diagnostics = true);

        Assert.True(Demand(store, gates).IsNeededFor("/ata/0"));
    }

    [Fact]
    public void Cycles_ADriveNothingNeeds_IsNeverReadEvenOnForce_AndItsSeedIsForgotten()
    {
        var seeded = new HashSet<string> { "/ata/0" };

        var forced = SmartPollDemand.Cycles(false, seeded, "/ata/0", () => 30, force: true);

        Assert.Equal(SmartPollPolicy.ToCycleCount(SmartPollPolicy.NeverSeconds), forced);
        Assert.DoesNotContain("/ata/0", seeded);
        Assert.Equal(1u, SmartPollDemand.Cycles(true, seeded, "/ata/0", () => 30, force: false));
        Assert.Equal(SmartPollPolicy.ToCycleCount(30), SmartPollDemand.Cycles(true, seeded, "/ata/0", () => 30, force: false));
    }

    [Fact]
    public void Cycles_AUserNeverStillWinsOverTheSeedAndForce()
    {
        var seeded = new HashSet<string>();
        var never = SmartPollPolicy.ToCycleCount(SmartPollPolicy.NeverSeconds);

        Assert.Equal(never, SmartPollDemand.Cycles(true, seeded, "/ata/0", () => SmartPollPolicy.NeverSeconds, force: true));
        Assert.Empty(seeded);
    }

    [Fact]
    public void ALiveConsumer_KeepsEveryDriveReading_AndItsDepartureStopsThem()
    {
        var (store, gates) = Build();
        Pillars(store, false, false, cooling: false);
        var seeded = new HashSet<string>();

        var idle = SmartPollDemand.Capture(gates, store.Load(), liveConsumers: false);
        Assert.False(idle.IsActive(new[] { "/ata/0" }));

        var live = SmartPollDemand.Capture(gates, store.Load(), liveConsumers: true);
        Assert.True(live.IsActive(new[] { "/ata/0" }));
        Assert.Equal(1u, SmartPollDemand.Cycles(live.IsNeededFor("/ata/0"), seeded, "/ata/0", () => 30, force: false));

        Assert.Equal(
            SmartPollPolicy.ToCycleCount(SmartPollPolicy.NeverSeconds),
            SmartPollDemand.Cycles(idle.IsNeededFor("/ata/0"), seeded, "/ata/0", () => 30, force: false));
        Assert.Equal(1u, SmartPollDemand.Cycles(live.IsNeededFor("/ata/0"), seeded, "/ata/0", () => 30, force: false));
    }

    [Fact]
    public void APollStyleReader_CountsForAWhile()
    {
        SmartLiveConsumers.Forget();
        Assert.False(SmartLiveConsumers.Any(null));

        SmartLiveConsumers.NoteRead();

        Assert.True(SmartLiveConsumers.Any(null));
        SmartLiveConsumers.Forget();
    }

    [Fact]
    public void TheFlipCallbackFiresOncePerChange_AndNotOnTheBaselineOrARepeat()
    {
        var flips = 0;
        var flip = new SmartActiveFlip(() => flips++);

        flip.Observe(false);
        flip.Observe(false);
        Assert.Equal(0, flips);

        flip.Observe(true);
        flip.Observe(true);
        Assert.Equal(1, flips);

        flip.Observe(false);
        Assert.Equal(2, flips);
    }

    [Fact]
    public void AReadAfterTheWindowClosed_IsReportedCold_AndAWarmOneIsNot()
    {
        SmartLiveConsumers.Forget();

        Assert.True(SmartLiveConsumers.NoteRead());
        Assert.False(SmartLiveConsumers.NoteRead());
        SmartLiveConsumers.Forget();
    }

    [Fact]
    public void TheReaderWalksOnlyWhenReadsHadStopped()
    {
        var (store, gates) = Build();
        Pillars(store, false, false, cooling: false);
        var stopped = SmartPollDemand.Capture(gates, store.Load(), liveConsumers: false);
        var drives = new[] { "/ata/0" };

        Assert.True(SmartPollDemand.ReaderMustWalk(true, stopped, drives));
        Assert.False(SmartPollDemand.ReaderMustWalk(false, stopped, drives));

        // Reads were running: a live subscriber, a pillar, or a drive curve keeps cadence in charge.
        Assert.False(SmartPollDemand.ReaderMustWalk(true, SmartPollDemand.Capture(gates, store.Load(), liveConsumers: true), drives));
        store.Update(s => s.Features.Monitoring = true);
        Assert.False(SmartPollDemand.ReaderMustWalk(true, SmartPollDemand.Capture(gates, store.Load(), liveConsumers: false), drives));
        Pillars(store, false, false, cooling: true);
        DriveCurve(store, "/ata/0/temperature/0");
        Assert.False(SmartPollDemand.ReaderMustWalk(true, SmartPollDemand.Capture(gates, store.Load(), liveConsumers: false), drives));
    }

    [Theory]
    [InlineData(uint.MaxValue, null, 0L, false, false)]
    [InlineData(uint.MaxValue, 0L, 600000L, true, false)]
    [InlineData(30u, null, 5000L, false, true)]
    [InlineData(30u, 1000L, 5000L, false, false)]
    [InlineData(30u, 1000L, 31000L, false, true)]
    [InlineData(30u, 1000L, 5000L, true, true)]
    [InlineData(1u, 1000L, 1950L, false, true)]
    public void StorageUpdateDue_TouchesADriveOnlyWhenSmartIsDue(uint cycles, long? last, long now, bool force, bool expected) =>
        Assert.Equal(expected, SmartPollDemand.StorageUpdateDue(cycles, last, now, force));

    [Fact]
    public void LiveDriveActivity_DefaultsOn()
    {
        Assert.True(new MonitoringSettings().LiveDriveActivity);
    }

    [Fact]
    public void Cycles_NotNeeded_NeverDue()
    {
        var cycles = SmartPollDemand.Cycles(false, new HashSet<string>(), "/nvme/0", () => 1, force: true);
        Assert.False(SmartPollDemand.StorageUpdateDue(cycles, null, 0, force: true));
    }

    [Fact]
    public void Cycles_UserNever_NeverDueEvenForced()
    {
        var cycles = SmartPollDemand.Cycles(true, new HashSet<string>(), "/nvme/0", () => SmartPollPolicy.NeverSeconds, force: true);
        Assert.False(SmartPollDemand.StorageUpdateDue(cycles, null, 0, force: true));
    }
}
