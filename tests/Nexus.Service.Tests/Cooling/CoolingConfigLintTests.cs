using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Xunit;

namespace Nexus.Service.Tests.Cooling;

public class CoolingConfigLintTests
{
    private const string GpuFan = "/gpu-nvidia/0/control/1";
    private const string Fan1 = "/lpc/it8696e/0/control/0";
    private static readonly string[] Followers = { "/lpc/it8696e/0/control/1", "/lpc/it8696e/0/control/2", "/lpc/it8696e/0/control/4" };

    private static FanChannel Mobo(string id, string name) => new() { Id = id, Name = name };

    private static List<FanChannel> T1Channels()
    {
        var list = new List<FanChannel>
        {
            new() { Id = GpuFan, Name = "GPU Fan 1", IsGpu = true, DeviceId = "/gpu-nvidia/0" },
            Mobo(Fan1, "Fan #1"),
        };
        for (var i = 0; i < Followers.Length; i++)
        {
            list.Add(Mobo(Followers[i], $"Fan #{i + 2}"));
        }
        return list;
    }

    private static List<TemperatureSource> Sources() => new()
    {
        new() { Id = "/amdcpu/0/temperature/2", Name = "Core (Tctl/Tdie)", Category = "CPU", Value = 40 },
        new() { Id = "/nvme/0/temperature/0", Name = "NVMe", Category = "Storage", Value = 40 },
    };

    private static List<CurveDocument> T1Curves() => new()
    {
        new CurveDocument
        {
            Id = "preset-silent", Name = "Silent", Type = "Sync", Preset = "silent",
            Sync = new SyncCurveData { SourceChannelId = GpuFan },
            Outputs = { new CurveOutputDocument { Id = Fan1, Type = "Fan" } },
        },
        new CurveDocument
        {
            Id = "sync-fan1", Name = "Sync Curve Fan 1", Type = "Sync",
            Sync = new SyncCurveData { SourceChannelId = Fan1 },
            Outputs = Followers.Select(f => new CurveOutputDocument { Id = f, Type = "Fan" }).ToList(),
        },
    };

    private static LintInput Input(List<CurveDocument> curves, double limit = 95) => new()
    {
        Curves = curves,
        Channels = T1Channels(),
        Sources = Sources(),
        LimitC = limit,
    };

    private static string Json(IEnumerable<CurveDocument> curves) => JsonSerializer.Serialize(
        new SetCurvesBody { Curves = curves.Select(CurveWireMapper.ToWire).ToList() },
        AppJsonContext.Default.SetCurvesBody);

    [Fact]
    public void T1Config_SyncChainRootedAtGpuFan_FlagsEveryMotherboardFan()
    {
        var hazards = CoolingConfigLint.Analyze(Input(T1Curves()));

        Assert.Equal(4, hazards.Count);
        Assert.All(hazards, h =>
        {
            Assert.Equal(CoolingHazardKinds.FollowsStoppableSource, h.Kind);
            Assert.Equal(GpuFan, h.RootId);
            Assert.Equal("GPU Fan 1", h.RootName);
        });
        Assert.Contains(hazards, h => h.ChannelId == Fan1 && h.ChannelName == "Fan #1");
    }

    [Fact]
    public void SyncToBiosManagedFan_IsStoppable()
    {
        var curves = new List<CurveDocument>
        {
            new() { Id = "s", Name = "s", Type = "Sync", Sync = new SyncCurveData { SourceChannelId = Followers[0] },
                    Outputs = { new CurveOutputDocument { Id = Fan1 } } },
        };
        var hazards = CoolingConfigLint.Analyze(Input(curves));
        Assert.Single(hazards);
        Assert.Equal(Followers[0], hazards[0].RootId);
    }

    [Fact]
    public void SyncToCpuCurveDrivenFan_IsSafe()
    {
        var curves = new List<CurveDocument>
        {
            Graph("cpu", "/amdcpu/0/temperature/2", Fan1, (30, 30), (70, 70), (90, 100)),
            new() { Id = "s", Name = "s", Type = "Sync", Sync = new SyncCurveData { SourceChannelId = Fan1 },
                    Outputs = { new CurveOutputDocument { Id = Followers[0] } } },
        };
        Assert.Empty(CoolingConfigLint.Analyze(Input(curves)));
    }

