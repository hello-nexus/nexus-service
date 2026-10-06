using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Lifecycle;
using Nexus.Service.Models.Cooling;
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
        fans.CpuTemp = 80f; // floor 50 at the default limit of 90
        engine.Tick();
        Assert.Equal(50, LastDuty(fans, "f1"));
        Assert.Equal(50, LastDuty(fans, "f2"));

        fans.CpuTemp = 50f;
        clock[0] += 1000;
        engine.Tick();
        Assert.Equal(20, LastDuty(fans, "f1"));
        Assert.Equal(30, LastDuty(fans, "f2"));
    }

    [Fact]
    public void LimitTrip_ForcesEveryNexusDrivenChannelToMax()
    {
        var (engine, fans, _, _) = Build();
        fans.CpuTemp = 95f;
        engine.Tick();
        Assert.Equal(100, LastDuty(fans, "f1"));
        Assert.Equal(100, LastDuty(fans, "f2"));
    }

    [Fact]
    public void Trip_PersistsTheRecordAndReportsTrippedState()
    {
        var (engine, fans, store, _) = Build();
        var guard = new ThermalGuardController(fans, store);
        fans.CpuTemp = 95f;
        engine.Tick();

        var trip = store.Load().Cooling.LastThermalTrip;
        Assert.NotNull(trip);
        Assert.Equal(ThermalTripReasons.Limit, trip!.Reason);
        Assert.Null(trip.EndedAtUtcMs);
    }

    [Fact]
    public void LockedChannelNexusDrives_IsRaisedByTheGuard_ButUncontrolledNever()
    {
        var (engine, fans, store, _) = Build();
        fans.Channels[0].Kind = FanKinds.Pump; // pumps are locked by default
        store.Update(s =>
        {
            s.Cooling.FanLockOverrides["f2"] = true;
            s.Cooling.Curves.Add(Flat("f3", 20));
        });
        fans.Channels.Add(new FanChannel { Id = "f3", Name = "Fan 3" });
        store.Update(s => s.Cooling.UncontrolledFanChannels.Add("f3"));
        fans.CpuTemp = 99f;
        engine.Tick();

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
        var (engine, fans, store, _) = Build();
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
        fans.CpuTemp = 99f;

        engine.Tick();

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
        fans.CpuTemp = 99f;
        engine.Tick();
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
        var (engine, fans, store, _) = Build();
        fans.CpuTemp = 99f;
        engine.Tick();
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
            fans.CpuTemp = 99f;
            engine.Tick();
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
    public void WatchdogRelease_MakesTheNextTickRedriveEveryChannel()
    {
        var (engine, fans, store, _) = Build();
        var guard = new ThermalGuardController(fans, store);
        var e = new CurveEngine(fans, store, new MultiplexHub(), null, guard) { Clock = () => 1_000_000 };
        fans.CpuTemp = 80f;
        e.Tick();
        var afterFirst = fans.Driven.Count;
        e.Tick();
        Assert.Equal(afterFirst, fans.Driven.Count);

        guard.WatchdogCheck(Environment.TickCount64 + 11_000);
        e.Tick();

        Assert.True(fans.Driven.Count > afterFirst);
    }

    [Fact]
    public void HealedSyncMember_IsNotScaledByTheGlobalModifier()
    {
        var fans = new Fans();
        fans.Channels.Add(new FanChannel { Id = "gpufan", Name = "GPU Fan", IsGpu = true, DeviceId = "/gpu-nvidia/0", DutyPercent = 40 });
        fans.Channels.Add(new FanChannel { Id = "f1", Name = "Fan 1" });
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Cooling.GlobalSpeedModifier = 1.5;
            s.Cooling.Curves.Add(new CurveDocument
            {
                Id = "sync", Name = "sync", Type = "Sync",
                Sync = new SyncCurveData { SourceChannelId = "gpufan" },
                Outputs = { new CurveOutputDocument { Id = "f1", Type = "Fan" } },
            });
        });
        var guard = new ThermalGuardController(fans, store);
        guard.HealNow(automatic: false);
        Assert.Contains(store.Load().Cooling.Curves, c => c.Id == CoolingConfigLint.MixIdPrefix + "f1");

        fans.CpuTemp = 40f;
        new CurveEngine(fans, store, new MultiplexHub(), null, guard) { Clock = () => 1_000_000 }.Tick();

        Assert.Equal(40, LastDuty(fans, "f1"));
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
        settings.ClearHeal();
        Assert.Null(settings.HealSnapshot);
        Assert.Null(settings.HealedAtUtcMs);
        Assert.Empty(settings.HealedChannels);
    }

    [Fact]
    public void Escalated_StopsWritingAndReleasesAllToBios()
    {
        var (engine, fans, _, clock) = Build();
        fans.CpuTemp = 95f;
        engine.Tick();
        var writes = fans.Driven.Count;

        fans.CpuTemp = 97f;
        clock[0] += 31_000;
        engine.Tick();

        Assert.Equal(1, fans.ReleaseAllCalls);
        Assert.Equal(writes, fans.Driven.Count);
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

    [Fact]
    public void Watchdog_ReleasesAllWhenTheEngineStallsWhileHot_AndNotWhenCool()
    {
        var (engine, fans, store, _) = Build();
        var guard = new ThermalGuardController(fans, store);
        var e = new CurveEngine(fans, store, new MultiplexHub(), null, guard) { Clock = () => 1_000_000 };
        fans.CpuTemp = 85f;
        e.Tick();

        guard.WatchdogCheck(Environment.TickCount64 + 11_000);
        Assert.Equal(1, fans.ReleaseAllCalls);

        var (e2, fans2, store2, _) = Build();
        var guard2 = new ThermalGuardController(fans2, store2);
        var eng2 = new CurveEngine(fans2, store2, new MultiplexHub(), null, guard2) { Clock = () => 1_000_000 };
        fans2.CpuTemp = 50f;
        eng2.Tick();
        guard2.WatchdogCheck(Environment.TickCount64 + 11_000);
        Assert.Equal(0, fans2.ReleaseAllCalls);
    }
}
