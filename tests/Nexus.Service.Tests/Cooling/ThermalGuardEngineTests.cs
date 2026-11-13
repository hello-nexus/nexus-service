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
        public int KeepingPortsCalls;
        public bool Restores = true;
        public bool ReleaseRestoresHardwareControl(string channelId) => Restores;

        public IReadOnlyList<FanChannel> GetFanChannels() => Channels;
        public Action? OnSources;
        public IReadOnlyList<TemperatureSource> GetTemperatureSources() =>
            Hook().Concat(new[] { new TemperatureSource { Id = "cpu", Name = "Core (Tctl/Tdie)", Category = "CPU", Value = CpuTemp } })
                .Concat(ExtraSources).ToList();
        private IEnumerable<TemperatureSource> Hook()
        {
            var h = OnSources;
            OnSources = null;
            h?.Invoke();
            return Array.Empty<TemperatureSource>();
        }
        public readonly HashSet<string> MissingSensors = new();
        public float? ReadTemperature(string sensorId) =>
            sensorId == "boom" ? throw new InvalidOperationException("sensor failure")
            : MissingSensors.Contains(sensorId) ? null : CpuTemp;
        public readonly List<(string Id, int Duty)> Order = new();
        public int SetFanSpeed(string channelId, int dutyPercent)
        {
            Order.Add((channelId, dutyPercent));
            return dutyPercent;
        }
        public void DriveFanSpeed(string channelId, int dutyPercent)
        {
            Driven.Add((channelId, dutyPercent));
            Order.Add((channelId, dutyPercent));
        }
        public void ReleaseFan(string channelId) => Released.Add(channelId);
        public void ReleaseAll() => ReleaseAllCalls++;
        public void ReleaseAllKeepingDrivenPorts() => KeepingPortsCalls++;
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
            s.Cooling.Curves.Add(Flat("f1", 10));
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

    // Holds a temperature past the default limit, one tick per second, until the limit trip fires.
    private static void Trip(CurveEngine engine, Fans fans, long[] clock)
    {
        fans.CpuTemp = 99f;
        engine.Tick();
        for (var i = 0; i < 5; i++)
        {
            clock[0] += 1000;
            engine.Tick();
        }
    }

    // A slow climb at low fan duty and unknown load, until the guard declares lost cooling.
    private static void TripOnCoolingLoss(CurveEngine engine, Fans fans, InMemoryConfigStore store, long[] clock)
    {
        for (var s = 0; s < 400 && store.Load().Cooling.LastThermalTrip is null; s++)
        {
            fans.CpuTemp = 70f + s * 0.1f;
            clock[0] += 1000;
            engine.Tick();
        }
        Assert.Equal(ThermalTripReasons.CoolingLoss, store.Load().Cooling.LastThermalTrip!.Reason);
    }

    // Ticks once a second, as the engine does, until the guard has had time to release.
    private static void CoolDown(CurveEngine engine, long[] clock)
    {
        for (var i = 0; i < 70; i++)
        {
            clock[0] += 1000;
            engine.Tick();
        }
    }

    private static int LastDuty(Fans fans, string id) => fans.Driven.Last(d => d.Id == id).Duty;

    [Fact]
    public void Trip_DrivesAHardwareControlChannelToMax_ThenHandsItBackWhenTheGuardClears()
    {
        var (engine, fans, _, clock) = Build();
        fans.Channels.Add(new FanChannel { Id = "hw", Name = "Wireless Fan", Mode = FanModes.Auto });
        engine.Tick();
        Assert.DoesNotContain(fans.Driven, d => d.Id == "hw");

        Trip(engine, fans, clock);
        Assert.Equal(100, LastDuty(fans, "hw"));
        Assert.DoesNotContain("hw", fans.Released);

        fans.CpuTemp = 40f;
        CoolDown(engine, clock);
        Assert.Contains("hw", fans.Released);
    }

    [Fact]
    public void IdleConfig_AllHardwareControl_TripDrivesChannelsToMax_ThenReleasesThem()
    {
        var (engine, fans, store, clock) = Build();
        store.Update(s => { s.Cooling.Curves.Clear(); s.Cooling.ManualSpeeds.Clear(); });
        fans.Channels.Clear();
        fans.Channels.Add(new FanChannel { Id = "hw1", Mode = FanModes.Auto });
        fans.Channels.Add(new FanChannel { Id = "hw2", Mode = FanModes.Auto });

        fans.CpuTemp = 40f;
        for (var i = 0; i < 3; i++)
        {
            clock[0] += 1000;
            engine.Tick();
        }
        Assert.Empty(fans.Driven);
        Assert.Empty(fans.Released);

        Trip(engine, fans, clock);
        // The idle probe runs every few seconds, so the guard wakes up to one probe interval late.
        for (var i = 0; i < 8; i++)
        {
            clock[0] += 1000;
            engine.Tick();
        }
        Assert.Equal(100, LastDuty(fans, "hw1"));
        Assert.Equal(100, LastDuty(fans, "hw2"));

        fans.CpuTemp = 40f;
        CoolDown(engine, clock);
        Assert.Contains("hw1", fans.Released);
        Assert.Contains("hw2", fans.Released);
    }

    private static (CurveEngine Engine, Fans Fans, InMemoryConfigStore Store, long[] Clock, ThermalGuardController Guard) BuildIdle(params string[] hardwareIds)
    {
        var fans = new Fans();
        foreach (var id in hardwareIds)
        {
            fans.Channels.Add(new FanChannel { Id = id, Mode = FanModes.Auto });
        }
        var store = new InMemoryConfigStore();
        var clock = new long[] { 1_000_000 };
        var hub = new MultiplexHub();
        var guard = new ThermalGuardController(fans, store, hub);
        var engine = new CurveEngine(fans, store, hub, new FeatureGates(store), guard) { Clock = () => clock[0] };
        return (engine, fans, store, clock, guard);
    }

    private static void Hot(CurveEngine engine, long[] clock, int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            clock[0] += 1000;
            engine.Tick();
        }
    }

    [Fact]
    public void FloorRamp_LeavesHardwareControlChannelsAlone()
    {
        var (engine, fans, _, clock, _) = BuildIdle("hw");
        fans.CpuTemp = 85f;
        Hot(engine, clock, 12);
        Assert.DoesNotContain(fans.Driven, d => d.Id == "hw");
        Assert.DoesNotContain("hw", fans.Released);
    }

    [Fact]
    public void ProviderThatCannotHandBackIsNeverTakenOver()
    {
        var (engine, fans, _, clock, _) = BuildIdle("hw");
        fans.Restores = false;
        fans.CpuTemp = 99f;
        Hot(engine, clock, 20);
        Assert.DoesNotContain(fans.Driven, d => d.Id == "hw");
    }

    [Fact]
    public void ManualDutyGivenDuringATrip_SurvivesTheRelease()
    {
        var (engine, fans, store, clock, _) = BuildIdle("hw");
        fans.CpuTemp = 99f;
        Hot(engine, clock, 20);
        Assert.Equal(100, LastDuty(fans, "hw"));

        store.Update(s => s.Cooling.ManualSpeeds["hw"] = 50);
        fans.CpuTemp = 40f;
        CoolDown(engine, clock);
        Assert.DoesNotContain("hw", fans.Released);
        Assert.Equal(50, store.Load().Cooling.ManualSpeeds["hw"]);
    }

    [Fact]
    public void GuardDisabledMidTrip_OnAnIdleConfig_ReleasesTheChannels()
    {
        var (engine, fans, store, clock, _) = BuildIdle("hw");
        fans.CpuTemp = 99f;
        Hot(engine, clock, 20);
        Assert.Equal(100, LastDuty(fans, "hw"));

        store.Update(s => s.Cooling.ThermalGuardEnabled = false);
        Hot(engine, clock, 2);
        Assert.Contains("hw", fans.Released);
    }

    [Fact]
    public void TrippedAtConstantMax_DoesNotWriteAHardwareChannelEveryTick()
    {
        var (engine, fans, _, clock, _) = BuildIdle("hw");
        fans.CpuTemp = 99f;
        Hot(engine, clock, 20);
        var before = fans.Driven.Count(d => d.Id == "hw");
        Assert.True(before >= 1);

        Hot(engine, clock, 10);
        Assert.Equal(before, fans.Driven.Count(d => d.Id == "hw"));
    }

    [Fact]
    public void ChannelThatLeavesMidTrip_IsReleasedWhenItReturnsAfterTheGuardClears()
    {
        var (engine, fans, _, clock, _) = BuildIdle("hw");
        fans.CpuTemp = 99f;
        Hot(engine, clock, 20);
        var hw = fans.Channels.Single();
        fans.Channels.Clear();
        fans.CpuTemp = 40f;
        CoolDown(engine, clock);
        var releases = fans.Released.Count(r => r == "hw");
        Assert.Equal(1, releases);

        fans.Channels.Add(hw);
        Hot(engine, clock, 2);
        Assert.True(fans.Released.Count(r => r == "hw") > releases);
    }

    [Fact]
    public void ChannelThatVanishesMidTrip_IsReleasedAtOnce_AndNotDrivenToMaxWhenItReturnsAfterTheGuardClears()
    {
        var (engine, fans, _, clock, _) = BuildIdle("hw");
        fans.CpuTemp = 99f;
        Hot(engine, clock, 20);
        var hw = fans.Channels.Single();
        fans.Channels.Clear();
        Hot(engine, clock, 2);
        Assert.Contains("hw", fans.Released);

        fans.CpuTemp = 40f;
        clock[0] += 11 * 60_000;
        CoolDown(engine, clock);
        var driven = fans.Driven.Count;
        fans.Channels.Add(hw);
        Hot(engine, clock, 3);
        Assert.Equal(driven, fans.Driven.Count);
    }

    [Fact]
    public void FansNotResponding_IgnoresHardwareChannelsInTheFloorState()
    {
        var (engine, fans, store, clock, guard) = BuildIdle("hw");
        store.Update(s => s.Cooling.Curves.Add(Flat("c", 10)));
        fans.Channels.Add(new FanChannel { Id = "c", Rpm = 1500 });
        fans.Channels.Single(c => c.Id == "hw").Rpm = 0;
        fans.CpuTemp = 85f;
        for (var i = 0; i < 90; i++)
        {
            fans.Channels.Single(c => c.Id == "hw").Rpm = i < 3 ? 1200 : 0;
            clock[0] += 1000;
            engine.Tick();
        }
        Assert.Equal("floor", guard.GetState().State);
        Assert.False(guard.GetState().FansNotResponding);
    }

    private static ThermalGuardResponse TripWithRpm(Func<int, int> rpmAt, int hotTicks)
    {
        var (engine, fans, _, clock, guard) = BuildIdle("hw");
        var fan = fans.Channels.Single();
        fans.CpuTemp = 99f;
        for (var i = 0; i < hotTicks; i++)
        {
            fan.Rpm = rpmAt(i);
            clock[0] += 1000;
            engine.Tick();
        }
        return guard.GetState();
    }

    [Fact]
    public void FansNotResponding_IsSetWhenADrivenFanThatSpunStopsForAWhile()
    {
        var state = TripWithRpm(i => i < 5 ? 1500 : 0, 90);
        Assert.Equal("tripped", state.State);
        Assert.True(state.FansNotResponding);
    }

    [Fact]
    public void FansNotResponding_IsNotSetForAFanStillSpinningUp()
    {
        var state = TripWithRpm(i => i < 40 ? 0 : 1500, 60);
        Assert.False(state.FansNotResponding);
        var early = TripWithRpm(i => i < 5 ? 1500 : 0, 30);
        Assert.False(early.FansNotResponding);
    }

    [Fact]
    public void FansNotResponding_IsNotSetForAFanThatNeverReportsRpm()
    {
        var state = TripWithRpm(_ => 0, 120);
        Assert.Equal("tripped", state.State);
        Assert.False(state.FansNotResponding);
    }

    [Fact]
    public void Trip_LeavesAnUncontrolledHardwareChannelAlone()
    {
        var (engine, fans, store, clock) = Build();
        fans.Channels.Add(new FanChannel { Id = "hw", Name = "Wireless Fan", Mode = FanModes.Auto });
        store.Update(s => s.Cooling.UncontrolledFanChannels.Add("hw"));
        Trip(engine, fans, clock);
        Assert.DoesNotContain(fans.Driven, d => d.Id == "hw");
    }

    [Fact]
    public void ChannelThatEnumeratesAfterBootApply_StaysOnThePresetAndIsDriven()
    {
        var (engine, fans, store, clock) = Build();
        store.Update(s => { s.Cooling.Curves.Clear(); s.Cooling.ManualSpeeds.Clear(); });
        fans.Channels.Add(new FanChannel { Id = "w1", Name = "Wireless Fan 1" });
        FanProfiles.Apply("turbo", fans, store);
        Assert.Contains(store.Load().Cooling.Curves.Single(c => c.Preset == "turbo").Outputs, o => o.Id == "w1");

        // Boot: the wireless chain is not enumerated yet when the restore re-applies the preset.
        var wireless = fans.Channels.Single(c => c.Id == "w1");
        fans.Channels.Remove(wireless);
        FanProfiles.Apply("turbo", fans, store);
        Assert.Contains(store.Load().Cooling.Curves.Single(c => c.Preset == "turbo").Outputs, o => o.Id == "w1");

        fans.Channels.Add(wireless);
        fans.CpuTemp = 50f;
        clock[0] += 1000;
        engine.Tick();
        Assert.Contains(fans.Driven, d => d.Id == "w1");
        Assert.Contains(store.Load().Cooling.Curves.Single(c => c.Preset == "turbo").Outputs, o => o.Id == "w1");
    }

    [Fact]
    public void BelowFloorBand_CurveDutiesAreUntouched()
    {
        var (engine, fans, _, _) = Build();
        fans.CpuTemp = 50f;
        engine.Tick();
        Assert.Equal(new[] { ("f1", 10) }, fans.Driven);
    }

    [Fact]
    public void Floor_RaisesACurveDutyAndAManualDuty_ThenRestoresTheManualOne()
    {
        var (engine, fans, _, clock) = Build();
        fans.CpuTemp = 85f;
        engine.Tick();
        Assert.Equal(40, LastDuty(fans, "f1"));
        Assert.Equal(40, LastDuty(fans, "f2"));

        fans.CpuTemp = 50f;
        clock[0] += 1000;
        engine.Tick();
        Assert.Equal(10, LastDuty(fans, "f1"));
        Assert.Equal(30, LastDuty(fans, "f2"));
    }

    private static (CurveEngine Engine, Fans Fans, InMemoryConfigStore Store, long[] Clock, ThermalGuardController Guard) BuildWithGuard()
    {
        var fans = new Fans();
        fans.Channels.Add(new FanChannel { Id = "f1", Name = "Fan 1" });
        fans.Channels.Add(new FanChannel { Id = "f2", Name = "Fan 2" });
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Cooling.Curves.Add(Flat("f1", 70));
            s.Cooling.ManualSpeeds["f2"] = 30;
        });
        var clock = new long[] { 1_000_000 };
        var hub = new MultiplexHub();
        var guard = new ThermalGuardController(fans, store, hub);
        var engine = new CurveEngine(fans, store, hub, new FeatureGates(store), guard) { Clock = () => clock[0] };
        return (engine, fans, store, clock, guard);
    }

    private static void Hold(CurveEngine engine, Fans fans, long[] clock, float tempC, int ticks = 6)
    {
        fans.CpuTemp = tempC;
        for (var i = 0; i < ticks; i++)
        {
            clock[0] += 1000;
            engine.Tick();
        }
    }

    [Fact]
    public void FloorBelowEveryGuardedDuty_IsReportedAsNormal_AndChangesNoDuty()
    {
        var (engine, fans, store, clock, guard) = BuildWithGuard();
        store.Update(s => s.Cooling.ManualSpeeds.Remove("f2"));
        Hold(engine, fans, clock, 85f);
        Assert.Equal("normal", guard.GetState().State);
        Assert.True(guard.IdleNeedsGuard(Array.Empty<TemperatureSource>()));
        Assert.Equal(70, LastDuty(fans, "f1"));
        Assert.DoesNotContain(fans.Driven, d => d.Duty != 70);
    }

    [Fact]
    public void ASingleTickFlip_DoesNotChangeTheReportedState()
    {
        var (engine, fans, store, clock, guard) = BuildWithGuard();
        store.Update(s => s.Cooling.ManualSpeeds.Remove("f2"));
        Hold(engine, fans, clock, 85f);
        Assert.Equal("normal", guard.GetState().State);
        store.Update(s => s.Cooling.Curves[0].Flat!.Speed = 10);
        Hold(engine, fans, clock, 85f, 1);
        Assert.Equal("normal", guard.GetState().State);
        store.Update(s => s.Cooling.Curves[0].Flat!.Speed = 70);
        Hold(engine, fans, clock, 85f, 1);
        Assert.Equal("normal", guard.GetState().State);
        store.Update(s => s.Cooling.Curves[0].Flat!.Speed = 10);
        Hold(engine, fans, clock, 85f, 6);
        Assert.Equal("floor", guard.GetState().State);
    }

    [Fact]
    public void FloorAboveOneChannelsDuty_IsReportedAsFloor()
    {
        var (engine, fans, _, clock, guard) = BuildWithGuard();
        Hold(engine, fans, clock, 85f);
        Assert.Equal("floor", guard.GetState().State);
        Assert.Equal(70, LastDuty(fans, "f1"));
        Assert.Equal(40, LastDuty(fans, "f2"));
    }

    [Fact]
    public void ReportedState_FollowsTheFloorBeatingADutyBothWays()
    {
        var (engine, fans, _, clock, guard) = BuildWithGuard();
        Hold(engine, fans, clock, 80f);
        Assert.Equal("normal", guard.GetState().State);
        Hold(engine, fans, clock, 85f);
        Assert.Equal("floor", guard.GetState().State);
        Hold(engine, fans, clock, 80f);
        Assert.Equal("normal", guard.GetState().State);
        Hold(engine, fans, clock, 50f);
        Assert.Equal("normal", guard.GetState().State);
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
    public void SittingJustUnderTheLimit_DoesNotTrip()
    {
        var (engine, fans, store, clock) = Build();
        fans.CpuTemp = 89.9f;
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
    public void ATrippedGuardOutranksTheMissingSourceFallback()
    {
        var (engine, fans, store, clock) = Build();
        fans.MissingSensors.Add("qx");
        store.Update(s =>
        {
            s.Cooling.Curves.Clear();
            s.Cooling.Curves.Add(new CurveDocument
            {
                Id = "c-f1",
                Name = "c-f1",
                Type = "Linear",
                Input = new CurveInputDocument { Id = "qx", Type = "Temperature" },
                Outputs = { new CurveOutputDocument { Id = "f1", Type = "Fan" } },
                Linear = new LinearCurveData { ResponseTime = 0, MinTemp = 20, MaxTemp = 60, MinSpeed = 20, MaxSpeed = 60 },
            });
        });

        Trip(engine, fans, clock);
        for (var i = 0; i < 4; i++)
        {
            clock[0] += 1000;
            engine.Tick();
        }

        var trip = store.Load().Cooling.LastThermalTrip;
        Assert.NotNull(trip);
        Assert.Null(trip!.EndedAtUtcMs);
        // The source has been missing past the grace, yet the fan runs at the guard's duty, never the fallback's.
        Assert.Equal(100, fans.Order.Last(o => o.Id == "f1").Duty);
        Assert.DoesNotContain(fans.Order.Skip(fans.Order.FindLastIndex(o => o.Id == "f1" && o.Duty == 100)), o => o.Id == "f1" && o.Duty == 70);
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
        fans.Channels[0].Kind = FanKinds.Pump; // pumps are pinned by default
        store.Update(s =>
        {
            s.Cooling.FanPinOverrides["f2"] = true;
            s.Cooling.Curves.Add(Flat("f3", 10));
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
        var bad = Flat("f1", 10);
        bad.Id = "bad";
        bad.Type = "Graph";
        bad.Input = new CurveInputDocument { Id = "boom", Type = "Temperature" };
        bad.Graph = new GraphCurveData { Points = { new Nexus.Service.Persistence.GraphPoint { Temp = 30, Speed = 30 } } };
        store.Update(s =>
        {
            s.Cooling.Curves.Insert(0, bad);
            s.Cooling.Curves.Add(Flat("f4", 10));
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
    public void LimitTripEnd_LeavesCurvesAndManualSpeedsUntouched()
    {
        var (engine, fans, store, clock) = Build();
        Trip(engine, fans, clock);
        fans.CpuTemp = 50f;
        engine.Tick();
        CoolDown(engine, clock);
        var cooling = store.Load().Cooling;
        Assert.NotNull(cooling.LastThermalTrip!.EndedAtUtcMs);
        Assert.Equal(ThermalTripReasons.Limit, cooling.LastThermalTrip.Reason);
        Assert.Null(cooling.HealSnapshot);
        Assert.Equal(10, Assert.Single(cooling.Curves).Flat!.Speed);
        Assert.Equal(30, cooling.ManualSpeeds["f2"]);
    }

    [Fact]
    public void CoolingLossTripEnd_HealsTheCurves()
    {
        var (engine, fans, store, clock) = Build();
        TripOnCoolingLoss(engine, fans, store, clock);
        fans.CpuTemp = 50f;
        engine.Tick();
        CoolDown(engine, clock);
        var cooling = store.Load().Cooling;
        Assert.NotNull(cooling.LastThermalTrip!.EndedAtUtcMs);
        Assert.NotNull(cooling.HealSnapshot);
    }

    [Fact]
    public void EscalatedCoolingLossTripEnd_StillHealsTheCurves()
    {
        var (engine, fans, store, clock) = Build();
        LearnRpm(engine, fans, clock, 1000);
        TripOnCoolingLoss(engine, fans, store, clock);
        fans.CpuTemp = 95f;
        fans.Channels[0].Rpm = 100;
        fans.Channels[1].Rpm = 100;
        for (var i = 0; i < 25; i++)
        {
            clock[0] += 1000;
            engine.Tick();
        }
        Assert.True(store.Load().Cooling.LastThermalTrip!.Escalated);

        fans.CpuTemp = 50f;
        engine.Tick();
        CoolDown(engine, clock);
        var cooling = store.Load().Cooling;
        Assert.NotNull(cooling.LastThermalTrip!.EndedAtUtcMs);
        Assert.Equal(ThermalTripReasons.CoolingLoss, cooling.LastThermalTrip.Reason);
        Assert.NotNull(cooling.HealSnapshot);
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
        store.Update(s => s.Cooling.Curves.Add(Flat("f1", 10)));
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
            CoolDown(engine, clock);
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
    public void ASensorThatComesAndGoes_DoesNotFlipTheGuardCurvesInputOrRewriteIt()
    {
        var (_, fans, store, _) = Build();
        var guard = new ThermalGuardController(fans, store);
        guard.HealNow(automatic: false);
        Assert.Equal("cpu", store.Load().Cooling.Curves.Single(c => c.Id == CoolingConfigLint.GuardCurveId).Input.Id);
        var e = new CurveEngine(fans, store, new MultiplexHub(), null, guard) { Clock = () => 1_000_000 };
        var changes = 0;
        store.OnChanged += () => changes++;

        // A Package sensor appears (it would be the preferred input), then drops out again.
        fans.ExtraSources.Add(new TemperatureSource { Id = "pkg", Name = "CPU Package", Category = "CPU", Value = 50 });
        e.Tick();
        e.Tick();
        fans.ExtraSources.Clear();
        e.Tick();
        e.Tick();

        Assert.Equal(0, changes);
        Assert.Equal("cpu", store.Load().Cooling.Curves.Single(c => c.Id == CoolingConfigLint.GuardCurveId).Input.Id);
    }

    // A store whose writes fail once they change the guard curve: the failure the curve sync must survive.
    private sealed class GuardWriteFailingStore : IConfigStore
    {
        private readonly InMemoryConfigStore _inner;
        public bool Failing;
        public GuardWriteFailingStore(InMemoryConfigStore inner) { _inner = inner; }
        public string SettingsPath => _inner.SettingsPath;
        public NexusSettings Load() => _inner.Load();
        public void Reload() => _inner.Reload();
        public void FlushNow() => _inner.FlushNow();
        public event Action? OnChanged { add => _inner.OnChanged += value; remove => _inner.OnChanged -= value; }

        private string GuardSignature() => string.Join(",", _inner.Load().Cooling.Curves
            .Where(c => c.Id == CoolingConfigLint.GuardCurveId && c.Graph is not null)
            .SelectMany(c => c.Graph!.Points.Select(p => $"{p.Temp}:{p.Speed}")));

        public void Update(Action<NexusSettings> mutator)
        {
            var before = GuardSignature();
            _inner.Update(mutator);
            if (Failing && GuardSignature() != before)
            {
                throw new InvalidOperationException("store write failed");
            }
        }
    }

    [Fact]
    public void AThrowingCurveSync_StillAppliesTheFloorThatTick()
    {
        var (_, fans, inner, _) = Build();
        var store = new GuardWriteFailingStore(inner);
        var guard = new ThermalGuardController(fans, store);
        guard.HealNow(automatic: false);
        inner.Update(s => s.Cooling.Curves.Single(c => c.Id == CoolingConfigLint.GuardCurveId).Graph!.Points[0].Speed = 55);
        store.Failing = true; // the sync now tries to repair the curve and the write throws
        var clock = new long[] { 1_000_000 };
        var e = new CurveEngine(fans, store, new MultiplexHub(), null, guard) { Clock = () => clock[0] };

        fans.CpuTemp = 85f;
        for (var i = 0; i < 6; i++)
        {
            clock[0] += 1000;
            e.Tick();
        }

        // f2 is a plain manual fan raised by the floor; f1 sits on the guard curve and is at least as high.
        Assert.Equal(40, LastDuty(fans, "f2"));
        Assert.InRange(LastDuty(fans, "f1"), 40, 100);
    }

    private static CurveDocument ManagedGuardCurve(params string[] outputs)
    {
        var cpu = new TemperatureSource { Id = "cpu", Name = "Core", Category = "CPU" };
        var curve = new CurveDocument { Id = CoolingConfigLint.GuardCurveId };
        foreach (var id in outputs)
        {
            curve.Outputs.Add(new CurveOutputDocument { Id = id, Type = "Fan" });
        }
        CoolingConfigLint.ApplyGuardCurve(curve, cpu, 90);
        return curve;
    }

    [Fact]
    public void GuardCurveMatches_ReturnsFalseForEveryNullPart_WithoutThrowing_AndApplyRepairsThem()
    {
        var cpu = new TemperatureSource { Id = "cpu", Name = "Core", Category = "CPU" };
        var damage = new Action<CurveDocument>[]
        {
            c => c.Input = null!,
            c => c.Outputs = null!,
            c => c.Outputs.Add(null!),
            c => c.Graph = null,
            c => c.Graph!.Points = null!,
            c => c.Graph!.Points.Add(null!),
            c => c.Mixed = new MixedCurveData { CurveIds = null! },
        };

        foreach (var hurt in damage)
        {
            var curve = ManagedGuardCurve("f1");
            Assert.True(CoolingConfigLint.GuardCurveMatches(curve, cpu, 90));
            hurt(curve);

            Assert.False(CoolingConfigLint.GuardCurveMatches(curve, cpu, 90));

            CoolingConfigLint.ApplyGuardCurve(curve, cpu, 90);
            Assert.True(CoolingConfigLint.GuardCurveMatches(curve, cpu, 90));
        }
    }

    [Fact]
    public void ApplyGuardCurve_CreatesMissingOutputs_AndDropsNullElements_KeepingTheRealOnes()
    {
        var cpu = new TemperatureSource { Id = "cpu", Name = "Core", Category = "CPU" };

        var missing = new CurveDocument { Id = CoolingConfigLint.GuardCurveId, Outputs = null! };
        CoolingConfigLint.ApplyGuardCurve(missing, cpu, 90);
        Assert.NotNull(missing.Outputs);
        Assert.Empty(missing.Outputs);

        var withNull = ManagedGuardCurve("f1");
        withNull.Outputs.Insert(0, null!);
        CoolingConfigLint.ApplyGuardCurve(withNull, cpu, 90);
        Assert.Equal(new[] { "f1" }, withNull.Outputs.Select(o => o.Id));
    }

    [Fact]
    public void AGuardCurveWithAMissingInput_IsRepairedNotAnError()
    {
        var (_, fans, store, _) = Build();
        var guard = new ThermalGuardController(fans, store);
        guard.HealNow(automatic: false);
        store.Update(s => s.Cooling.Curves.Single(c => c.Id == CoolingConfigLint.GuardCurveId).Input = null!);
        var e = new CurveEngine(fans, store, new MultiplexHub(), null, guard) { Clock = () => 1_000_000 };

        e.Tick();

        Assert.Equal("cpu", store.Load().Cooling.Curves.Single(c => c.Id == CoolingConfigLint.GuardCurveId).Input.Id);
    }

    [Fact]
    public void GuardCurveMatches_ComparesTheWholeManagedShape()
    {
        var cpu = new TemperatureSource { Id = "cpu", Name = "Core", Category = "CPU" };
        var curve = new CurveDocument { Id = CoolingConfigLint.GuardCurveId, Outputs = { new CurveOutputDocument { Id = "f1", Type = "Fan" } } };
        CoolingConfigLint.ApplyGuardCurve(curve, cpu, 90);
        Assert.True(CoolingConfigLint.GuardCurveMatches(curve, cpu, 90));

        var otherDevice = CoolingConfigLint.CloneCurve(curve);
        otherDevice.Input.Device = "GPU";
        Assert.False(CoolingConfigLint.GuardCurveMatches(otherDevice, cpu, 90));

        var strayData = CoolingConfigLint.CloneCurve(curve);
        strayData.Flat = new FlatCurveData { Speed = 5 };
        Assert.False(CoolingConfigLint.GuardCurveMatches(strayData, cpu, 90));

        var preset = CoolingConfigLint.CloneCurve(curve);
        preset.Preset = "silent";
        Assert.False(CoolingConfigLint.GuardCurveMatches(preset, cpu, 90));
    }

    [Fact]
    public void AGuardCurveCarryingAPreset_IsRewrittenOnceNotOnEveryTick()
    {
        var (_, fans, store, _) = Build();
        var guard = new ThermalGuardController(fans, store);
        guard.HealNow(automatic: false);
        store.Update(s => s.Cooling.Curves.Single(c => c.Id == CoolingConfigLint.GuardCurveId).Preset = "silent");
        var e = new CurveEngine(fans, store, new MultiplexHub(), null, guard) { Clock = () => 1_000_000 };
        var changes = 0;
        store.OnChanged += () => changes++;

        e.Tick();
        var afterFirst = changes;
        e.Tick();
        e.Tick();

        Assert.Equal(1, afterFirst);
        Assert.Equal(afterFirst, changes);
        Assert.Null(store.Load().Cooling.Curves.Single(c => c.Id == CoolingConfigLint.GuardCurveId).Preset);
    }

    [Fact]
    public void AThrowingCurveSyncInSetConfig_StillKeepsThePersistedOverrideAndAnswers()
    {
        var (_, fans, inner, _) = Build();
        var store = new GuardWriteFailingStore(inner);
        var guard = new ThermalGuardController(fans, store);
        guard.HealNow(automatic: false);
        store.Failing = true; // the sync after the override change writes new points and throws

        var (state, error) = guard.SetConfig(new SetThermalGuardConfigBody { LimitOverrideC = 100, Enabled = true });

        Assert.Null(error);
        Assert.Equal(100, state!.LimitC);
        Assert.Equal(100, inner.Load().Cooling.ThermalGuardLimitOverrideC);
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
    public void AcknowledgingAnEndedTrip_MarksItAndTheStateReportsIt()
    {
        var (_, fans, store, _) = Build();
        var guard = new ThermalGuardController(fans, store, null, null, null, null, () => 5_000);
        store.Update(s => s.Cooling.LastThermalTrip = new ThermalGuardTripRecord
        {
            AtUtcMs = 1_000, EndedAtUtcMs = 2_000, PeakC = 99, Reason = ThermalTripReasons.Limit,
        });
        Assert.False(guard.GetState().LastTrip!.Acknowledged);
        Assert.Equal(2_000, guard.GetState().LastTrip!.EndedAtUtcMs);

        var (state, error) = guard.AcknowledgeTrip();

        Assert.Null(error);
        Assert.True(state!.LastTrip!.Acknowledged);
        Assert.Equal(5_000, store.Load().Cooling.LastThermalTrip!.AcknowledgedAtUtcMs);
    }

    [Fact]
    public void AcknowledgingAnActiveTrip_IsRefused_AndNothingChanges()
    {
        var (_, fans, store, _) = Build();
        var live = new ThermalGuardController(fans, store);
        // The record is written after the controller exists, so the restart cleanup has not closed it.
        store.Update(s => s.Cooling.LastThermalTrip = new ThermalGuardTripRecord { AtUtcMs = 1_000, PeakC = 99, Reason = ThermalTripReasons.Limit });

        var (state, error) = live.AcknowledgeTrip();

        Assert.Null(state);
        Assert.Equal("The thermal guard is still active.", error);
        Assert.Null(store.Load().Cooling.LastThermalTrip!.AcknowledgedAtUtcMs);
    }

    [Fact]
    public void AcknowledgingWithNoTrip_IsANoOp()
    {
        var (_, fans, store, _) = Build();
        var guard = new ThermalGuardController(fans, store);
        var changes = 0;
        store.OnChanged += () => changes++;

        var (state, error) = guard.AcknowledgeTrip();

        Assert.Null(error);
        Assert.NotNull(state);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void ANewTrip_ReplacesAnAcknowledgedRecordUnacknowledged()
    {
        var (engine, fans, store, clock) = Build();
        store.Update(s => s.Cooling.LastThermalTrip = new ThermalGuardTripRecord
        {
            AtUtcMs = 1, EndedAtUtcMs = 2, AcknowledgedAtUtcMs = 3, PeakC = 90, Reason = ThermalTripReasons.Limit,
        });

        Trip(engine, fans, clock);

        var trip = store.Load().Cooling.LastThermalTrip!;
        Assert.NotEqual(1, trip.AtUtcMs);
        Assert.Null(trip.AcknowledgedAtUtcMs);
        Assert.Null(trip.EndedAtUtcMs);
    }

    [Fact]
    public void LintWarnings_DefaultOn_ArePartiallyUpdatable_AndDoNotStopTheAutoHeal()
    {
        Assert.True(new CoolingSettings().CurveLintWarnings);
        var missingKey = JsonSerializer.Deserialize("{\"cooling\":{}}", PersistenceJsonContext.Default.NexusSettings)!;
        Assert.True(missingKey.Cooling.CurveLintWarnings);

        var (engine, fans, store, clock) = Build();
        var guard = new ThermalGuardController(fans, store);
        Assert.True(guard.GetState().LintWarnings);

        var (state, error) = guard.SetConfig(new SetThermalGuardConfigBody { LintWarnings = false });
        Assert.Null(error);
        Assert.False(state!.LintWarnings);
        Assert.True(state.Enabled);
        Assert.False(store.Load().Cooling.CurveLintWarnings);

        // The trip-end auto-heal does not consult the setting: the config here has a hazard.
        var e = new CurveEngine(fans, store, new MultiplexHub(), null, guard) { Clock = () => clock[0] };
        TripOnCoolingLoss(e, fans, store, clock);
        fans.CpuTemp = 50f;
        e.Tick();
        CoolDown(e, clock);
        Assert.NotNull(store.Load().Cooling.HealSnapshot);
    }

    [Fact]
    public void KeepingAHeal_ClearsTheUndoSnapshotAndTheHealedList_ButKeepsTheCurves()
    {
        var (_, fans, store, _) = Build();
        var guard = new ThermalGuardController(fans, store);
        var healed = guard.HealNow(automatic: false);
        Assert.True(healed.UndoAvailable);
        Assert.NotEmpty(healed.Channels);
        var curvesAfterHeal = store.Load().Cooling.Curves.Select(c => c.Id).ToArray();

        var kept = guard.Keep();

        Assert.False(kept.UndoAvailable);
        Assert.Empty(kept.Channels);
        Assert.Null(kept.HealedAtUtcMs);
        Assert.Null(store.Load().Cooling.HealSnapshot);
        Assert.Equal(curvesAfterHeal, store.Load().Cooling.Curves.Select(c => c.Id).ToArray());
        // Nothing left to undo.
        Assert.False(guard.Undo().UndoAvailable);
        Assert.Equal(curvesAfterHeal, store.Load().Cooling.Curves.Select(c => c.Id).ToArray());
    }

    [Fact]
    public void KeepingWithNothingToKeep_IsANoOp_ThatReturnsTheCurrentHealState()
    {
        var (_, fans, store, _) = Build();
        var guard = new ThermalGuardController(fans, store);
        var changes = 0;
        store.OnChanged += () => changes++;

        var state = guard.Keep();

        Assert.False(state.UndoAvailable);
        Assert.Empty(state.Channels);
        Assert.Equal(0, changes);
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
    public void Escalated_WhenFansThatReportedRpmStayFarBelowTheirBest_ReassertsFullSpeedPeriodicallyAndReleasesNothing()
    {
        var (engine, fans, _, clock) = Build();
        LearnRpm(engine, fans, clock, 1000);
        Trip(engine, fans, clock);
        var writes = fans.Driven.Count;

        fans.Channels[0].Rpm = 100;
        fans.Channels[1].Rpm = 100;
        clock[0] += 21_000;
        engine.Tick();

        Assert.Equal(0, fans.KeepingPortsCalls);
        Assert.Equal(0, fans.ReleaseAllCalls);
        Assert.Empty(fans.Released);
        Assert.True(fans.Driven.Count > writes);
        Assert.Equal(100, LastDuty(fans, "f1"));
        Assert.Equal(100, LastDuty(fans, "f2"));
        // Re-asserted on a slower cadence than the tick, so per-write USB hubs are not hit every second.
        var again = fans.Driven.Count;
        clock[0] += 1000;
        engine.Tick();
        Assert.Equal(again, fans.Driven.Count);
        clock[0] += 5000;
        engine.Tick();
        Assert.True(fans.Driven.Count > again);
    }

    private static void Import(CurveEngine engine, InMemoryConfigStore store, int duty)
    {
        store.Update(s => s.Cooling.ManualSpeeds["f2"] = duty);
        engine.InvalidateManual(new[] { "f2" });
    }

    [Fact]
    public void ImportDuringEscalation_StaysAtFullSpeed()
    {
        var (engine, fans, store, clock) = Build();
        LearnRpm(engine, fans, clock, 1000);
        Trip(engine, fans, clock);
        fans.Channels[0].Rpm = 100;
        fans.Channels[1].Rpm = 100;
        clock[0] += 21_000;
        engine.Tick();
        Import(engine, store, 65);
        clock[0] += 1000;
        engine.Tick();

        Assert.Equal(100, LastDuty(fans, "f2"));
        Assert.Equal(0, fans.ReleaseAllCalls);
    }

    [Fact]
    public void ManualReplay_NeverWritesAChannelTheGuardHasHandedBack()
    {
        var (engine, fans, store, _) = Build();
        var settings = store.Load();
        var present = new HashSet<string> { "f2" };
        var blocked = new GuardPlan { Output = GuardPlan.None.Output, GpuBlocked = { "f2" } };

        engine.ReplayManualDuties(settings, present, new HashSet<string>(), blocked);
        Assert.Empty(fans.Order);

        engine.ReplayManualDuties(settings, present, new HashSet<string>(), GuardPlan.None);
        Assert.Equal(new[] { ("f2", 30) }, fans.Order);
    }

    [Fact]
    public void ImportDuringATrip_HoldsTheFloor_ThenAppliesTheNewDutyOnceItEnds()
    {
        var (engine, fans, store, clock) = Build();
        Trip(engine, fans, clock);
        var before = fans.Order.Count;

        Import(engine, store, 65);
        clock[0] += 1000;
        engine.Tick();
        Assert.DoesNotContain(fans.Order.Skip(before), w => w.Id == "f2" && w.Duty < 100);

        fans.CpuTemp = 40f;
        CoolDown(engine, clock);
        Assert.Equal(("f2", 65), fans.Order.Last(w => w.Id == "f2"));
    }

    [Fact]
    public void ImportOnAnyTickOfTheCoolDown_NeverLeavesTheFanOnTheOldDuty()
    {
        for (var k = 0; k < 70; k++)
        {
            var (engine, fans, store, clock) = Build();
            Trip(engine, fans, clock);
            fans.CpuTemp = 40f;
            for (var i = 0; i < 70; i++)
            {
                if (i == k)
                {
                    Import(engine, store, 65);
                }
                clock[0] += 1000;
                engine.Tick();
            }
            Assert.True(("f2", 65) == fans.Order.Last(w => w.Id == "f2"), $"import at tick {k}");
        }
    }

    [Fact]
    public void AnImportMidTick_IsNotUndoneByTheGuardRestoreOnTheTripEndTick()
    {
        for (var k = 0; k < 70; k++)
        {
            var (engine, fans, store, clock) = Build();
            Trip(engine, fans, clock);
            fans.CpuTemp = 40f;
            for (var i = 0; i < 70; i++)
            {
                if (i == k)
                {
                    fans.OnSources = () => Import(engine, store, 65);
                }
                clock[0] += 1000;
                engine.Tick();
            }
            Assert.True(("f2", 65) == fans.Order.Last(w => w.Id == "f2"), $"import mid-tick {k}");
        }
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
        var before = fans.Driven.Count;
        engine.Tick();
        Assert.Equal(before, fans.Driven.Count);

        clock[0] += 31_000;
        engine.Tick();
        Assert.True(fans.Driven.Count > before);
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
        TripOnCoolingLoss(engine, fans, store, clock);
        fans.CpuTemp = 50f;
        engine.Tick();
        CoolDown(engine, clock);

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

        Assert.Equal(10, LastDuty(fans, "f1"));
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
    public void Watchdog_DrivesCoolingModeAfterTenSecondsWhenHot_OnlyOnceTheReadingIsStaleWhenCool()
    {
        var (_, fans, store, _) = Build();
        var (e, guard, mono) = WatchdogRig(fans, store);
        fans.CpuTemp = 85f;
        e.Tick();
        guard.TickCompleted();
        var before = fans.Driven.Count;
        guard.WatchdogCheck(mono[0] + 9_000);
        Assert.Equal(before, fans.Driven.Count);
        guard.WatchdogCheck(mono[0] + 11_000);
        Assert.Equal(0, fans.ReleaseAllCalls);
        Assert.Equal(100, LastDuty(fans, "f1"));
        Assert.Equal(100, LastDuty(fans, "f2"));

        var (_, fans2, store2, _) = Build();
        var (e2, guard2, mono2) = WatchdogRig(fans2, store2);
        fans2.CpuTemp = 50f;
        e2.Tick();
        guard2.TickCompleted();
        var before2 = fans2.Driven.Count;
        guard2.WatchdogCheck(mono2[0] + 119_000);
        Assert.Equal(before2, fans2.Driven.Count);
        guard2.WatchdogCheck(mono2[0] + 121_000);
        Assert.Equal(0, fans2.ReleaseAllCalls);
        Assert.Equal(100, LastDuty(fans2, "f1"));
    }

    [Fact]
    public void Watchdog_StillDrivesCoolingMode_WhenTicksKeepCompletingOnReadingsThatStoppedRefreshing()
    {
        var (_, fans, store, _) = Build();
        var (e, guard, mono) = WatchdogRig(fans, store);
        var fresh = true;
        guard.ReadingsFresh = () => fresh;
        fans.CpuTemp = 85f;
        e.Tick();
        guard.TickCompleted();

        // Readers no longer block on a wedged walk, so the engine keeps ticking on cached values.
        fresh = false;
        for (var i = 0; i < 12; i++)
        {
            mono[0] += 1000;
            e.Tick();
            guard.TickCompleted();
        }
        guard.WatchdogCheck(mono[0]);

        Assert.Equal(100, LastDuty(fans, "f1"));
        Assert.Equal(100, LastDuty(fans, "f2"));
    }

    [Fact]
    public void ContinuousHotStall_KeepsFansAtFullForTheWholeStall_ThenCurvesResumeOnFreshReadings()
    {
        var (_, fans, store, _) = Build();
        var (e, guard, mono) = WatchdogRig(fans, store);
        var fresh = true;
        guard.ReadingsFresh = () => fresh;
        fans.CpuTemp = 85f;
        e.Tick();
        guard.TickCompleted();

        fresh = false;
        var drivenAfterFire = -1;
        for (var i = 0; i < 40; i++)
        {
            mono[0] += 1000;
            e.Tick();
            guard.TickCompleted();
            guard.WatchdogCheck(mono[0]);
            if (drivenAfterFire < 0 && LastDuty(fans, "f1") == 100)
            {
                drivenAfterFire = fans.Driven.Count;
            }
            if (drivenAfterFire >= 0)
            {
                Assert.Equal(100, LastDuty(fans, "f1"));
                Assert.Equal(100, LastDuty(fans, "f2"));
            }
        }

        Assert.True(drivenAfterFire >= 0, "the watchdog never drove cooling mode");
        Assert.Equal(drivenAfterFire, fans.Driven.Count);

        fresh = true;
        mono[0] += 1000;
        e.Tick();
        guard.TickCompleted();

        Assert.True(fans.Driven.Count > drivenAfterFire, "curves did not resume after fresh readings");
    }

    [Fact]
    public void Watchdog_StaysQuiet_WhenTicksCompleteOnFreshReadings()
    {
        var (_, fans, store, _) = Build();
        var (e, guard, mono) = WatchdogRig(fans, store);
        guard.ReadingsFresh = () => true;
        fans.CpuTemp = 85f;
        for (var i = 0; i < 12; i++)
        {
            mono[0] += 1000;
            e.Tick();
            guard.TickCompleted();
        }
        var before = fans.Driven.Count;

        guard.WatchdogCheck(mono[0]);

        Assert.Equal(before, fans.Driven.Count);
    }

    [Fact]
    public void StallWhileTheFloorIsDriving_RewritesTheSameDutyOnTheNextTickAfterCoolingMode()
    {
        var (_, fans, store, _) = Build();
        var (e, guard, mono) = WatchdogRig(fans, store);
        fans.CpuTemp = 89f;
        mono[0] += 1000;
        e.Tick();
        guard.TickCompleted();
        Assert.Equal(56, LastDuty(fans, "f1"));
        var before = fans.Driven.Count(d => d.Id == "f1" && d.Duty == 56);

        guard.WatchdogCheck(mono[0] + 11_000);
        Assert.Equal(0, fans.ReleaseAllCalls);
        Assert.Equal(100, LastDuty(fans, "f1"));
        mono[0] += 12_000;
        guard.TickCompleted();
        e.Tick();

        Assert.True(fans.Driven.Count(d => d.Id == "f1" && d.Duty == 56) > before);
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
        fans.CpuTemp = 89f;

        Stall(fans, e, guard, mono);
        Stall(fans, e, guard, mono);
        Assert.False(guard.WatchdogLatched);
        Stall(fans, e, guard, mono);
        Assert.True(guard.WatchdogLatched);
        Assert.Equal(1, alerts.Count(a => a.Contains("Cooling mode held")));

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
        // Cooling mode holds the fans: nothing is ever handed to the BIOS.
        Assert.Equal(releasesAtLatch, fans.ReleaseAllCalls);
        Assert.Empty(fans.Released);
        var state = guard.GetState();
        Assert.True(state.WatchdogLatched);
        Assert.NotNull(state.GuardTempC);
    }

    [Fact]
    public void TogglingTheGuardOffAndOn_ClearsTheWatchdogLatch()
    {
        var (_, fans, store, _) = Build();
        var (e, guard, mono) = WatchdogRig(fans, store);
        fans.CpuTemp = 89f;
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

    private static void SteadyTicks(CurveEngine e, ThermalGuardController guard, long[] mono, long forMs)
    {
        for (var end = mono[0] + forMs; mono[0] < end;)
        {
            mono[0] += 1000;
            e.Tick();
            guard.TickCompleted();
        }
    }

    [Fact]
    public void ALatchedEngineThatTicksSteadilyAgain_TakesTheFansBackAndRedrivesThem()
    {
        var (_, fans, store, _) = Build();
        var (e, guard, mono) = WatchdogRig(fans, store);
        fans.CpuTemp = 89f;
        Stall(fans, e, guard, mono);
        Stall(fans, e, guard, mono);
        Stall(fans, e, guard, mono);
        Assert.True(guard.WatchdogLatched);

        var writes = fans.Driven.Count;
        SteadyTicks(e, guard, mono, ThermalGuardController.WatchdogResumeMs - 2000);
        Assert.True(guard.WatchdogLatched);
        Assert.Equal(writes, fans.Driven.Count);

        SteadyTicks(e, guard, mono, 3000);
        Assert.False(guard.WatchdogLatched);
        e.Tick();
        Assert.True(fans.Driven.Count > writes);
    }

    [Fact]
    public void AGapWhileLatched_RestartsTheSteadyClock()
    {
        var (_, fans, store, _) = Build();
        var (e, guard, mono) = WatchdogRig(fans, store);
        fans.CpuTemp = 89f;
        Stall(fans, e, guard, mono);
        Stall(fans, e, guard, mono);
        Stall(fans, e, guard, mono);

        SteadyTicks(e, guard, mono, ThermalGuardController.WatchdogResumeMs - 60_000);
        mono[0] += 15_000;
        guard.TickCompleted();
        SteadyTicks(e, guard, mono, 120_000);

        Assert.True(guard.WatchdogLatched);
    }

    [Fact]
    public void ASecondLatchInTheSameRun_HoldsUntilAGuardToggle()
    {
        var (_, fans, store, _) = Build();
        var (e, guard, mono) = WatchdogRig(fans, store);
        fans.CpuTemp = 89f;
        Stall(fans, e, guard, mono);
        Stall(fans, e, guard, mono);
        Stall(fans, e, guard, mono);
        Assert.True(guard.GetState().WatchdogResumes);
        SteadyTicks(e, guard, mono, ThermalGuardController.WatchdogResumeMs + 2000);
        Assert.False(guard.WatchdogLatched);

        Stall(fans, e, guard, mono);
        Stall(fans, e, guard, mono);
        Stall(fans, e, guard, mono);
        SteadyTicks(e, guard, mono, 3 * ThermalGuardController.WatchdogResumeMs);

        Assert.True(guard.WatchdogLatched);
        Assert.False(guard.GetState().WatchdogResumes);
        guard.SetEnabled(false);
        guard.SetEnabled(true);
        Assert.False(guard.WatchdogLatched);
        Assert.True(guard.WatchdogResumes);
    }

    [Fact]
    public void ALatchThatRecursSoonAfterResuming_DoesNotAlertAgain()
    {
        var (_, fans, store, _) = Build();
        var (e, guard, mono) = WatchdogRig(fans, store);
        var alerts = new List<string>();
        guard.AlertSink = n => alerts.Add(n.Title);
        fans.CpuTemp = 89f;
        Stall(fans, e, guard, mono);
        Stall(fans, e, guard, mono);
        Stall(fans, e, guard, mono);
        SteadyTicks(e, guard, mono, ThermalGuardController.WatchdogResumeMs + 2000);
        Assert.False(guard.WatchdogLatched);

        Stall(fans, e, guard, mono);
        Stall(fans, e, guard, mono);
        Stall(fans, e, guard, mono);

        Assert.True(guard.WatchdogLatched);
        Assert.Equal(1, alerts.Count(a => a.Contains("Cooling mode held")));
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
        public void ReleaseAll() => _inner.ReleaseAll();
        public void DriveFansToFull(IReadOnlyList<string> channelIds)
        {
            ReleaseStarted.Set();
            AllowRelease.Wait();
            foreach (var id in channelIds)
            {
                _inner.DriveFanSpeed(id, 100);
            }
        }
        public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct)
            => _inner.CalibrateAsync(fanIds, progress, ct);
    }

    [Fact]
    public async Task TheRedriveFlag_IsOnlySetOnceTheWatchdogsCoolingModeWriteHasReturned()
    {
        var (_, fans, store, _) = Build();
        var blocking = new BlockingReleaseFans(fans);
        var mono = new long[] { 1_000 };
        var guard = new ThermalGuardController(blocking, store) { MonotonicMs = () => mono[0] };
        var e = new CurveEngine(blocking, store, new MultiplexHub(), null, guard) { Clock = () => mono[0] };
        fans.CpuTemp = 89f;
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

    [Fact]
    public void Watchdog_FireDrivesEveryNexusDrivenAndHardwareChannelToFull_AndHandsNothingToHardware()
    {
        var (_, fans, store, _) = Build();
        fans.Channels.Add(new FanChannel { Id = "hw1", Name = "Board fan" });
        var (e, guard, mono) = WatchdogRig(fans, store);
        fans.CpuTemp = 85f;
        e.Tick();
        guard.TickCompleted();
        fans.Driven.Clear();

        guard.WatchdogCheck(mono[0] + 11_000);

        foreach (var id in new[] { "f1", "f2", "hw1" })
        {
            Assert.Equal(100, LastDuty(fans, id));
        }
        Assert.Equal(0, fans.ReleaseAllCalls);
        Assert.Empty(fans.Released);
    }

    [Fact]
    public void Watchdog_FireSkipsAChannelTheUserHandedToTheMotherboard()
    {
        var (_, fans, store, _) = Build();
        store.Update(s => s.Cooling.UncontrolledFanChannels.Add("f2"));
        var (e, guard, mono) = WatchdogRig(fans, store);
        fans.CpuTemp = 85f;
        e.Tick();
        guard.TickCompleted();
        fans.Driven.Clear();

        guard.WatchdogCheck(mono[0] + 11_000);

        Assert.Equal(100, LastDuty(fans, "f1"));
        Assert.DoesNotContain(fans.Driven, d => d.Id == "f2");
    }

    [Fact]
    public void Latch_KeepsAssertingCoolingModeForChannelsThatAppearLater_AndNeverWritesACurve()
    {
        var (_, fans, store, _) = Build();
        var (e, guard, mono) = WatchdogRig(fans, store);
        fans.CpuTemp = 89f;
        Stall(fans, e, guard, mono);
        Stall(fans, e, guard, mono);
        Stall(fans, e, guard, mono);
        Assert.True(guard.WatchdogLatched);

        fans.Channels.Add(new FanChannel { Id = "late", Name = "Late fan" });
        store.Update(s => s.Cooling.Curves[0].Flat!.Speed = 33);
        mono[0] += 1000;
        guard.TickCompleted();
        e.Tick();
        fans.Driven.Clear();
        guard.WatchdogCheck(mono[0] + 2_000);

        Assert.Equal(100, LastDuty(fans, "late"));
        Assert.DoesNotContain(fans.Driven, d => d.Duty == 33);
        Assert.Equal(0, fans.ReleaseAllCalls);

        // Already driven channels are not rewritten on every poll.
        fans.Driven.Clear();
        guard.WatchdogCheck(mono[0] + 4_000);
        Assert.Empty(fans.Driven);
    }

    [Fact]
    public void Resume_RestoresTheCurvesWithAFullRewrite_AndHandsHardwareChannelsBackOnce()
    {
        var (_, fans, store, _) = Build();
        fans.Channels.Add(new FanChannel { Id = "hw1", Name = "Board fan" });
        var (e, guard, mono) = WatchdogRig(fans, store);
        fans.CpuTemp = 89f;
        Stall(fans, e, guard, mono);
        Stall(fans, e, guard, mono);
        Stall(fans, e, guard, mono);
        Assert.True(guard.WatchdogLatched);
        e.Tick();
        var releasedWhileLatched = fans.Released.Count(x => x == "hw1");
        SteadyTicks(e, guard, mono, ThermalGuardController.WatchdogResumeMs - 2000);
        // Latched: the hardware channel stays driven.
        Assert.Equal(releasedWhileLatched, fans.Released.Count(x => x == "hw1"));

        SteadyTicks(e, guard, mono, 3000);
        Assert.False(guard.WatchdogLatched);
        fans.Driven.Clear();
        e.Tick();

        // Back to the curve and manual duties, raised only by the guard's floor at this temperature.
        Assert.Equal(56, LastDuty(fans, "f1"));
        Assert.Equal(56, LastDuty(fans, "f2"));
        Assert.Equal(releasedWhileLatched + 1, fans.Released.Count(x => x == "hw1"));
        e.Tick();
        Assert.Equal(releasedWhileLatched + 1, fans.Released.Count(x => x == "hw1"));
        Assert.Equal(0, fans.ReleaseAllCalls);
    }

    [Fact]
    public void GuardEscalation_DrivesHardwareControlChannelsToFullToo_AndNeverReleasesThem()
    {
        var (engine, fans, _, clock) = Build();
        fans.Channels.Add(new FanChannel { Id = "hw1", Name = "Board fan" });
        LearnRpm(engine, fans, clock, 1000);
        Trip(engine, fans, clock);
        fans.Channels[0].Rpm = 100;
        fans.Channels[1].Rpm = 100;
        clock[0] += 21_000;
        engine.Tick();
        clock[0] += 1000;
        engine.Tick();

        Assert.Equal(100, LastDuty(fans, "hw1"));
        Assert.Empty(fans.Released);
        Assert.Equal(0, fans.ReleaseAllCalls);
    }

    [Fact]
    public void TogglingTheGuardOffDuringALatch_HandsHardwareChannelsBackExactlyOnce()
    {
        var (_, fans, store, _) = Build();
        fans.Channels.Add(new FanChannel { Id = "hw1", Name = "Board fan" });
        var (e, guard, mono) = WatchdogRig(fans, store);
        fans.CpuTemp = 89f;
        Stall(fans, e, guard, mono);
        Stall(fans, e, guard, mono);
        Stall(fans, e, guard, mono);
        Assert.True(guard.WatchdogLatched);
        e.Tick();
        var before = fans.Released.Count(x => x == "hw1");

        guard.SetEnabled(false);
        for (var i = 0; i < 3; i++)
        {
            mono[0] += 1000;
            guard.TickCompleted();
            e.Tick();
        }

        Assert.Equal(before + 1, fans.Released.Count(x => x == "hw1"));
    }

    [Fact]
    public void CoolingSwitchedOff_GetsNoCoolingModeWrites_EvenFromADirectDrive()
    {
        var (_, fans, store, _) = Build();
        var (e, guard, mono) = WatchdogRig(fans, store);
        fans.CpuTemp = 85f;
        e.Tick();
        guard.TickCompleted();
        store.Update(s => s.Features.Cooling = false);
        fans.Driven.Clear();

        guard.DriveCoolingMode(requireLatch: false);

        Assert.Empty(fans.Driven);
    }

    [Fact]
    public void ACoolingOffThenOnWithAPendingWatchdogRelease_HandsHardwareChannelsBackOnce()
    {
        var (_, fans, store, _) = Build();
        fans.Channels.Add(new FanChannel { Id = "hw1", Name = "Board fan" });
        var mono = new long[] { 1_000 };
        var guard = new ThermalGuardController(fans, store) { MonotonicMs = () => mono[0] };
        var e = new CurveEngine(fans, store, new MultiplexHub(), new FeatureGates(store), guard) { Clock = () => mono[0] };
        store.Update(s => s.FeaturesOnboardingCompleted = true);
        fans.CpuTemp = 85f;
        e.Tick();
        guard.TickCompleted();
        guard.WatchdogCheck(mono[0] + 11_000);
        store.Update(s => s.Features.Cooling = false);
        mono[0] += 1000;
        e.Tick();
        Assert.DoesNotContain("hw1", fans.Released);

        store.Update(s => s.Features.Cooling = true);
        mono[0] += 1000;
        e.Tick();
        mono[0] += 1000;
        e.Tick();

        Assert.Single(fans.Released, x => x == "hw1");
    }

    [Fact]
    public void ThePollAfterTheLatchEnded_DrivesNothing()
    {
        var (_, fans, store, _) = Build();
        var (e, guard, mono) = WatchdogRig(fans, store);
        fans.CpuTemp = 89f;
        Stall(fans, e, guard, mono);
        Stall(fans, e, guard, mono);
        Stall(fans, e, guard, mono);
        SteadyTicks(e, guard, mono, ThermalGuardController.WatchdogResumeMs + 2000);
        Assert.False(guard.WatchdogLatched);
        fans.Driven.Clear();

        guard.DriveCoolingMode(requireLatch: true);

        Assert.Empty(fans.Driven);
        Assert.Empty(guard.TakeWatchdogHardware());
    }

    [Fact]
    public void AChannelThatDropsAndReturnsDuringALatch_IsDrivenAgain()
    {
        var (_, fans, store, _) = Build();
        var (e, guard, mono) = WatchdogRig(fans, store);
        fans.CpuTemp = 89f;
        Stall(fans, e, guard, mono);
        Stall(fans, e, guard, mono);
        Stall(fans, e, guard, mono);
        var f2 = fans.Channels.Single(c => c.Id == "f2");

        fans.Channels.Remove(f2);
        mono[0] += 1000;
        guard.TickCompleted();
        e.Tick();
        fans.Channels.Add(f2);
        mono[0] += 1000;
        guard.TickCompleted();
        e.Tick();
        fans.Driven.Clear();
        guard.WatchdogCheck(mono[0] + 2_000);

        Assert.Equal(100, LastDuty(fans, "f2"));
    }

    [Fact]
    public void AWatchdogFireWhileTheGuardIsTripped_DoesNotReleaseTheChannelsTheGuardIsDriving()
    {
        var (_, fans, store, _) = Build();
        fans.Channels.Add(new FanChannel { Id = "hw1", Name = "Board fan" });
        var (e, guard, mono) = WatchdogRig(fans, store);
        Trip(e, fans, mono);
        Assert.Equal(100, LastDuty(fans, "hw1"));

        guard.WatchdogCheck(mono[0]);
        mono[0] += 1000;
        guard.TickCompleted();
        e.Tick();

        Assert.DoesNotContain("hw1", fans.Released);
        Assert.Equal(100, LastDuty(fans, "hw1"));
    }

    [Fact]
    public void RestoreFromALatch_ClearsItLikeAToggle_RewritesEveryDutyAndHandsHardwareBackOnce()
    {
        var (_, fans, store, _) = Build();
        fans.Channels.Add(new FanChannel { Id = "hw1", Name = "Board fan" });
        var (e, guard, mono) = WatchdogRig(fans, store);
        fans.CpuTemp = 89f;
        Stall(fans, e, guard, mono);
        Stall(fans, e, guard, mono);
        Stall(fans, e, guard, mono);
        Assert.True(guard.WatchdogLatched);
        Assert.True(guard.GetState().RestoreAvailable);
        e.Tick();
        var before = fans.Released.Count(x => x == "hw1");
        var curves = store.Load().Cooling.Curves[0].Flat!.Speed;
        var manual = store.Load().Cooling.ManualSpeeds["f2"];

        var (result, error) = guard.RestoreCurves();
        fans.Driven.Clear();
        mono[0] += 1000;
        guard.TickCompleted();
        e.Tick();
        e.Tick();

        Assert.Null(error);
        Assert.False(result!.RestoreAvailable);
        Assert.False(guard.WatchdogLatched);
        Assert.Equal(56, LastDuty(fans, "f1"));
        Assert.Equal(56, LastDuty(fans, "f2"));
        Assert.Equal(before + 1, fans.Released.Count(x => x == "hw1"));
        Assert.Equal(curves, store.Load().Cooling.Curves[0].Flat!.Speed);
        Assert.Equal(manual, store.Load().Cooling.ManualSpeeds["f2"]);

        // Detection stays armed: a new stall fires again.
        fans.Driven.Clear();
        guard.WatchdogCheck(mono[0] + 31_000);
        Assert.Equal(100, LastDuty(fans, "f1"));
    }

    [Fact]
    public void RestoreFromATrip_StopsTheOverride_HandsHardwareBackOnce_AndDoesNotRetripAtOnce()
    {
        var (_, fans, store, _) = Build();
        fans.Channels.Add(new FanChannel { Id = "hw1", Name = "Board fan" });
        var (e, guard, mono) = WatchdogRig(fans, store);
        Trip(e, fans, mono);
        Assert.Equal(ThermalGuardStates.Tripped, guard.GetState().State);
        Assert.True(guard.GetState().RestoreAvailable);
        var released = fans.Released.Count(x => x == "hw1");

        var (_, error) = guard.RestoreCurves();
        Assert.Null(error);
        for (var i = 0; i < 40; i++)
        {
            mono[0] += 1000;
            guard.TickCompleted();
            e.Tick();
        }

        var state = guard.GetState();
        Assert.NotEqual(ThermalGuardStates.Tripped, state.State);
        Assert.False(state.RestoreAvailable);
        Assert.Equal(released + 1, fans.Released.Count(x => x == "hw1"));

        // Cooled below the band, then hot again: re-armed.
        fans.CpuTemp = 40f;
        for (var i = 0; i < 5; i++)
        {
            mono[0] += 1000;
            guard.TickCompleted();
            e.Tick();
        }
        fans.CpuTemp = 99f;
        for (var i = 0; i < 20; i++)
        {
            mono[0] += 1000;
            guard.TickCompleted();
            e.Tick();
        }
        Assert.Equal(ThermalGuardStates.Tripped, guard.GetState().State);
    }

    [Fact]
    public void RestoreFromAnEscalation_StopsTheReassertion_AndReleasesNothing()
    {
        var (_, fans, store, _) = Build();
        var (engine, guard, clock) = WatchdogRig(fans, store);
        LearnRpm(engine, fans, clock, 1000);
        Trip(engine, fans, clock);
        fans.Channels[0].Rpm = 100;
        fans.Channels[1].Rpm = 100;
        clock[0] += 21_000;
        engine.Tick();
        Assert.Equal(ThermalGuardStates.Escalated, guard.GetState().State);

        var (_, error) = guard.RestoreCurves();
        Assert.Null(error);
        // Still hot, inside the band below the limit: the override stays off.
        fans.CpuTemp = 88f;
        for (var i = 0; i < 60; i++)
        {
            clock[0] += 1000;
            engine.Tick();
        }
        Assert.NotEqual(ThermalGuardStates.Escalated, guard.GetState().State);
        Assert.NotEqual(ThermalGuardStates.Tripped, guard.GetState().State);
        Assert.Equal(0, fans.ReleaseAllCalls);

        // At the limit the trip would normally fire again; suppression holds it back.
        fans.CpuTemp = 96f;
        for (var i = 0; i < 30; i++)
        {
            clock[0] += 1000;
            engine.Tick();
        }
        Assert.NotEqual(ThermalGuardStates.Tripped, guard.GetState().State);

        // Cooled below the band: re-armed.
        fans.CpuTemp = 40f;
        for (var i = 0; i < 5; i++)
        {
            clock[0] += 1000;
            engine.Tick();
        }
        fans.CpuTemp = 99f;
        for (var i = 0; i < 20; i++)
        {
            clock[0] += 1000;
            engine.Tick();
        }
        Assert.Equal(ThermalGuardStates.Tripped, guard.GetState().State);
    }

    [Fact]
    public void RestoreWithNothingActive_ReportsAnErrorAndChangesNothing()
    {
        var (_, fans, store, _) = Build();
        var guard = new ThermalGuardController(fans, store);

        var (result, error) = guard.RestoreCurves();

        Assert.Null(result);
        Assert.False(string.IsNullOrEmpty(error));
        Assert.False(guard.GetState().RestoreAvailable);
    }

    [Fact]
    public void ALateCoolingModeWriteAfterARestore_IsFollowedByAFullRewrite()
    {
        var (_, fans, store, _) = Build();
        var (e, guard, mono) = WatchdogRig(fans, store);
        fans.CpuTemp = 89f;
        Stall(fans, e, guard, mono);
        Stall(fans, e, guard, mono);
        Stall(fans, e, guard, mono);
        guard.RestoreCurves();
        mono[0] += 1000;
        guard.TickCompleted();
        e.Tick();
        Assert.Equal(56, LastDuty(fans, "f1"));

        // A poll that chose its ids before the restore lands its write after the engine's rewrite.
        guard.DriveCoolingMode(requireLatch: false);
        Assert.Equal(100, LastDuty(fans, "f1"));
        mono[0] += 1000;
        guard.TickCompleted();
        e.Tick();

        Assert.Equal(56, LastDuty(fans, "f1"));
    }

    [Fact]
    public void TheWatchdogOutcome_IsConsumedAtomically_AndOnlyOnce()
    {
        var (_, fans, store, _) = Build();
        fans.Channels.Add(new FanChannel { Id = "hw1", Name = "Board fan" });
        var (e, guard, mono) = WatchdogRig(fans, store);
        fans.CpuTemp = 85f;
        e.Tick();
        guard.TickCompleted();
        guard.WatchdogCheck(mono[0] + 11_000);

        var first = guard.ConsumeWatchdogOutcome();
        var second = guard.ConsumeWatchdogOutcome();

        Assert.True(first.Rewrite);
        Assert.Equal(new[] { "hw1" }, first.WatchdogHardware);
        Assert.False(second.Rewrite);
        Assert.Empty(second.WatchdogHardware);
        Assert.False(second.RestoredTrip);
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

        // Against the raised limit the same temperature gives a smaller floor than the default limit would.
        // The floor reads a smoothed temperature, so let a few seconds of readings settle.
        fans.CpuTemp = 95f;
        for (var i = 0; i < 4; i++)
        {
            clock[0] += 1000;
            e.Tick();
        }
        Assert.Equal(40, LastDuty(fans, "f1"));

        // Past the default limit but under the override: no trip however long.
        fans.CpuTemp = 99.9f;
        for (var i = 0; i < 8; i++)
        {
            clock[0] += 1000;
            e.Tick();
        }
        Assert.Null(store.Load().Cooling.LastThermalTrip);

        // At the overridden limit the trip needs the full sustain.
        fans.CpuTemp = 100f;
        clock[0] += 1000;
        e.Tick();
        clock[0] += 4000;
        e.Tick();
        Assert.Null(store.Load().Cooling.LastThermalTrip);
        clock[0] += 1000;
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

        store.Update(s => s.Cooling.Curves.Add(Flat("f1", 10)));
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