    [Fact]
    public void NonCpuSensorCurve_IsFlagged()
    {
        var curves = new List<CurveDocument> { Graph("nvme", "/nvme/0/temperature/0", Fan1, (30, 30), (90, 100)) };
        var h = Assert.Single(CoolingConfigLint.Analyze(Input(curves)));
        Assert.Equal(CoolingHazardKinds.NonCpuSensor, h.Kind);
        Assert.Equal("/nvme/0/temperature/0", h.RootId);
    }

    [Fact]
    public void LowCeilingAtLimit_IsFlagged()
    {
        var curves = new List<CurveDocument> { Graph("low", "/amdcpu/0/temperature/2", Fan1, (30, 20), (95, 50)) };
        var h = Assert.Single(CoolingConfigLint.Analyze(Input(curves)));
        Assert.Equal(CoolingHazardKinds.LowCeiling, h.Kind);
    }

    [Fact]
    public void ManualLowWithNoCurve_IsFlagged_AndManualHighIsNot()
    {
        var input = new LintInput
        {
            Channels = T1Channels(),
            Sources = Sources(),
            ManualSpeeds = new Dictionary<string, int> { [Fan1] = 20, [Followers[0]] = 60 },
        };
        var h = Assert.Single(CoolingConfigLint.Analyze(input));
        Assert.Equal(CoolingHazardKinds.ManualLow, h.Kind);
        Assert.Equal(Fan1, h.ChannelId);
    }

    [Fact]
    public void GpuAndHubFans_AreNotCpuCooling_ButPumpsAre()
    {
        var roles = new Dictionary<string, string>();
        Assert.False(CoolingConfigLint.IsCpuCooling(new FanChannel { Id = "g", IsGpu = true }, roles));
        Assert.False(CoolingConfigLint.IsCpuCooling(new FanChannel { Id = "h", DeviceId = "np50:1" }, roles));
        Assert.True(CoolingConfigLint.IsCpuCooling(new FanChannel { Id = "p", Kind = FanKinds.Pump, DeviceId = "q:1" }, roles));
        Assert.True(CoolingConfigLint.IsCpuCooling(new FanChannel { Id = "m" }, roles));
        Assert.False(CoolingConfigLint.IsCpuCooling(new FanChannel { Id = "m" }, new Dictionary<string, string> { ["m"] = FanRoleKind.Gpu }));
    }

    [Fact]
    public void Heal_ProducesMixedMaxOfOriginalAndSafeCpuCurve_AndLeavesNoHazards()
    {
        var curves = T1Curves();
        var input = Input(curves);
        var hazards = CoolingConfigLint.Analyze(input);

        var result = CoolingConfigLint.Heal(input, hazards);

        Assert.NotNull(result);
        Assert.Equal(4, result!.Healed.Count);
        var mix = result.Curves.Single(c => c.Id == CoolingConfigLint.MixIdPrefix + Fan1);
        Assert.Equal("Mixed", mix.Type);
        Assert.Equal("max", mix.Mixed!.Fn);
        Assert.Equal(new[] { "preset-silent", CoolingConfigLint.SafeCurveId }, mix.Mixed.CurveIds);
        Assert.Equal(Fan1, Assert.Single(mix.Outputs).Id);
        var safe = result.Curves.Single(c => c.Id == CoolingConfigLint.SafeCurveId);
        Assert.Equal("Graph", safe.Type);
        Assert.Equal("/amdcpu/0/temperature/2", safe.Input.Id);

        // Evaluated with the GPU fan at 0 and the CPU at 85 C, the result follows the safe curve.
        var silentRaw = CurveEngine.EvaluateSync(new SyncCurveData { SourceChannelId = GpuFan },
            new Dictionary<string, double> { [GpuFan] = 0 })!.Value;
        var safeRaw = CurveEngine.EvaluateGraph(safe.Graph, 85)!.Value;
        var mixed = CurveEngine.EvaluateMix(mix.Mixed,
            new Dictionary<string, double> { ["preset-silent"] = silentRaw, [CoolingConfigLint.SafeCurveId] = safeRaw })!.Value;
        Assert.Equal(0, silentRaw);
        Assert.Equal(System.Math.Max(silentRaw, safeRaw), mixed);
        Assert.True(mixed >= 60);

        var after = new LintInput { Curves = result.Curves, Channels = input.Channels, Sources = input.Sources, LimitC = input.LimitC };
        Assert.Empty(CoolingConfigLint.Analyze(after));
    }

