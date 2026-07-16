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

namespace Nexus.Service.Tests;

/// <summary>
/// Tick-level tests for the presence-gated write dedup and the persisted
/// manual-duty replay: channels that appear after boot (late LHM discovery,
/// hub USB connect) must be driven / restored on their first present tick,
/// and a reconnect (absent then present) must re-drive even at an unchanged
/// duty because the hub lost its duty state.
/// </summary>
public class CurveEngineTests
{
    private sealed class FakeFanProvider : IFanControlProvider
    {
        public readonly HashSet<string> Present = new();
        public readonly List<(string Id, int Duty)> Driven = new();
        public readonly List<(string Id, int Duty)> ManualSet = new();
        public float Temperature = 50f;

        public IReadOnlyList<FanChannel> GetFanChannels() =>
            Present.Select(id => new FanChannel { Id = id, Name = id }).ToList();

        public IReadOnlyList<TemperatureSource> GetTemperatureSources() =>
            Array.Empty<TemperatureSource>();

        public float? ReadTemperature(string sensorId) => Temperature;

        public int SetFanSpeed(string channelId, int dutyPercent)
        {
            ManualSet.Add((channelId, dutyPercent));
            return dutyPercent;
        }

        public void DriveFanSpeed(string channelId, int dutyPercent) =>
            Driven.Add((channelId, dutyPercent));

        public void ReleaseFan(string channelId) { }
        public void ReleaseAll() { }

        public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }

    private static CurveDocument FlatCurve(string outputId, int speed) => new()
    {
        Id = "c-" + outputId,
        Name = "c-" + outputId,
        Type = "Flat",
        Input = new CurveInputDocument { Id = "t", Type = "Temperature" },
        Outputs = { new CurveOutputDocument { Id = outputId, Type = "Fan" } },
        Flat = new FlatCurveData { Speed = speed },
    };

    private static (CurveEngine Engine, FakeFanProvider Fans, InMemoryConfigStore Store) Build()
    {
        var fans = new FakeFanProvider();
        var store = new InMemoryConfigStore();
        var engine = new CurveEngine(fans, store, new MultiplexHub());
        return (engine, fans, store);
    }

    [Fact]
    public void CurveOutput_AbsentChannel_IsDrivenTheTickItAppears()
    {
        var (engine, fans, store) = Build();
        store.Update(s => s.Cooling.Curves.Add(FlatCurve("x", 40)));

        engine.Tick();
        Assert.Empty(fans.Driven);

        fans.Present.Add("x");
        engine.Tick();
        Assert.Equal(new[] { ("x", 40) }, fans.Driven);

        // Unchanged duty: the dedup suppresses further writes.
        engine.Tick();
        Assert.Single(fans.Driven);
    }

    [Fact]
    public void CurveOutput_Reconnect_RedrivesAtUnchangedDuty()
    {
        var (engine, fans, store) = Build();
        store.Update(s => s.Cooling.Curves.Add(FlatCurve("x", 40)));

        fans.Present.Add("x");
        engine.Tick();
        Assert.Single(fans.Driven);

        // Hub drops: the dedup record must go with it.
        fans.Present.Remove("x");
        engine.Tick();
        Assert.Single(fans.Driven);

        // Hub returns having lost its duty state: same duty must be re-sent.
        fans.Present.Add("x");
        engine.Tick();
        Assert.Equal(2, fans.Driven.Count);
        Assert.Equal(("x", 40), fans.Driven[1]);
    }

    [Fact]
    public void ManualReplay_AppliesSavedDutyWhenChannelAppears()
    {
        var (engine, fans, store) = Build();
        store.Update(s => s.Cooling.ManualSpeeds["x"] = 60);

        // No curves at all: the replay must still run.
        engine.Tick();
        Assert.Empty(fans.ManualSet);

        fans.Present.Add("x");
        engine.Tick();
        Assert.Equal(new[] { ("x", 60) }, fans.ManualSet);

        engine.Tick();
        Assert.Single(fans.ManualSet);
    }

    [Fact]
    public void ManualReplay_SkipsCurveOwnedChannels()
    {
        var (engine, fans, store) = Build();
        store.Update(s =>
        {
            s.Cooling.Curves.Add(FlatCurve("x", 40));
            // Stale leftover from before the fan was attached to the curve.
            s.Cooling.ManualSpeeds["x"] = 60;
        });

        fans.Present.Add("x");
        engine.Tick();

        Assert.Empty(fans.ManualSet);
        Assert.Equal(new[] { ("x", 40) }, fans.Driven);
    }

