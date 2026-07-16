using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Covers the NVML-backed NVIDIA provider's channel building, per-fan control
/// routing, and temperature sources - using injected GPU snapshots, no real
/// NVML / GPU. (The libnvidia-ml interop itself is verified live on hardware.)
/// </summary>
public class LinuxNvidiaFanProviderTests
{
    private static List<GpuInfo> DualFan() => new()
    {
        new GpuInfo(0, "NVIDIA GeForce RTX 5080", 42f,
            new List<GpuFan> { new(0, 30, 1000), new(1, 35, 1200) }),
    };

    [Fact]
    public void GetFanChannels_DualFanGpu_YieldsTwoNumberedChannels()
    {
        var p = new LinuxNvidiaFanProvider(DualFan, (_, _, _) => true);
        var chans = p.GetFanChannels();
        Assert.Equal(2, chans.Count);
        Assert.Equal("nvidia:0:0", chans[0].Id);
        Assert.Equal("NVIDIA GeForce RTX 5080 fan 1", chans[0].Name);
        Assert.Equal(30, chans[0].DutyPercent);
        Assert.Equal(1000, chans[0].Rpm); // tach RPM, not duty
        Assert.Equal("nvidia:0:1", chans[1].Id);
        Assert.Equal("NVIDIA GeForce RTX 5080 fan 2", chans[1].Name);
        Assert.Equal(35, chans[1].DutyPercent);
        Assert.Equal(1200, chans[1].Rpm);
        // both group under the one GPU device
        Assert.Equal("nvidia:0", chans[0].DeviceId);
        Assert.Equal("nvidia:0", chans[1].DeviceId);
    }

    [Fact]
    public void GetFanChannels_SingleFan_OmitsTheNumberSuffix()
    {
        var single = new List<GpuInfo> { new(0, "RTX 4060", 50f, new List<GpuFan> { new(0, 20, 800) }) };
        var ch = Assert.Single(new LinuxNvidiaFanProvider(() => single, (_, _, _) => true).GetFanChannels());
        Assert.Equal("nvidia:0:0", ch.Id);
        Assert.Equal("RTX 4060 fan", ch.Name);
    }

    [Fact]
    public void SetFanSpeed_RoutesToTheRightGpuAndFan()
    {
        var calls = new List<(int gpu, int fan, int? duty)>();
        var p = new LinuxNvidiaFanProvider(DualFan, (g, f, d) => { calls.Add((g, f, d)); return true; });
        p.SetFanSpeed("nvidia:0:1", 80);
        Assert.Equal((0, 1, 80), Assert.Single(calls));
    }

    [Fact]
    public void ReleaseFan_PassesNullDutyForAuto()
    {
        var calls = new List<(int gpu, int fan, int? duty)>();
        var p = new LinuxNvidiaFanProvider(DualFan, (g, f, d) => { calls.Add((g, f, d)); return true; });
        p.ReleaseFan("nvidia:0:0");
        Assert.Equal((0, 0, (int?)null), Assert.Single(calls));
    }

    [Fact]
    public void Control_IgnoresTemperatureIds()
    {
        var calls = new List<(int, int, int?)>();
        var p = new LinuxNvidiaFanProvider(DualFan, (g, f, d) => { calls.Add((g, f, d)); return true; });
        p.SetFanSpeed("nvidia:temp:0", 50); // not a fan id - must not drive anything
        Assert.Empty(calls);
    }

    [Fact]
    public void TemperatureSource_HasGpuCategoryAndRoundTrips()
    {
        var p = new LinuxNvidiaFanProvider(DualFan, (_, _, _) => true);
        var src = Assert.Single(p.GetTemperatureSources());
        Assert.Equal("nvidia:temp:0", src.Id);
        Assert.Equal("GPU", src.Category);
        Assert.Equal(42f, src.Value);
        Assert.Equal(42f, p.ReadTemperature("nvidia:temp:0"));
        Assert.Null(p.ReadTemperature("nvidia:temp:9"));
    }

    [Fact]
    public void NoGpus_YieldsNothing()
    {
        var p = new LinuxNvidiaFanProvider(() => new List<GpuInfo>(), (_, _, _) => true);
        Assert.Empty(p.GetFanChannels());
        Assert.Empty(p.GetTemperatureSources());
    }

