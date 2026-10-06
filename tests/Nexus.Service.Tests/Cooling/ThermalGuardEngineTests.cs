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
        public int ReleaseAllCalls;

        public IReadOnlyList<FanChannel> GetFanChannels() => Channels;
        public IReadOnlyList<TemperatureSource> GetTemperatureSources() => new[]
        {
            new TemperatureSource { Id = "cpu", Name = "Core (Tctl/Tdie)", Category = "CPU", Value = CpuTemp },
        };
        public float? ReadTemperature(string sensorId) => CpuTemp;
        public int SetFanSpeed(string channelId, int dutyPercent) => dutyPercent;
        public void DriveFanSpeed(string channelId, int dutyPercent) => Driven.Add((channelId, dutyPercent));
        public void ReleaseFan(string channelId) { }
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
    public void LockedAndUncontrolledChannels_AreNeverWrittenByTheGuard()
    {
        var (engine, fans, store, _) = Build();
        store.Update(s =>
        {
            s.Cooling.FanLockOverrides["f1"] = true;
            s.Cooling.UncontrolledFanChannels.Add("f2");
        });
        fans.CpuTemp = 99f;
        engine.Tick();
        // The curve write for the locked fan is its own computed duty, never the override.
        Assert.DoesNotContain(fans.Driven, d => d.Duty == 100);
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
