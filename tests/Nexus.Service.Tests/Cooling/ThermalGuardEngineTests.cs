using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Lifecycle;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Sensors;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.Cooling;

/// <summary>Tick-level tests: the guard's decisions reach the fan writes, and stay silent when off.</summary>
public class ThermalGuardEngineTests
{
    private sealed class Fans : IFanControlProvider
    {
        public readonly List<FanChannel> Channels = new();
        public float CpuTemp = 40f;
        public readonly List<(string Id, int Duty)> Driven = new();
        public readonly List<string> Released = new();
        public readonly List<TemperatureSource> ExtraSources = new();
        public int ReleaseAllCalls;

        public IReadOnlyList<FanChannel> GetFanChannels() => Channels;
        public IReadOnlyList<TemperatureSource> GetTemperatureSources() =>
            new[] { new TemperatureSource { Id = "cpu", Name = "Core (Tctl/Tdie)", Category = "CPU", Value = CpuTemp } }
                .Concat(ExtraSources).ToList();
        public float? ReadTemperature(string sensorId) =>
            sensorId == "boom" ? throw new InvalidOperationException("sensor failure") : CpuTemp;
        public int SetFanSpeed(string channelId, int dutyPercent) => dutyPercent;
        public void DriveFanSpeed(string channelId, int dutyPercent) => Driven.Add((channelId, dutyPercent));
        public void ReleaseFan(string channelId) => Released.Add(channelId);
        public void ReleaseAll() => ReleaseAllCalls++;
        public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FanCalibration>>(new List<FanCalibration>());
    }

    private static CurveDocument Flat(string output, int speed) => new()
    {
        Id = "c-" + output,
        Name = "c-" + output,
        Type = "Flat",
        Input = new CurveInputDocument { Id = "cpu", Type = "Temperature" },
        Outputs = { new CurveOutputDocument { Id = output, Type = "Fan" } },
        Flat = new FlatCurveData { Speed = speed },
    };