    [Fact]
    public void Mode_TracksManualOnSuccessfulWrite_AndAutoOnRelease()
    {
        var p = new LinuxNvidiaFanProvider(DualFan, (_, _, _) => true);
        string Mode() => p.GetFanChannels().Single(c => c.Id == "nvidia:0:0").Mode;
        Assert.Equal("Auto", Mode());
        p.SetFanSpeed("nvidia:0:0", 60);
        Assert.Equal("Manual", Mode());   // sticks instead of snapping back to Auto/Bios
        p.ReleaseFan("nvidia:0:0");
        Assert.Equal("Auto", Mode());
    }

    [Fact]
    public void Mode_StaysAutoWhenWriteFails()
    {
        // No root -> NVML rejects -> control returns false -> truthfully Auto.
        var p = new LinuxNvidiaFanProvider(DualFan, (_, _, _) => false);
        p.SetFanSpeed("nvidia:0:0", 60);
        Assert.Equal("Auto", p.GetFanChannels().Single(c => c.Id == "nvidia:0:0").Mode);
    }

    [Fact]
    public void IsNvidiaId_OnlyMatchesPrefix()
    {
        Assert.True(LinuxNvidiaFanProvider.IsNvidiaId("nvidia:0:1"));
        Assert.True(LinuxNvidiaFanProvider.IsNvidiaId("nvidia:temp:0"));
        Assert.False(LinuxNvidiaFanProvider.IsNvidiaId("liquidctl:x"));
        Assert.False(LinuxNvidiaFanProvider.IsNvidiaId(""));
    }

    [Fact]
    public void SetFanSpeed_PersistsManualDuty()
    {
        // The persisted duty is replayed across restarts by CurveEngine's
        // presence-gated replay (see CurveEngineTests), not by this provider.
        var store = new InMemoryConfigStore();
        new LinuxNvidiaFanProvider(DualFan, (_, _, _) => true, store).SetFanSpeed("nvidia:0:0", 70);
        Assert.Equal(70, store.Load().Cooling.ManualSpeeds["nvidia:0:0"]);
    }

    [Fact]
    public void ReleaseAll_PreservesPersistedManualDuties()
    {
        // ReleaseAll runs on shutdown/profile switch; the persisted intent
        // must survive for the replay. Only ReleaseFan drops an entry.
        var store = new InMemoryConfigStore();
        var p = new LinuxNvidiaFanProvider(DualFan, (_, _, _) => true, store);
        p.SetFanSpeed("nvidia:0:0", 70);
        p.ReleaseAll();
        Assert.Equal(70, store.Load().Cooling.ManualSpeeds["nvidia:0:0"]);
    }

    [Fact]
    public void ReleaseFan_RemovesPersistedOverride()
    {
        var store = new InMemoryConfigStore();
        var p = new LinuxNvidiaFanProvider(DualFan, (_, _, _) => true, store);
        p.SetFanSpeed("nvidia:0:0", 70);
        p.ReleaseFan("nvidia:0:0");
        Assert.False(store.Load().Cooling.ManualSpeeds.ContainsKey("nvidia:0:0"));
    }

    [Fact]
    public void DriveFanSpeed_DoesNotPersist()
    {
        var store = new InMemoryConfigStore();
        var p = new LinuxNvidiaFanProvider(DualFan, (_, _, _) => true, store);
        p.DriveFanSpeed("nvidia:0:0", 70);
        Assert.Empty(store.Load().Cooling.ManualSpeeds);
    }

    [Fact]
    public void GetFanChannels_CurveBoundFan_ReportsCurveMode()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Cooling.Curves.Add(new Nexus.Service.Persistence.CurveDocument
        {
            Id = "c1",
            Outputs = { new Nexus.Service.Persistence.CurveOutputDocument { Id = "nvidia:0:0" } },
        }));
        var p = new LinuxNvidiaFanProvider(DualFan, (_, _, _) => true, store);
        Assert.Equal(FanModes.Curve, p.GetFanChannels().Single(c => c.Id == "nvidia:0:0").Mode);
    }
}
