using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.Cooling;

// A temperature-driven curve whose source reads nothing drives its fans at the fixed safe
// duty and returns to the curve on its own when the reading comes back.
public class CurveMissingSourceTests
{
    private sealed class Fans : IFanControlProvider
    {
        public readonly HashSet<string> Present = new();
        public readonly List<(string Id, int Duty)> Driven = new();
        public readonly Dictionary<string, int> Duty = new();
        public readonly Dictionary<string, float> Temps = new();

        public IReadOnlyList<FanChannel> GetFanChannels() =>
            Present.Select(id => new FanChannel { Id = id, Name = id, DutyPercent = Duty.GetValueOrDefault(id) }).ToList();

        public IReadOnlyList<TemperatureSource> GetTemperatureSources() => Array.Empty<TemperatureSource>();
        public float? ReadTemperature(string sensorId) => Temps.TryGetValue(sensorId, out var t) ? t : null;

        public int SetFanSpeed(string channelId, int dutyPercent)
        {
            Duty[channelId] = dutyPercent;
            return dutyPercent;
        }

        public void DriveFanSpeed(string channelId, int dutyPercent)
        {
            Driven.Add((channelId, dutyPercent));
            Duty[channelId] = dutyPercent;
        }

        public void ReleaseFan(string channelId) { }
        public void ReleaseAll() { }

        public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }

    private static (CurveEngine Engine, Fans Fans, InMemoryConfigStore Store) Build()
    {
        var fans = new Fans();
        var store = new InMemoryConfigStore();
        store.Update(s => s.Cooling.ThermalGuardEnabled = false);
        return (new CurveEngine(fans, store, new MultiplexHub()), fans, store);
    }

    // Per-channel writes are rate limited, so each tick that must write sits well past that window.
    private static void NextTick(CurveEngine engine, ref long now)
    {
        now += 60_000;
        var at = now;
        engine.Clock = () => at;
        engine.Tick();
    }

    // 20 C -> 20%, 60 C -> 60%, no smoothing, so a reading of 40 C is 40%.
    private static CurveDocument Linear(string id, string sensor, string output) => new()
    {
        Id = id,
        Name = id,
        Type = "Linear",
        Input = new CurveInputDocument { Id = sensor, Type = "Temperature" },
        Outputs = { new CurveOutputDocument { Id = output, Type = "Fan" } },
        Linear = new LinearCurveData { ResponseTime = 0, MinTemp = 20, MaxTemp = 60, MinSpeed = 20, MaxSpeed = 60 },
    };

    private static int LastDuty(Fans fans, string id) => fans.Driven.Last(d => d.Id == id).Duty;

    [Fact]
    public void A_curve_whose_source_reads_nothing_drives_its_fans_at_the_safe_duty()
    {
        var (engine, fans, store) = Build();
        store.Update(s => s.Cooling.Curves.Add(Linear("c", "corsair:ch1:temp", "fan")));
        fans.Present.Add("fan");

        engine.Tick();

        Assert.Equal(70, LastDuty(fans, "fan"));
    }

    [Fact]
    public void The_safe_duty_holds_while_the_source_stays_missing_and_is_not_rewritten()
    {
        var (engine, fans, store) = Build();
        store.Update(s => s.Cooling.Curves.Add(Linear("c", "corsair:ch1:temp", "fan")));
        fans.Present.Add("fan");

        engine.Tick();
        engine.Tick();
        engine.Tick();

        Assert.All(fans.Driven.Where(d => d.Id == "fan"), d => Assert.Equal(70, d.Duty));
    }

    [Fact]
    public void The_curve_resumes_when_the_reading_returns_and_falls_back_again_when_it_goes()
    {
        var (engine, fans, store) = Build();
        store.Update(s => s.Cooling.Curves.Add(Linear("c", "corsair:ch1:temp", "fan")));
        fans.Present.Add("fan");

        long now = 0;
        NextTick(engine, ref now);
        Assert.Equal(70, LastDuty(fans, "fan"));

        fans.Temps["corsair:ch1:temp"] = 40f;
        NextTick(engine, ref now);
        Assert.Equal(40, LastDuty(fans, "fan"));

        fans.Temps.Clear();
        NextTick(engine, ref now);
        Assert.Equal(70, LastDuty(fans, "fan"));
    }

    [Fact]
    public void A_mix_falls_back_only_when_every_member_did()
    {
        var (engine, fans, store) = Build();
        var mix = new CurveDocument
        {
            Id = "mix",
            Name = "mix",
            Type = "Mixed",
            Input = new CurveInputDocument(),
            Outputs = { new CurveOutputDocument { Id = "out", Type = "Fan" } },
            Mixed = new MixedCurveData { CurveIds = { "a", "b" }, Fn = "max" },
        };
        store.Update(s =>
        {
            s.Cooling.Curves.Add(Linear("a", "s1", "fan-a"));
            s.Cooling.Curves.Add(Linear("b", "s2", "fan-b"));
            s.Cooling.Curves.Add(mix);
        });
        fans.Present.UnionWith(new[] { "fan-a", "fan-b", "out" });

        long now = 0;
        NextTick(engine, ref now);
        Assert.Equal(70, LastDuty(fans, "out"));

        // One member reads again: the mix follows it alone and the missing member is skipped.
        fans.Temps["s1"] = 30f;
        NextTick(engine, ref now);
        Assert.Equal(30, LastDuty(fans, "out"));
    }

    [Fact]
    public void A_mix_with_no_members_still_drives_nothing()
    {
        var (engine, fans, store) = Build();
        var mix = new CurveDocument
        {
            Id = "mix",
            Name = "mix",
            Type = "Mixed",
            Input = new CurveInputDocument(),
            Outputs = { new CurveOutputDocument { Id = "out", Type = "Fan" } },
            Mixed = new MixedCurveData { CurveIds = { "gone" }, Fn = "max" },
        };
        store.Update(s => s.Cooling.Curves.Add(mix));
        fans.Present.Add("out");

        engine.Tick();

        Assert.DoesNotContain(fans.Driven, d => d.Id == "out");
    }
}