    [Fact]
    public void Heal_DoesNotMutateTheInputCurves()
    {
        var curves = T1Curves();
        var before = Json(curves);
        var input = Input(curves);
        CoolingConfigLint.Heal(input, CoolingConfigLint.Analyze(input));
        Assert.Equal(before, Json(curves));
    }

    [Fact]
    public void Heal_ManualLowChannel_KeepsTheManualDutyAsAMixMember()
    {
        var input = new LintInput
        {
            Channels = T1Channels(),
            Sources = Sources(),
            ManualSpeeds = new Dictionary<string, int> { [Fan1] = 20 },
        };
        var result = CoolingConfigLint.Heal(input, CoolingConfigLint.Analyze(input))!;
        var manual = result.Curves.Single(c => c.Id == CoolingConfigLint.ManualIdPrefix + Fan1);
        Assert.Equal(20, manual.Flat!.Speed);
        Assert.Empty(CoolingConfigLint.Analyze(new LintInput { Curves = result.Curves, Channels = input.Channels, Sources = input.Sources, ManualSpeeds = input.ManualSpeeds }));
    }

    [Fact]
    public void Heal_WithoutACpuTemperatureSource_IsNotAvailable()
    {
        var input = new LintInput { Curves = T1Curves(), Channels = T1Channels(), Sources = new List<TemperatureSource>() };
        Assert.Null(CoolingConfigLint.Heal(input, CoolingConfigLint.Analyze(input)));
    }

    [Fact]
    public void ControllerHealThenUndo_RestoresTheSnapshotExactly()
    {
        var fans = new LintFans(T1Channels(), Sources());
        var store = new InMemoryConfigStore();
        store.Update(s => s.Cooling.Curves.AddRange(T1Curves()));
        var before = Json(store.Load().Cooling.Curves);
        var controller = new ThermalGuardController(fans, store);

        var healed = controller.HealNow(automatic: false);

        Assert.True(healed.UndoAvailable);
        Assert.Equal(4, healed.Channels.Count);
        Assert.NotEqual(before, Json(store.Load().Cooling.Curves));

        var undone = controller.Undo();

        Assert.False(undone.UndoAvailable);
        Assert.Empty(undone.Channels);
        Assert.Equal(before, Json(store.Load().Cooling.Curves));
    }

    [Fact]
    public void ControllerLint_ReportsHazardsAndFixAvailability()
    {
        var fans = new LintFans(T1Channels(), Sources());
        var store = new InMemoryConfigStore();
        var controller = new ThermalGuardController(fans, store);
        var body = new SetCurvesBody { Curves = T1Curves().Select(CurveWireMapper.ToWire).ToList() };

        var response = controller.Lint(body);

        Assert.Equal(4, response.Hazards.Count);
        Assert.True(response.FixAvailable);
        Assert.Contains(response.Hazards, h => h.Kind == CoolingHazardKinds.FollowsStoppableSource && h.RootId == GpuFan);
    }

    private static CurveDocument Graph(string id, string sensor, string output, params (double Temp, double Speed)[] points) => new()
    {
        Id = id,
        Name = id,
        Type = "Graph",
        Input = new CurveInputDocument { Id = sensor, Type = "Temperature" },
        Outputs = { new CurveOutputDocument { Id = output, Type = "Fan" } },
        Graph = new GraphCurveData { Points = points.Select(p => new Nexus.Service.Persistence.GraphPoint { Temp = p.Temp, Speed = p.Speed }).ToList() },
    };

    private sealed class LintFans : IFanControlProvider
    {
        private readonly List<FanChannel> _channels;
        private readonly List<TemperatureSource> _sources;
        public LintFans(List<FanChannel> channels, List<TemperatureSource> sources)
        {
            _channels = channels;
            _sources = sources;
        }
        public IReadOnlyList<FanChannel> GetFanChannels() => _channels;
        public IReadOnlyList<TemperatureSource> GetTemperatureSources() => _sources;
        public float? ReadTemperature(string sensorId) => null;
        public int SetFanSpeed(string channelId, int dutyPercent) => dutyPercent;
        public void DriveFanSpeed(string channelId, int dutyPercent) { }
        public void ReleaseFan(string channelId) { }
        public void ReleaseAll() { }
        public System.Threading.Tasks.Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds, System.IProgress<FanCalibrationProgress> progress, System.Threading.CancellationToken ct)
            => System.Threading.Tasks.Task.FromResult<IReadOnlyList<FanCalibration>>(new List<FanCalibration>());
    }
}