    private static (CurveEngine Engine, Fans Fans, InMemoryConfigStore Store, long[] Clock) Build()
    {
        var fans = new Fans();
        fans.Channels.Add(new FanChannel { Id = "f1", Name = "Fan 1" });
        fans.Channels.Add(new FanChannel { Id = "f2", Name = "Fan 2" });
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Cooling.Curves.Add(Flat("f1", 20));
            s.Cooling.ManualSpeeds["f2"] = 30;
        });
        var clock = new long[] { 1_000_000 };
        var hub = new MultiplexHub();
        var engine = new CurveEngine(fans, store, hub, new FeatureGates(store), new ThermalGuardController(fans, store, hub))
        {
            Clock = () => clock[0],
        };
        return (engine, fans, store, clock);
    }

    // Default limit without a sensor provider is 90 C; the limit trip is 93 C held for 5 s.
    private static void Trip(CurveEngine engine, Fans fans, long[] clock)
    {
        fans.CpuTemp = 99f;
        engine.Tick();
        clock[0] += 5000;
        engine.Tick();
    }

    private static int LastDuty(Fans fans, string id) => fans.Driven.Last(d => d.Id == id).Duty;

    [Fact]
    public void BelowFloorBand_CurveDutiesAreUntouched()
    {
        var (engine, fans, _, _) = Build();
        fans.CpuTemp = 50f;
        engine.Tick();
        Assert.Equal(new[] { ("f1", 20) }, fans.Driven);
    }

    [Fact]
    public void Floor_RaisesACurveDutyAndAManualDuty_ThenRestoresTheManualOne()
    {
        var (engine, fans, _, clock) = Build();
        fans.CpuTemp = 85f; // 40 percent floor at the default limit of 90
        engine.Tick();
        Assert.Equal(40, LastDuty(fans, "f1"));
        Assert.Equal(40, LastDuty(fans, "f2"));

        fans.CpuTemp = 50f;
        clock[0] += 1000;
        engine.Tick();
        Assert.Equal(20, LastDuty(fans, "f1"));
        Assert.Equal(30, LastDuty(fans, "f2"));
    }

    [Fact]
    public void WorkingCurveAtOrAboveSixtyAtTheLimit_IsNeverTouched()
    {
        var (engine, fans, store, _) = Build();
        store.Update(s => s.Cooling.Curves[0].Flat!.Speed = 70);
        fans.CpuTemp = 90f;
        engine.Tick();
        Assert.Equal(new[] { ("f1", 70) }, fans.Driven.Where(d => d.Id == "f1").ToArray());
    }

    [Fact]
    public void SittingAtTheLimit_DoesNotTrip()
    {
        var (engine, fans, store, clock) = Build();
        fans.CpuTemp = 91f;
        for (var i = 0; i < 20; i++)
        {
            clock[0] += 1000;
            engine.Tick();
        }
        Assert.Null(store.Load().Cooling.LastThermalTrip);
        Assert.DoesNotContain(fans.Driven, d => d.Duty == 100);
    }

    [Fact]
    public void LimitTrip_ForcesEveryNexusDrivenChannelToMax()
    {
        var (engine, fans, _, clock) = Build();
        Trip(engine, fans, clock);
        Assert.Equal(100, LastDuty(fans, "f1"));
        Assert.Equal(100, LastDuty(fans, "f2"));
    }

    [Fact]
    public void Trip_PersistsTheRecordAndReportsTrippedState()
    {
        var (engine, fans, store, clock) = Build();
        Trip(engine, fans, clock);

        var trip = store.Load().Cooling.LastThermalTrip;
        Assert.NotNull(trip);
        Assert.Equal(ThermalTripReasons.Limit, trip!.Reason);
        Assert.Null(trip.EndedAtUtcMs);
    }

    [Fact]
    public void LockedChannelNexusDrives_IsRaisedByTheGuard_ButUncontrolledNever()
    {
        var (engine, fans, store, clock) = Build();
        fans.Channels[0].Kind = FanKinds.Pump; // pumps are locked by default
        store.Update(s =>
        {
            s.Cooling.FanLockOverrides["f2"] = true;
            s.Cooling.Curves.Add(Flat("f3", 20));
        });
        fans.Channels.Add(new FanChannel { Id = "f3", Name = "Fan 3" });
        store.Update(s => s.Cooling.UncontrolledFanChannels.Add("f3"));
        Trip(engine, fans, clock);

        Assert.Equal(100, LastDuty(fans, "f1"));
        Assert.Equal(100, LastDuty(fans, "f2"));
        Assert.DoesNotContain(fans.Driven, d => d.Id == "f3" && d.Duty == 100);
    }

    [Fact]
    public void DistanceToTjMaxSensor_IsNotAGuardTemperature()
    {
        var (engine, fans, store, _) = Build();
        fans.ExtraSources.Add(new TemperatureSource { Id = "d", Name = "CPU Core #1 Distance to TjMax", Category = "CPU", Value = 120f });
        fans.CpuTemp = 40f;
        engine.Tick();

        Assert.DoesNotContain(fans.Driven, d => d.Duty == 100);
        Assert.Null(store.Load().Cooling.LastThermalTrip);
    }

    [Fact]
    public void ThrowingCurve_DoesNotKeepTheOverrideOffOtherChannels()
    {
        var (engine, fans, store, clock) = Build();
        var bad = Flat("f1", 20);
        bad.Id = "bad";
        bad.Type = "Graph";
        bad.Input = new CurveInputDocument { Id = "boom", Type = "Temperature" };
        bad.Graph = new GraphCurveData { Points = { new Nexus.Service.Persistence.GraphPoint { Temp = 30, Speed = 30 } } };
        store.Update(s =>
        {
            s.Cooling.Curves.Insert(0, bad);
            s.Cooling.Curves.Add(Flat("f4", 20));
        });
        fans.Channels.Add(new FanChannel { Id = "f4", Name = "Fan 4" });

        Trip(engine, fans, clock);

        Assert.Equal(100, LastDuty(fans, "f4"));
        Assert.Equal(100, LastDuty(fans, "f2"));
    }

    [Fact]
    public void ToggleOffMidTrip_ReleasesAnOrphanOutputTheGuardDrove()
    {
        var (engine, fans, store, clock) = Build();
        store.Update(s => s.Cooling.Curves.Add(new CurveDocument
        {
            Id = "orphan", Name = "orphan", Type = "Sync",
            Sync = new SyncCurveData { SourceChannelId = "absent" },
            Outputs = { new CurveOutputDocument { Id = "f5", Type = "Fan" } },
        }));
        fans.Channels.Add(new FanChannel { Id = "f5", Name = "Fan 5" });
        Trip(engine, fans, clock);
        Assert.Equal(100, LastDuty(fans, "f5"));

        store.Update(s => s.Cooling.ThermalGuardEnabled = false);
        clock[0] += 1000;
        engine.Tick();

        Assert.Contains("f5", fans.Released);
        Assert.Equal(30, LastDuty(fans, "f2"));
    }

    [Fact]
    public void OpenTripRecord_IsClosedOnRestartToggleOffCoolingOffAndIdle()
    {
        static ThermalGuardTripRecord Open() => new() { AtUtcMs = 1, PeakC = 99, Reason = ThermalTripReasons.Limit };

        // Restart: a new controller closes what the previous run left open.
        var (_, fans, store, _) = Build();
        store.Update(s => s.Cooling.LastThermalTrip = Open());
        _ = new ThermalGuardController(fans, store);
        Assert.NotNull(store.Load().Cooling.LastThermalTrip!.EndedAtUtcMs);

        // Toggle off through the route's entry point.
        var (_, fans2, store2, _) = Build();
        var c2 = new ThermalGuardController(fans2, store2);
        store2.Update(s => s.Cooling.LastThermalTrip = Open());
        c2.SetEnabled(false);
        Assert.NotNull(store2.Load().Cooling.LastThermalTrip!.EndedAtUtcMs);

        // Cooling off, seen by the engine.
        var (e3, _, store3, _) = Build();
        store3.Update(s => { s.Cooling.LastThermalTrip = Open(); s.Features.Cooling = false; });
        e3.Tick();
        Assert.NotNull(store3.Load().Cooling.LastThermalTrip!.EndedAtUtcMs);

        // Idle config.
        var fans4 = new Fans();
        var store4 = new InMemoryConfigStore();
        store4.Update(s => s.Cooling.LastThermalTrip = Open());
        var guard4 = new ThermalGuardController(fans4, store4);
        store4.Update(s => s.Cooling.LastThermalTrip = Open());
        new CurveEngine(fans4, store4, new MultiplexHub(), null, guard4).Tick();
        Assert.NotNull(store4.Load().Cooling.LastThermalTrip!.EndedAtUtcMs);
    }

    [Fact]
    public void ActiveTrip_StaysOpenWhileTripped()
    {
        var (engine, fans, store, clock) = Build();
        Trip(engine, fans, clock);
        Assert.Null(store.Load().Cooling.LastThermalTrip!.EndedAtUtcMs);
    }

    [Fact]
    public void TripAlerts_AreLimitedToOnePerCausePerThirtyMinutes()
    {
        var fans = new Fans();
        fans.Channels.Add(new FanChannel { Id = "f1", Name = "Fan 1" });
        var store = new InMemoryConfigStore();
        store.Update(s => s.Cooling.Curves.Add(Flat("f1", 20)));
        var utc = new long[] { 10_000_000 };
        var alerts = new List<string>();
        var guard = new ThermalGuardController(fans, store, null, null, null, null, () => utc[0]) { AlertSink = n => alerts.Add(n.Title) };
        var clock = new long[] { 1_000_000 };
        var engine = new CurveEngine(fans, store, new MultiplexHub(), null, guard) { Clock = () => clock[0] };

        void TripAndRelease()
        {
            Trip(engine, fans, clock);
            fans.CpuTemp = 50f;
            engine.Tick();
            clock[0] += 61_000;
            engine.Tick();
        }

        TripAndRelease();
        utc[0] += 5 * 60_000;
        TripAndRelease();
        Assert.Equal(1, alerts.Count(a => a.Contains("tripped")));

        utc[0] += 31 * 60_000;
        TripAndRelease();
        Assert.Equal(2, alerts.Count(a => a.Contains("tripped")));
    }

    [Fact]
    public void AHealedChannel_FollowsTheSharedCurveOnTheCpuTemperature()
    {
        var fans = new Fans();
        fans.Channels.Add(new FanChannel { Id = "gpufan", Name = "GPU Fan", IsGpu = true, DeviceId = "/gpu-nvidia/0", DutyPercent = 0 });
        fans.Channels.Add(new FanChannel { Id = "f1", Name = "Fan 1" });
        var store = new InMemoryConfigStore();
        store.Update(s => s.Cooling.Curves.Add(new CurveDocument
        {
            Id = "sync", Name = "sync", Type = "Sync",
            Sync = new SyncCurveData { SourceChannelId = "gpufan" },
            Outputs = { new CurveOutputDocument { Id = "f1", Type = "Fan" } },
        }));
        var guard = new ThermalGuardController(fans, store);
        guard.HealNow(automatic: false);
        var healed = Assert.Single(store.Load().Cooling.Curves, c => c.Id == CoolingConfigLint.GuardCurveId);
        Assert.Equal("f1", Assert.Single(healed.Outputs).Id);

        fans.CpuTemp = 65f;
        new CurveEngine(fans, store, new MultiplexHub(), null, guard) { Clock = () => 1_000_000 }.Tick();

        Assert.Equal(60, LastDuty(fans, "f1"));
    }

    [Fact]
    public void TheSharedCurve_IsNotScaledByTheGlobalModifier()
    {
        var fans = new Fans();
        fans.Channels.Add(new FanChannel { Id = "gpufan", Name = "GPU Fan", IsGpu = true, DeviceId = "/gpu-nvidia/0", DutyPercent = 0 });
        fans.Channels.Add(new FanChannel { Id = "f1", Name = "Fan 1" });
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Cooling.GlobalSpeedModifier = 0.5;
            s.Cooling.Curves.Add(new CurveDocument
            {
                Id = "sync", Name = "sync", Type = "Sync",
                Sync = new SyncCurveData { SourceChannelId = "gpufan" },
                Outputs = { new CurveOutputDocument { Id = "f1", Type = "Fan" } },
            });
        });
        var guard = new ThermalGuardController(fans, store);
        guard.HealNow(automatic: false);

        fans.CpuTemp = 90f; // the limit itself: the curve's full point
        new CurveEngine(fans, store, new MultiplexHub(), null, guard) { Clock = () => 1_000_000 }.Tick();

        Assert.Equal(100, LastDuty(fans, "f1"));
    }

    private static double[] GuardTemps(InMemoryConfigStore store) =>
        store.Load().Cooling.Curves.Single(c => c.Id == CoolingConfigLint.GuardCurveId).Graph!.Points.Select(p => p.Temp).ToArray();

    [Fact]
    public void ANoOpResync_WritesNothingToTheStore()
    {
        var (_, fans, store, _) = Build();
        var guard = new ThermalGuardController(fans, store);
        guard.HealNow(automatic: false);
        var e = new CurveEngine(fans, store, new MultiplexHub(), null, guard) { Clock = () => 1_000_000 };
        var changes = 0;
        store.OnChanged += () => changes++;

        e.Tick();
        e.Tick();

        Assert.Equal(0, changes);
    }

    [Fact]
    public void AHandEditToTheGuardCurve_IsRevertedOnTheNextTick()
    {
        var (_, fans, store, _) = Build();
        var guard = new ThermalGuardController(fans, store);
        guard.HealNow(automatic: false);
        var e = new CurveEngine(fans, store, new MultiplexHub(), null, guard) { Clock = () => 1_000_000 };
        store.Update(s => s.Cooling.Curves.Single(c => c.Id == CoolingConfigLint.GuardCurveId).Graph!.Points[0].Speed = 55);

        e.Tick();

        Assert.Equal(30, store.Load().Cooling.Curves.Single(c => c.Id == CoolingConfigLint.GuardCurveId).Graph!.Points[0].Speed);
    }

    [Fact]
    public void AfterABootWhereTheModelResolvesOnALaterTick_TheGuardCurveIsWrittenExactlyOnce_AtTheRealLimit()
    {
        var (_, fans, store, _) = Build();
        new ThermalGuardController(fans, store).HealNow(automatic: false); // leaves the curve built for the placeholder limit
        Assert.Equal(new double[] { 35, 50, 65, 80, 90 }, GuardTemps(store));

        var guard = new ThermalGuardController(fans, store, null, new FlakySensors()); // first model read throws
        var clock = new long[] { 1_000_000 };
        var e = new CurveEngine(fans, store, new MultiplexHub(), null, guard) { Clock = () => clock[0] };
        var curveWrites = 0;
        store.OnChanged += () =>
        {
            var pts = GuardTemps(store);
            if (!pts.SequenceEqual(new double[] { 35, 50, 65, 80, 90 }))
            {
                curveWrites++;
            }
        };

        e.Tick(); // unresolved: nothing is synced against the placeholder
        Assert.Equal(new double[] { 35, 50, 65, 80, 90 }, GuardTemps(store));
        clock[0] += 6000;
        e.Tick(); // resolved: 9800X3D, limit 95
        clock[0] += 1000;
        e.Tick();
        e.Tick();

        Assert.Equal(new double[] { 40, 55, 70, 85, 95 }, GuardTemps(store));
        Assert.Equal(1, curveWrites);
    }

    [Fact]
    public void UndoFollowedByATick_ResyncsTheRestoredGuardCurveToTheCurrentLimit()
    {
        var (e, fans, store, guard, clock) = LimitRig(null);
        guard.HealNow(automatic: false);
        guard.SetConfig(new SetThermalGuardConfigBody { LimitOverrideC = 100 });
        Assert.Equal(new double[] { 45, 60, 75, 90, 100 }, GuardTemps(store));
        store.Update(s => s.Cooling.ManualSpeeds["f2"] = 10); // a second hazard
        guard.HealNow(automatic: false); // its snapshot holds the guard curve built for 100
        guard.SetConfig(new SetThermalGuardConfigBody { ClearLimitOverride = true });
        Assert.Equal(new double[] { 35, 50, 65, 80, 90 }, GuardTemps(store));

        guard.Undo();
        Assert.Equal(new double[] { 45, 60, 75, 90, 100 }, GuardTemps(store));

        clock[0] += 1000;
        e.Tick();
        Assert.Equal(new double[] { 35, 50, 65, 80, 90 }, GuardTemps(store));
    }

    [Fact]
    public void APersistedOverrideOf89_StaysAsStoredUntilDetectionResolvesOnAn89CPart()
    {
        var (_, fans, store, _) = Build();
        store.Update(s => s.Cooling.ThermalGuardLimitOverrideC = 89);
        var guard = new ThermalGuardController(fans, store, null, new FlakySensors { Model = "AMD Ryzen 7 7800X3D 8-Core Processor" });
        var clock = new long[] { 1_000_000 };
        var e = new CurveEngine(fans, store, new MultiplexHub(), null, guard) { Clock = () => clock[0] };

        e.Tick(); // model unknown: the stored value is not clamped against the 90 placeholder
        Assert.Equal(89, guard.GetState().LimitC);

        clock[0] += 6000;
        e.Tick(); // resolved: the 7800X3D's own 89 C is inside the range
        Assert.Equal(89, guard.GetState().LimitC);
        Assert.Equal(89, guard.GetState().DetectedLimitC);
    }

    [Fact]
    public void AHealThatOnlyMigratesLeftovers_TakesNoSnapshotAndRaisesNoAlert_ButDropsStaleManualSpeeds()
    {
        var (_, fans, store, _) = Build();
        var alerts = new List<string>();
        var guard = new ThermalGuardController(fans, store) { AlertSink = n => alerts.Add(n.Title) };
        store.Update(s =>
        {
            s.Cooling.Curves.Clear();
            s.Cooling.Curves.Add(new CurveDocument
            {
                Id = "guard-mix-f1", Name = "old", Type = "Mixed",
                Outputs = { new CurveOutputDocument { Id = "f1", Type = "Fan" } },
            });
            s.Cooling.ManualSpeeds["f1"] = 20;
        });

        var state = guard.HealNow(automatic: false);

        Assert.False(state.UndoAvailable);
        Assert.Empty(state.Channels);
        Assert.Empty(alerts);
        Assert.Null(store.Load().Cooling.HealSnapshot);
        Assert.DoesNotContain("f1", store.Load().Cooling.ManualSpeeds.Keys);
        var curve = Assert.Single(store.Load().Cooling.Curves);
        Assert.Equal(CoolingConfigLint.GuardCurveId, curve.Id);
        Assert.Equal("f1", Assert.Single(curve.Outputs).Id);
    }

    [Fact]
    public void HealUndo_RestoresTheCurvesAndTheDroppedManualSpeedsExactly()
    {
        var (_, fans, store, _) = Build();
        var guard = new ThermalGuardController(fans, store);
        var curvesBefore = store.Load().Cooling.Curves.Select(c => c.Id).ToArray();
        store.Update(s => s.Cooling.ManualSpeeds["f2"] = 10); // manual-low: flagged
        var manualBefore = new Dictionary<string, int>(store.Load().Cooling.ManualSpeeds);

        guard.HealNow(automatic: false);
        Assert.DoesNotContain("f2", store.Load().Cooling.ManualSpeeds.Keys);

        guard.Undo();
        Assert.Equal(curvesBefore, store.Load().Cooling.Curves.Select(c => c.Id).ToArray());
        Assert.Equal(manualBefore, store.Load().Cooling.ManualSpeeds);
    }

    [Fact]
    public void TheSharedCurve_FollowsTheLimitWhenTheOverrideChanges()
    {
        var (e, fans, store, guard, clock) = LimitRig(null);
        guard.HealNow(automatic: false);
        double[] Temps() => store.Load().Cooling.Curves.Single(c => c.Id == CoolingConfigLint.GuardCurveId).Graph!.Points.Select(p => p.Temp).ToArray();
        Assert.Equal(new double[] { 35, 50, 65, 80, 90 }, Temps());

        guard.SetConfig(new SetThermalGuardConfigBody { LimitOverrideC = 100 });
        clock[0] += 1000;
        e.Tick();
        Assert.Equal(new double[] { 45, 60, 75, 90, 100 }, Temps());
        // The undo snapshot survives the service's own adjustment.
        Assert.NotNull(store.Load().Cooling.HealSnapshot);

        guard.SetConfig(new SetThermalGuardConfigBody { ClearLimitOverride = true });
        clock[0] += 1000;
        e.Tick();
        Assert.Equal(new double[] { 35, 50, 65, 80, 90 }, Temps());
    }

    [Fact]
    public void TheGuardSwitch_IsReportedAtOnceWhileTheStateFollowsTheNextTick()
    {
        var (_, fans, store, _) = Build();
        store.Update(s => s.Cooling.ThermalGuardEnabled = false);
        var guard = new ThermalGuardController(fans, store);
        Assert.False(guard.GetState().Enabled);

        var (state, _) = guard.SetConfig(new SetThermalGuardConfigBody { Enabled = true });

        Assert.True(state!.Enabled);
        Assert.True(guard.GetState().Enabled);
    }

    [Fact]
    public void OnlySyncAndTheGuardCurveAreExemptFromTheGlobalModifier()
    {
        Assert.True(CoolingConfigLint.IsGlobalModifierExempt(new CurveDocument { Id = "x", Type = "Sync" }));
        Assert.True(CoolingConfigLint.IsGlobalModifierExempt(new CurveDocument { Id = CoolingConfigLint.GuardCurveId, Type = "Graph" }));
        Assert.False(CoolingConfigLint.IsGlobalModifierExempt(new CurveDocument { Id = "x", Type = "Graph" }));
    }

    [Fact]
    public void GpuCoreTemp_UsesThatGpusOwnCoreSensorOnly()
    {
        var sources = new List<TemperatureSource>
        {
            new() { Id = "/gpu-nvidia/0/temperature/0", Name = "GPU Core", Category = "GPU", Value = 60 },
            new() { Id = "/gpu-nvidia/0/temperature/1", Name = "GPU Hot Spot", Category = "GPU", Value = 95 },
            new() { Id = "/gpu-nvidia/0/temperature/2", Name = "GPU Memory Junction", Category = "GPU", Value = 100 },
            new() { Id = "/gpu-nvidia/1/temperature/0", Name = "GPU Core", Category = "GPU", Value = 80 },
            new() { Id = "nvidia:temp:2", Name = "x temp", Category = "GPU", Value = 55 },
        };
        Assert.Equal(60, ThermalGuardController.GpuCoreTemp(sources, "/gpu-nvidia/0"));
        Assert.Equal(80, ThermalGuardController.GpuCoreTemp(sources, "/gpu-nvidia/1"));
        Assert.Equal(55, ThermalGuardController.GpuCoreTemp(sources, "nvidia:2"));
        Assert.Null(ThermalGuardController.GpuCoreTemp(sources, "/gpu-nvidia/7"));
    }

    [Fact]
    public void GpuManualBackup_IsPersistedAndRestoredAfterResume()
    {
        var (_, fans, store, _) = Build();
        store.Update(s => s.Cooling.GpuManualBackup["g1"] = 55);
        var guard = new ThermalGuardController(fans, store);

        guard.RestoreGpuManual(new[] { "g1" });

        Assert.Equal(55, store.Load().Cooling.ManualSpeeds["g1"]);
        Assert.Empty(store.Load().Cooling.GpuManualBackup);
    }

    [Fact]
    public void ClearHeal_DropsTheUndoState()
    {
        var settings = new CoolingSettings
        {
            HealSnapshot = new List<CurveDocument>(),
            HealedAtUtcMs = 5,
            HealedChannels = { new HealedChannelRecord { Id = "a" } },
        };
        settings.ClearHeal("test");
        Assert.Null(settings.HealSnapshot);
        Assert.Null(settings.HealedAtUtcMs);
        Assert.Empty(settings.HealedChannels);
    }

    // Three ticks at a steady RPM establish the fan's best before the trip.
    private static void LearnRpm(CurveEngine engine, Fans fans, long[] clock, int rpm)
    {
        fans.Channels[0].Rpm = rpm;
        fans.Channels[1].Rpm = rpm;
        for (var i = 0; i < 3; i++)
        {
            clock[0] += 1000;
            engine.Tick();
        }
    }

    [Fact]
    public void Escalated_WhenFansThatReportedRpmStayFarBelowTheirBest_StopsWritingAndReleasesAll()
    {
        var (engine, fans, _, clock) = Build();
        LearnRpm(engine, fans, clock, 1000);
        Trip(engine, fans, clock);
        var writes = fans.Driven.Count;

        fans.Channels[0].Rpm = 100;
        fans.Channels[1].Rpm = 100;
        clock[0] += 21_000;
        engine.Tick();

        Assert.Equal(1, fans.ReleaseAllCalls);
        Assert.Equal(writes, fans.Driven.Count);
    }

    [Fact]
    public void TachDroppingToZero_EscalatesOnlyAfterAMinute()
    {
        var (engine, fans, _, clock) = Build();
        LearnRpm(engine, fans, clock, 1000);
        Trip(engine, fans, clock);

        fans.Channels[0].Rpm = 0;
        fans.Channels[1].Rpm = 0;
        clock[0] += 21_000;
        engine.Tick();
        clock[0] += 30_000;
        engine.Tick();
        Assert.Equal(0, fans.ReleaseAllCalls);

        clock[0] += 31_000;
        engine.Tick();
        Assert.Equal(1, fans.ReleaseAllCalls);
    }

    [Fact]
    public void ARpmSpike_NeverSetsTheBest()
    {
        var (engine, fans, _, clock) = Build();
        LearnRpm(engine, fans, clock, 1000);
        // One tick at a high value, then back to normal: not three consecutive readings.
        fans.Channels[0].Rpm = 9000;
        fans.Channels[1].Rpm = 9000;
        clock[0] += 1000;
        engine.Tick();
        fans.Channels[0].Rpm = 1000;
        fans.Channels[1].Rpm = 1000;
        clock[0] += 1000;
        engine.Tick();
        Trip(engine, fans, clock);

        // Well above a quarter of the real best: a working fan, no escalation.
        fans.Channels[0].Rpm = 500;
        fans.Channels[1].Rpm = 500;
        clock[0] += 25_000;
        engine.Tick();
        Assert.Equal(0, fans.ReleaseAllCalls);
    }

    [Fact]
    public void PumpsAreNotEscalationEvidence()
    {
        var (engine, fans, _, clock) = Build();
        fans.Channels[0].Kind = FanKinds.Pump;
        fans.Channels[1].Kind = FanKinds.Pump;
        LearnRpm(engine, fans, clock, 1000);
        Trip(engine, fans, clock);

        fans.Channels[0].Rpm = 0;
        fans.Channels[1].Rpm = 0;
        clock[0] += 100_000;
        engine.Tick();
        Assert.Equal(0, fans.ReleaseAllCalls);
    }

    [Fact]
    public void Trip_NeverEscalatesOnTemperatureAloneOrWithFansNeverReportingRpm()
    {
        var (engine, fans, _, clock) = Build();
        Trip(engine, fans, clock);

        fans.CpuTemp = 110f;
        clock[0] += 60_000;
        engine.Tick();

        Assert.Equal(0, fans.ReleaseAllCalls);
    }

    [Fact]
    public void TripEnd_WithNoHazardsInTheConfig_ChangesNoCurves()
    {
        var (engine, fans, store, clock) = Build();
        store.Update(s =>
        {
            s.Cooling.Curves[0].Flat!.Speed = 70; // a healthy curve: nothing for the lint to flag
            s.Cooling.ManualSpeeds["f2"] = 70;
        });
        var before = store.Load().Cooling.Curves.Select(c => c.Id).ToArray();
        Trip(engine, fans, clock);
        fans.CpuTemp = 50f;
        engine.Tick();
        clock[0] += 61_000;
        engine.Tick();

        Assert.Equal(before, store.Load().Cooling.Curves.Select(c => c.Id).ToArray());
        Assert.Null(store.Load().Cooling.HealSnapshot);
        Assert.NotNull(store.Load().Cooling.LastThermalTrip!.EndedAtUtcMs);
    }

    [Fact]
    public void ToggleOff_WritesNothingBeyondTheCurves_AndReportsOff()
    {
        var (engine, fans, store, _) = Build();
        store.Update(s => s.Cooling.ThermalGuardEnabled = false);
        fans.CpuTemp = 99f;
        engine.Tick();

        Assert.Equal(20, LastDuty(fans, "f1"));
        Assert.DoesNotContain(fans.Driven, d => d.Duty == 100);
        Assert.Equal(0, fans.ReleaseAllCalls);
        Assert.Null(store.Load().Cooling.LastThermalTrip);

        var state = new ThermalGuardController(fans, store).GetState();
        Assert.Equal(ThermalGuardStates.Off, state.State);
    }

    [Fact]
    public void CoolingFeatureOff_GuardWritesNothing()
    {
        var (engine, fans, store, _) = Build();
        store.Update(s => s.Features.Cooling = false);
        fans.CpuTemp = 99f;
        engine.Tick();
        engine.Tick();

        Assert.Empty(fans.Driven);
        Assert.Equal(0, fans.ReleaseAllCalls);
        Assert.Null(store.Load().Cooling.LastThermalTrip);
    }

    [Fact]
    public void MissingSettingsKey_ReadsAsEnabled()
    {
        var settings = JsonSerializer.Deserialize("{\"cooling\":{\"curves\":[]}}", PersistenceJsonContext.Default.NexusSettings)!;
        Assert.True(settings.Cooling.ThermalGuardEnabled);
        Assert.True(new CoolingSettings().ThermalGuardEnabled);

        var off = JsonSerializer.Deserialize("{\"cooling\":{\"thermalGuardEnabled\":false}}", PersistenceJsonContext.Default.NexusSettings)!;
        Assert.False(off.Cooling.ThermalGuardEnabled);
    }

    private static (CurveEngine Engine, ThermalGuardController Guard, long[] Mono) WatchdogRig(Fans fans, InMemoryConfigStore store)
    {
        var mono = new long[] { 1_000 };
        var guard = new ThermalGuardController(fans, store) { MonotonicMs = () => mono[0] };
        var engine = new CurveEngine(fans, store, new MultiplexHub(), null, guard) { Clock = () => mono[0] };
        return (engine, guard, mono);
    }

    [Fact]
    public void Watchdog_ReleasesAfterTenSecondsWhenHot_ThirtyWhenCool()
    {
        var (_, fans, store, _) = Build();
        var (e, guard, mono) = WatchdogRig(fans, store);
        fans.CpuTemp = 85f;
        e.Tick();
        guard.TickCompleted();
        guard.WatchdogCheck(mono[0] + 9_000);
        Assert.Equal(0, fans.ReleaseAllCalls);
        guard.WatchdogCheck(mono[0] + 11_000);
        Assert.Equal(1, fans.ReleaseAllCalls);

        var (_, fans2, store2, _) = Build();
        var (e2, guard2, mono2) = WatchdogRig(fans2, store2);
        fans2.CpuTemp = 50f;
        e2.Tick();
        guard2.TickCompleted();
        guard2.WatchdogCheck(mono2[0] + 11_000);
        Assert.Equal(0, fans2.ReleaseAllCalls);
        guard2.WatchdogCheck(mono2[0] + 31_000);
        Assert.Equal(1, fans2.ReleaseAllCalls);
    }

    [Fact]
    public void StallWhileTheFloorIsDriving_RewritesTheSameDutyOnTheNextTickAfterTheRelease()
    {
        var (_, fans, store, _) = Build();
        var (e, guard, mono) = WatchdogRig(fans, store);
        fans.CpuTemp = 90f;
        mono[0] += 1000;
        e.Tick();
        guard.TickCompleted();
        Assert.Equal(60, LastDuty(fans, "f1"));
        var before = fans.Driven.Count(d => d.Id == "f1" && d.Duty == 60);

        guard.WatchdogCheck(mono[0] + 11_000);
        Assert.Equal(1, fans.ReleaseAllCalls);
        mono[0] += 12_000;
        guard.TickCompleted();
        e.Tick();

        Assert.True(fans.Driven.Count(d => d.Id == "f1" && d.Duty == 60) > before);
    }

    private static void Stall(Fans fans, CurveEngine e, ThermalGuardController guard, long[] mono)
    {
        mono[0] += 1000;
        e.Tick();
        guard.TickCompleted();
        guard.WatchdogCheck(mono[0] + 31_000);
        mono[0] += 32_000;
    }

    [Fact]
    public void ThreeStallsInTenMinutes_LatchAndNoFanIsWrittenAfterwards_ButTheGuardKeepsReporting()
    {
        var (_, fans, store, _) = Build();
        var (e, guard, mono) = WatchdogRig(fans, store);
        var alerts = new List<string>();
        guard.AlertSink = n => alerts.Add(n.Title);
        fans.CpuTemp = 90f;

        Stall(fans, e, guard, mono);
        Stall(fans, e, guard, mono);
        Assert.False(guard.WatchdogLatched);
        Stall(fans, e, guard, mono);
        Assert.True(guard.WatchdogLatched);
        Assert.Equal(1, alerts.Count(a => a.Contains("BIOS")));

        var releasesAtLatch = fans.ReleaseAllCalls;
        var writes = fans.Driven.Count;
        store.Update(s => s.Cooling.Curves[0].Flat!.Speed = 33);
        fans.CpuTemp = 95f;
        for (var i = 0; i < 5; i++)
        {
            mono[0] += 1000;
            guard.TickCompleted();
            e.Tick();
        }

        Assert.Equal(writes, fans.Driven.Count);
        // The engine thread released the fans itself, once.
        Assert.Equal(releasesAtLatch + 1, fans.ReleaseAllCalls);
        var state = guard.GetState();
        Assert.True(state.WatchdogLatched);
        Assert.NotNull(state.GuardTempC);
    }

    [Fact]
    public void TogglingTheGuardOffAndOn_ClearsTheWatchdogLatch()
    {
        var (_, fans, store, _) = Build();
        var (e, guard, mono) = WatchdogRig(fans, store);
        fans.CpuTemp = 90f;
        Stall(fans, e, guard, mono);
        Stall(fans, e, guard, mono);
        Stall(fans, e, guard, mono);
        Assert.True(guard.WatchdogLatched);

        guard.SetEnabled(false);
        guard.SetEnabled(true);
        Assert.False(guard.WatchdogLatched);

        var writes = fans.Driven.Count;
        mono[0] += 1000;
        guard.TickCompleted();
        e.Tick();
        Assert.True(fans.Driven.Count > writes);
    }

    private sealed class BlockingReleaseFans : IFanControlProvider
    {
        private readonly Fans _inner;
        public readonly ManualResetEventSlim ReleaseStarted = new(false);
        public readonly ManualResetEventSlim AllowRelease = new(false);
        public BlockingReleaseFans(Fans inner) { _inner = inner; }
        public IReadOnlyList<FanChannel> GetFanChannels() => _inner.GetFanChannels();
        public IReadOnlyList<TemperatureSource> GetTemperatureSources() => _inner.GetTemperatureSources();
        public float? ReadTemperature(string sensorId) => _inner.ReadTemperature(sensorId);
        public int SetFanSpeed(string channelId, int dutyPercent) => _inner.SetFanSpeed(channelId, dutyPercent);
        public void DriveFanSpeed(string channelId, int dutyPercent) => _inner.DriveFanSpeed(channelId, dutyPercent);
        public void ReleaseFan(string channelId) => _inner.ReleaseFan(channelId);
        public void ReleaseAll()
        {
            ReleaseStarted.Set();
            AllowRelease.Wait();
            _inner.ReleaseAll();
        }
        public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct)
            => _inner.CalibrateAsync(fanIds, progress, ct);
    }

    [Fact]
    public async Task TheRedriveFlag_IsOnlySetOnceTheWatchdogsReleaseHasReturned()
    {
        var (_, fans, store, _) = Build();
        var blocking = new BlockingReleaseFans(fans);
        var mono = new long[] { 1_000 };
        var guard = new ThermalGuardController(blocking, store) { MonotonicMs = () => mono[0] };
        var e = new CurveEngine(blocking, store, new MultiplexHub(), null, guard) { Clock = () => mono[0] };
        fans.CpuTemp = 90f;
        mono[0] += 1000;
        e.Tick();
        guard.TickCompleted();

        var watchdog = Task.Run(() => guard.WatchdogCheck(mono[0] + 11_000));
        Assert.True(blocking.ReleaseStarted.Wait(5000));
        Assert.False(guard.ConsumeWatchdogRelease());

        blocking.AllowRelease.Set();
        await watchdog;
        Assert.True(guard.ConsumeWatchdogRelease());
    }

    private sealed class FlakySensors : ISensorProvider
    {
        public int ModelCalls;
        public bool ThrowFirst = true;
        public string Model = "AMD Ryzen 7 9800X3D 8-Core Processor";
        public float? TjMax;
        public float? GetCpuTjMaxC() => TjMax;
        public string GetCpuModel() => Model;
        public string GetCpuModelCached()
        {
            if (ModelCalls++ == 0 && ThrowFirst)
            {
                throw new InvalidOperationException("hardware busy");
            }
            return GetCpuModel();
        }
        public IReadOnlyList<HardwareSensor> GetCpuSensors() => Array.Empty<HardwareSensor>();
        public (bool Healthy, float DistanceToTJMax) GetCpuHealth() => (true, 0f);
        public IReadOnlyList<string> GetGpuModels() => Array.Empty<string>();
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

    private static (CurveEngine Engine, Fans Fans, InMemoryConfigStore Store, ThermalGuardController Guard, long[] Clock) LimitRig(FlakySensors? sensors)
    {
        var (_, fans, store, _) = Build();
        var guard = new ThermalGuardController(fans, store, null, sensors);
        var clock = new long[] { 1_000_000 };
        var e = new CurveEngine(fans, store, new MultiplexHub(), null, guard) { Clock = () => clock[0] };
        fans.CpuTemp = 50f;
        e.Tick();
        return (e, fans, store, guard, clock);
    }

    [Theory]
    [InlineData(false)] // default source
    [InlineData(true)] // spec source
    public void LimitOverride_AppliesWhenTheDetectedLimitIsNotFromTheHardware(bool spec)
    {
        var sensors = spec ? new FlakySensors { ThrowFirst = false } : null;
        var (_, _, store, guard, _) = LimitRig(sensors);

        var (state, error) = guard.SetConfig(new SetThermalGuardConfigBody { LimitOverrideC = 100 });

        Assert.Null(error);
        Assert.Equal(100, state!.LimitC);
        Assert.Equal(ThermalLimitSources.User, state.LimitSource);
        Assert.Equal(100, state.LimitOverrideC);
        Assert.Equal(spec ? 95 : 90, state.DetectedLimitC);
        Assert.Equal(spec ? ThermalLimitSources.Spec : ThermalLimitSources.Default, state.DetectedLimitSource);
        Assert.Equal(100, store.Load().Cooling.ThermalGuardLimitOverrideC);
    }

    [Fact]
    public void LimitOverride_AppliesOnAHardwareLimitToo_WithTheDetectedValueKept()
    {
        var (_, _, store, guard, _) = LimitRig(new FlakySensors { ThrowFirst = false, Model = "Intel(R) Core(TM) i9-14900K", TjMax = 100 });
        Assert.Equal(ThermalLimitSources.Hardware, guard.GetState().DetectedLimitSource);

        var (state, error) = guard.SetConfig(new SetThermalGuardConfigBody { LimitOverrideC = 108 });

        Assert.Null(error);
        Assert.Equal(108, state!.LimitC);
        Assert.Equal(ThermalLimitSources.User, state.LimitSource);
        Assert.Equal(100, state.DetectedLimitC);
        Assert.Equal(ThermalLimitSources.Hardware, state.DetectedLimitSource);
        Assert.Equal(108, store.Load().Cooling.ThermalGuardLimitOverrideC);
    }

    [Fact]
    public void LimitDetection_RunsWithTheGuardOff_AndAnOverrideStillWinsOnAHardwareLimit()
    {
        var (_, fans, store, _) = Build();
        store.Update(s => s.Cooling.ThermalGuardEnabled = false);
        var guard = new ThermalGuardController(fans, store, null, new FlakySensors { ThrowFirst = false, Model = "Intel(R) Core(TM) i9-14900K", TjMax = 100 });
        var e = new CurveEngine(fans, store, new MultiplexHub(), null, guard) { Clock = () => 1_000_000 };
        e.Tick();

        Assert.Equal(ThermalLimitSources.Hardware, guard.GetState().DetectedLimitSource);
        var (state, error) = guard.SetConfig(new SetThermalGuardConfigBody { LimitOverrideC = 105 });
        Assert.Null(error);
        Assert.Equal(105, state!.LimitOverrideC);
        Assert.Equal(105, store.Load().Cooling.ThermalGuardLimitOverrideC);
    }

    [Fact]
    public void WhileTheCpuModelIsStillUnknown_AnOverrideIsRejectedAsStillDetecting()
    {
        var (_, _, store, guard, _) = LimitRig(new FlakySensors()); // first model read throws
        var (state, error) = guard.SetConfig(new SetThermalGuardConfigBody { LimitOverrideC = 100 });
        Assert.Null(state);
        Assert.Contains("Still detecting", error);
        Assert.Null(store.Load().Cooling.ThermalGuardLimitOverrideC);
    }

    [Fact]
    public void ALimitAndAResetInOneBody_IsRejected()
    {
        var (_, _, store, guard, _) = LimitRig(null);
        var (state, error) = guard.SetConfig(new SetThermalGuardConfigBody { LimitOverrideC = 100, ClearLimitOverride = true });
        Assert.Null(state);
        Assert.Contains("not both", error);
        Assert.Null(store.Load().Cooling.ThermalGuardLimitOverrideC);
    }

    [Fact]
    public void AStoredOverride_StaysWhenTheHardwareReportsItsOwnLimit()
    {
        var (_, fans, store, _) = Build();
        store.Update(s => s.Cooling.ThermalGuardLimitOverrideC = 105);
        var guard = new ThermalGuardController(fans, store, null, new FlakySensors { ThrowFirst = false, Model = "Intel(R) Core(TM) i9-14900K", TjMax = 100 });
        var clock = new long[] { 1_000_000 };
        var e = new CurveEngine(fans, store, new MultiplexHub(), null, guard) { Clock = () => clock[0] };

        e.Tick();

        Assert.Equal(105, store.Load().Cooling.ThermalGuardLimitOverrideC);
        Assert.Equal(105, guard.GetState().LimitC);
        Assert.Equal(ThermalLimitSources.User, guard.GetState().LimitSource);
    }

    [Theory]
    [InlineData(200, 110)]
    [InlineData(10, 90)]
    [InlineData(95, 95)]
    public void LimitOverride_IsClampedToNinetyToOneTen_OnA95DetectedLimit(double requested, double expected)
    {
        var (_, _, store, guard, _) = LimitRig(new FlakySensors { ThrowFirst = false });
        Assert.Equal(95, guard.GetState().DetectedLimitC);
        var (state, error) = guard.SetConfig(new SetThermalGuardConfigBody { LimitOverrideC = requested });
        Assert.Null(error);
        Assert.Equal(expected, state!.LimitC);
        Assert.Equal(expected, store.Load().Cooling.ThermalGuardLimitOverrideC);
    }

    [Theory]
    [InlineData(10, 89)]
    [InlineData(89, 89)]
    [InlineData(200, 110)]
    public void LimitOverride_RangeIncludesTheDetectedLimit_SoAn89CPartCanSitAtItsOwnValue(double requested, double expected)
    {
        var (_, _, _, guard, _) = LimitRig(new FlakySensors { ThrowFirst = false, Model = "AMD Ryzen 7 7800X3D 8-Core Processor" });
        Assert.Equal(89, guard.GetState().DetectedLimitC);
        var (state, error) = guard.SetConfig(new SetThermalGuardConfigBody { LimitOverrideC = requested });
        Assert.Null(error);
        Assert.Equal(expected, state!.LimitC);
    }

    [Fact]
    public void ClearLimitOverride_RestoresTheDetectedLimit_AndAPartialUpdateLeavesTheRestAlone()
    {
        var (_, _, store, guard, _) = LimitRig(null);
        guard.SetConfig(new SetThermalGuardConfigBody { LimitOverrideC = 105 });

        // Toggling the guard alone keeps the override.
        guard.SetConfig(new SetThermalGuardConfigBody { Enabled = true });
        Assert.Equal(105, store.Load().Cooling.ThermalGuardLimitOverrideC);

        var (state, _) = guard.SetConfig(new SetThermalGuardConfigBody { ClearLimitOverride = true });

        Assert.Null(store.Load().Cooling.ThermalGuardLimitOverrideC);
        Assert.Equal(90, state!.LimitC);
        Assert.Equal(ThermalLimitSources.Default, state.LimitSource);
        Assert.Null(state.LimitOverrideC);
    }

    [Fact]
    public void TheFloorAndTheTripMoveWithTheOverride()
    {
        var (e, fans, store, guard, clock) = LimitRig(null);
        guard.SetConfig(new SetThermalGuardConfigBody { LimitOverrideC = 100 });

        // 95 C against a limit of 100 is a 40 percent floor (at the default limit it would be 60).
        fans.CpuTemp = 95f;
        clock[0] += 1000;
        e.Tick();
        Assert.Equal(40, LastDuty(fans, "f1"));

        // 99 C held for longer than the trip sustain: past the default limit's trip point, not this one.
        fans.CpuTemp = 99f;
        for (var i = 0; i < 8; i++)
        {
            clock[0] += 1000;
            e.Tick();
        }
        Assert.Null(store.Load().Cooling.LastThermalTrip);

        // 104 C is beyond the overridden limit plus its margin.
        fans.CpuTemp = 104f;
        clock[0] += 1000;
        e.Tick();
        clock[0] += 5000;
        e.Tick();
        Assert.NotNull(store.Load().Cooling.LastThermalTrip);
        Assert.Equal(100, LastDuty(fans, "f1"));
    }

    [Fact]
    public void CpuModelThatThrowsOnTheFirstRead_ResolvesTheLimitOnTheRetry()
    {
        var (_, fans, store, _) = Build();
        var sensors = new FlakySensors();
        var guard = new ThermalGuardController(fans, store, null, sensors);
        var clock = new long[] { 1_000_000 };
        var e = new CurveEngine(fans, store, new MultiplexHub(), null, guard) { Clock = () => clock[0] };
        fans.CpuTemp = 50f;

        e.Tick();
        Assert.Equal(ThermalLimitSources.Default, guard.GetState().LimitSource);
        Assert.Equal(ThermalLimits.GenericDefaultC, guard.GetState().LimitC);

        clock[0] += 6000;
        e.Tick();
        Assert.Equal(ThermalLimitSources.Spec, guard.GetState().LimitSource);
        Assert.Equal(95, guard.GetState().LimitC);
    }

    [Fact]
    public void NonRouteCurveWriters_ClearTheHealUndoState()
    {
        var (_, fans, store, _) = Build();
        var guard = new ThermalGuardController(fans, store);
        guard.HealNow(automatic: false);
        Assert.NotNull(store.Load().Cooling.HealSnapshot);

        // The provider behind the MCP tools and every curve save.
        new StubCoolingProvider(store).SetCurves(new SetCurvesBody());
        Assert.Null(store.Load().Cooling.HealSnapshot);

        store.Update(s => s.Cooling.Curves.Add(Flat("f1", 20)));
        guard.HealNow(automatic: false);
        Assert.NotNull(store.Load().Cooling.HealSnapshot);
        FanProfiles.DetachFanFromCurves("not-a-curve-output", store);
        Assert.NotNull(store.Load().Cooling.HealSnapshot);
        FanProfiles.DetachFanFromCurves("f1", store);
        Assert.Null(store.Load().Cooling.HealSnapshot);
    }

    [Fact]
    public void NvmlSlowdown_SuccessAndNotSupportedAreCachedForGood_TransientRetriesAfterBackoff()
    {
        var cache = new Dictionary<int, (GpuSlowdownThreshold.Read Read, long RetryAtMs)>();
        var calls = 0;
        GpuSlowdownThreshold.Read Next(GpuSlowdownThreshold.Read r) { calls++; return r; }

        // Transient: retried only after the backoff.
        Assert.Null(GpuSlowdownThreshold.Resolve(0, () => Next(GpuSlowdownThreshold.Read.Transient), 1000, cache));
        Assert.Null(GpuSlowdownThreshold.Resolve(0, () => Next(GpuSlowdownThreshold.Read.Transient), 2000, cache));
        Assert.Equal(1, calls);
        Assert.Equal(88, GpuSlowdownThreshold.Resolve(0, () => Next(GpuSlowdownThreshold.Read.Ok(88)), 1000 + 5 * 60_000, cache));
        Assert.Equal(88, GpuSlowdownThreshold.Resolve(0, () => Next(GpuSlowdownThreshold.Read.Transient), 10_000_000, cache));
        Assert.Equal(2, calls);

        // Not supported: never asked again.
        Assert.Null(GpuSlowdownThreshold.Resolve(1, () => Next(GpuSlowdownThreshold.Read.NotSupported), 0, cache));
        Assert.Null(GpuSlowdownThreshold.Resolve(1, () => Next(GpuSlowdownThreshold.Read.Ok(90)), 10_000_000, cache));
        Assert.Equal(3, calls);
    }

    [Fact]
    public void ManualOnlyConfig_DoesNotRewriteTheGuardOverrideEveryTick()
    {
        var fans = new Fans();
        fans.Channels.Add(new FanChannel { Id = "m1", Name = "M1" });
        var store = new InMemoryConfigStore();
        store.Update(s => s.Cooling.ManualSpeeds["m1"] = 30);
        var clock = new long[] { 1_000_000 };
        var engine = new CurveEngine(fans, store, new MultiplexHub(), null, new ThermalGuardController(fans, store)) { Clock = () => clock[0] };
        fans.CpuTemp = 85f;
        engine.Tick();
        var writes = fans.Driven.Count;
        Assert.True(writes >= 1);
        for (var i = 0; i < 3; i++)
        {
            clock[0] += 1000;
            engine.Tick();
        }
        Assert.Equal(writes, fans.Driven.Count);
    }

}