    [Fact]
    public void ManualReplay_RefiresAfterCurveReleasesChannel()
    {
        // A preset apply attaches the fan to a curve, then leaving the preset
        // for Custom detaches it. Curve ownership must re-arm the replay latch
        // so the saved manual duty comes back when the curve lets go.
        var (engine, fans, store) = Build();
        store.Update(s => s.Cooling.ManualSpeeds["x"] = 60);
        fans.Present.Add("x");
        engine.Tick();
        Assert.Equal(new[] { ("x", 60) }, fans.ManualSet);

        store.Update(s => s.Cooling.Curves.Add(FlatCurve("x", 40)));
        engine.Tick();
        Assert.Single(fans.ManualSet);
        Assert.Equal(new[] { ("x", 40) }, fans.Driven);

        store.Update(s => s.Cooling.Curves.First(c => c.Id == "c-x").Outputs.Clear());
        engine.Tick();
        Assert.Equal(2, fans.ManualSet.Count);
        Assert.Equal(("x", 60), fans.ManualSet[1]);
    }

    [Fact]
    public void ManualReplay_ReconnectReplays()
    {
        var (engine, fans, store) = Build();
        store.Update(s => s.Cooling.ManualSpeeds["x"] = 60);

        fans.Present.Add("x");
        engine.Tick();
        Assert.Single(fans.ManualSet);

        fans.Present.Remove("x");
        engine.Tick();
        fans.Present.Add("x");
        engine.Tick();

        Assert.Equal(2, fans.ManualSet.Count);
    }

    [Fact]
    public void SetCurves_PrunesManualEntryForUserCurveFan_ButNotPresetFan()
    {
        // Attaching a fan to a user curve supersedes its manual override;
        // without the prune, the replay resurrects the stale duty after the
        // fan is later detached back to BIOS via the same curves/set path.
        // Preset attachments ride along in every whole-list save and must
        // NOT prune - FanProfiles.Apply preserves those entries so a fan
        // re-manualizes after leaving the preset.
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Cooling.ManualSpeeds["x"] = 60;
            s.Cooling.ManualSpeeds["p"] = 55;
        });
        var provider = new StubCoolingProvider(store);

        provider.SetCurves(new SetCurvesBody
        {
            GlobalSpeedModifier = 1.0,
            Curves =
            {
                new Curve
                {
                    Id = "c1",
                    Name = "c1",
                    Type = "Flat",
                    Input = new CurveInput { Id = "t", Type = "Temperature" },
                    Outputs = { new CurveOutput { Id = "x", Type = "Fan" } },
                    Flat = new FlatCurve { Speed = 40 },
                },
                new Curve
                {
                    Id = "preset-silent",
                    Name = "Silent",
                    Type = "Flat",
                    Preset = "silent",
                    Input = new CurveInput { Id = "t", Type = "Temperature" },
                    Outputs = { new CurveOutput { Id = "p", Type = "Fan" } },
                    Flat = new FlatCurve { Speed = 20 },
                },
            },
        });

        var manual = store.Load().Cooling.ManualSpeeds;
        Assert.False(manual.ContainsKey("x"));
        Assert.Equal(55, manual["p"]);
    }

    [Fact]
    public void CurveOutput_DetachedThenReattached_IsRedrivenAtUnchangedDuty()
    {
        // A dedup record must not outlive the curve attachment: the hub can
        // reset (losing duty state) while no curve references the fan, and
        // the stale record would then suppress the re-attach write.
        var (engine, fans, store) = Build();
        store.Update(s => s.Cooling.Curves.Add(FlatCurve("x", 40)));
        fans.Present.Add("x");
        engine.Tick();
        Assert.Single(fans.Driven);

        // Detach: replace the curve set with one that does not reference x.
        store.Update(s =>
        {
            s.Cooling.Curves.Clear();
            s.Cooling.Curves.Add(FlatCurve("y", 40));
        });
        engine.Tick();

        store.Update(s => s.Cooling.Curves.Add(FlatCurve("x", 40)));
        engine.Tick();
        Assert.Equal(2, fans.Driven.Count(w => w.Id == "x"));
    }

    [Fact]
    public void CurveOutput_AllCurvesDeleted_DedupClearsForReattach()
    {
        // Zero-curves path: every dedup record is orphaned and must clear,
        // including through the idle early-out (no curves, no manual).
        var (engine, fans, store) = Build();
        store.Update(s => s.Cooling.Curves.Add(FlatCurve("x", 40)));
        fans.Present.Add("x");
        engine.Tick();
        Assert.Single(fans.Driven);

        store.Update(s => s.Cooling.Curves.Clear());
        engine.Tick();

        store.Update(s => s.Cooling.Curves.Add(FlatCurve("x", 40)));
        engine.Tick();
        Assert.Equal(2, fans.Driven.Count);
        Assert.Equal(("x", 40), fans.Driven[1]);
    }

    [Fact]
    public void ResetSmoothing_RearmsManualReplay()
    {
        // Profile switch calls ResetSmoothing after ReleaseAll; the incoming
        // profile's saved duties must be replayed on the next tick.
        var (engine, fans, store) = Build();
        store.Update(s => s.Cooling.ManualSpeeds["x"] = 60);

        fans.Present.Add("x");
        engine.Tick();
        Assert.Single(fans.ManualSet);

        engine.ResetSmoothing();
        engine.Tick();
        Assert.Equal(2, fans.ManualSet.Count);
    }
}
