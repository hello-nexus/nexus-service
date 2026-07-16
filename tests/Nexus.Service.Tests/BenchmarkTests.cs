using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Benchmarks;
using Nexus.Service.Benchmarks.Providers;
using Nexus.Service.Models.Benchmarks;
using Nexus.Service.Models.Sensors;

namespace Nexus.Service.Tests;

public class BenchmarkTests
{
    [Fact]
    public void Scoring_Score_ZeroReference_ReturnsZero()
    {
        Assert.Equal(0, Scoring.Score(100, 0));
    }

    [Fact]
    public void Scoring_Score_AtBaseline_ReturnsThousand()
    {
        Assert.Equal(1000, Scoring.Score(Scoring.BaselineCpuPrimesPerSec, Scoring.BaselineCpuPrimesPerSec));
    }

    [Fact]
    public void Scoring_Composite_AllThousand_ReturnsThousand()
    {
        var result = Scoring.Composite(1000, 1000, 1000, 1000);
        Assert.InRange(result, 999, 1001);
    }

    [Fact]
    public void Scoring_Composite_WeakStorageHurtsComposite()
    {
        var balanced = Scoring.Composite(1000, 1000, 1000, 1000);
        var weakStorage = Scoring.Composite(1000, 1000, 1000, 100);
        Assert.True(weakStorage < balanced * 0.75);
    }

    [Fact]
    public void Scoring_ScoringVersion_NotEmpty()
    {
        Assert.False(string.IsNullOrEmpty(Scoring.ScoringVersion));
    }

    [Fact]
    public void Scoring_ScoringVersion_IsCurrentMethodologyVersion()
    {
        Assert.Equal("v2.2-2026.07", Scoring.ScoringVersion);
    }

    [Fact]
    public void Scoring_Median_OddCount_ReturnsMiddleValue()
    {
        Assert.Equal(20, Scoring.Median(new List<double> { 10, 30, 20 }));
    }

    [Fact]
    public void Scoring_Median_EvenCount_AveragesMiddleTwo()
    {
        Assert.Equal(25, Scoring.Median(new List<double> { 10, 20, 30, 40 }));
    }

    [Fact]
    public void Scoring_Median_UnsortedInput_StillSorts()
    {
        Assert.Equal(5, Scoring.Median(new List<double> { 9, 1, 5 }));
    }

    [Fact]
    public void Scoring_Median_Empty_ReturnsZero()
    {
        Assert.Equal(0, Scoring.Median(new List<double>()));
    }

    [Fact]
    public void Scoring_RelativeSpread_ComputesMaxMinusMinOverMedian()
    {
        // (110 - 90) / 100 = 0.2
        var spread = Scoring.RelativeSpread(new List<double> { 90, 100, 110 });
        Assert.Equal(0.2, spread);
    }

    [Fact]
    public void Scoring_RelativeSpread_SingleValue_ReturnsZero()
    {
        Assert.Equal(0, Scoring.RelativeSpread(new List<double> { 100 }));
    }

    [Fact]
    public void Scoring_RelativeSpread_IdenticalTrials_ReturnsZero()
    {
        Assert.Equal(0, Scoring.RelativeSpread(new List<double> { 100, 100, 100 }));
    }

    [Fact]
    public void BenchmarkProgressFrame_Roundtrip()
    {
        var frame = new BenchmarkProgressFrame
        {
            RunId = "abc",
            State = "running",
            OverallPercent = 0.5,
            Phase = new BenchmarkPhaseProgress { Phase = "cpu", Percent = 0.5, Detail = "single" },
        };
        var json = System.Text.Json.JsonSerializer.Serialize(
            frame, Nexus.Service.Serialization.AppJsonContext.Default.BenchmarkProgressFrame);
        Assert.Contains("\"runId\":\"abc\"", json);
        Assert.Contains("\"phase\":{", json);
    }

    [Fact]
    public void ParseStreamTriad_ValidOutput_ParsesGbPerSec()
    {
        const string output = @"Function    Best Rate MB/s  Avg time     Min time     Max time
Copy:           52000.0     0.006154     0.006154     0.006155
Scale:          51000.0     0.006278     0.006278     0.006280
Add:            53000.0     0.009056     0.009056     0.009056
Triad:          54321.0     0.009055     0.009055     0.009055";

        double result = ExternalToolBenchmarkProvider.ParseStreamTriad(output);
        Assert.InRange(result, 54.3, 54.4);
    }

    [Fact]
    public void ParseDiskSpdXml_ValidXml_ParsesMbPerSec()
    {
        // DiskSpd emits a config <TimeSpan> (no <TestTimeSeconds>, has <Duration>)
        // before the results <TimeSpan>. The parser must read the results node.
        const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<Results>
  <Profile>
    <TimeSpans>
      <TimeSpan>
        <Duration>15</Duration>
        <Targets><Target><Path>x</Path></Target></Targets>
      </TimeSpan>
    </TimeSpans>
  </Profile>
  <TimeSpan>
    <TestTimeSeconds>15</TestTimeSeconds>
    <Thread>
      <Target>
        <BytesCount>47185920000</BytesCount>
        <IOCount>45000</IOCount>
      </Target>
    </Thread>
    <AverageLatencyMilliseconds>0.33</AverageLatencyMilliseconds>
  </TimeSpan>
</Results>";

        var (seqMbPerSec, iops, latMs) = ExternalToolBenchmarkProvider.ParseDiskSpdXml(xml);
        Assert.True(seqMbPerSec > 2000, $"expected >2000 MB/s, got {seqMbPerSec}");
        Assert.True(iops > 0, $"expected positive IOPS, got {iops}");
        Assert.InRange(latMs, 0.3, 0.4);
    }

    [Fact]
    public void ParseClpeakJson_RealSchema_PicksGpuSinglePrecisionAndBandwidth()
    {
        // Flat clpeak schema: the benchmark name is in `test`, the coarse group
        // in `category`; a "CPU" backend pseudo-device must be excluded and
        // unsupported entries (status, no value) skipped.
        const string json = @"{""clpeak_version"":""2.0.13"",""entries"":[
{""backend"":""OpenCL"",""category"":""fp_compute"",""test"":""single_precision_compute"",""metric"":""float2"",""unit"":""gflops"",""value"":20962.3,""device"":""NVIDIA GeForce RTX 4060""},
{""backend"":""CPU"",""category"":""fp_compute"",""test"":""single_precision_compute"",""metric"":""float MT"",""unit"":""gflops"",""value"":1077.5},
{""backend"":""OpenCL"",""category"":""fp_compute"",""test"":""half_precision_compute"",""metric"":""half"",""unit"":""gflops"",""status"":""unsupported""},
{""backend"":""OpenCL"",""category"":""bandwidth"",""test"":""global_memory_bandwidth"",""metric"":""float4"",""unit"":""gbps"",""value"":417.8},
{""backend"":""CPU"",""category"":""bandwidth"",""test"":""global_memory_bandwidth"",""metric"":""triad"",""unit"":""gbps"",""value"":31.3}]}";

        var (gflops, memGbPerSec, version, device) = ExternalToolBenchmarkProvider.ParseClpeakJson(json);
        Assert.InRange(gflops, 20962.2, 20962.4);
        Assert.InRange(memGbPerSec, 417.7, 417.9);
        Assert.Equal("2.0.13", version);
        Assert.Equal("NVIDIA GeForce RTX 4060", device);
    }

    [Fact]
    public void ParseClpeakJson_NoGpuEntries_ReturnsZero()
    {
        const string json = @"{""clpeak_version"":""2.0.13"",""entries"":[
{""backend"":""CPU"",""category"":""fp_compute"",""test"":""single_precision_compute"",""metric"":""float MT"",""unit"":""gflops"",""value"":1077.5}]}";
        var (gflops, memGbPerSec, _, device) = ExternalToolBenchmarkProvider.ParseClpeakJson(json);
        Assert.Equal(0, gflops);
        Assert.Equal(0, memGbPerSec);
        Assert.Null(device);
    }

    [Fact]
    public void ParsePrimesPerSec_RealOutput_ParsesCorrectly()
    {
        const string output = "Primes: 455,052,511\nSeconds: 2.123";
        double result = ExternalToolBenchmarkProvider.ParsePrimesPerSec(output);
        Assert.InRange(result, 214_000_000d, 215_000_000d);
    }

    [Fact]
    public void ParsePrimesPerSec_NoMatch_ReturnsZero()
    {
        double result = ExternalToolBenchmarkProvider.ParsePrimesPerSec("some unrelated output");
        Assert.Equal(0, result);
    }

    private static GpuReadout Gpu(string name, bool integrated) => new() { Name = name, Integrated = integrated };

    [Fact]
    public void SelectReportedGpus_IntegratedPlusDiscrete_ReportsDiscrete()
    {
        var result = BenchmarkRunner.SelectReportedGpus(new List<GpuReadout>
        {
            Gpu("AMD Radeon(TM) Graphics", integrated: true),
            Gpu("NVIDIA GeForce RTX 5080", integrated: false),
        });
        Assert.Equal(new List<string> { "NVIDIA GeForce RTX 5080" }, result);
    }

    [Fact]
    public void SelectReportedGpus_DiscreteListedFirst_StillReportsDiscrete()
    {
        var result = BenchmarkRunner.SelectReportedGpus(new List<GpuReadout>
        {
            Gpu("NVIDIA GeForce RTX 5080", integrated: false),
            Gpu("Intel(R) UHD Graphics 770", integrated: true),
        });
        Assert.Equal(new List<string> { "NVIDIA GeForce RTX 5080" }, result);
    }

    [Fact]
    public void SelectReportedGpus_OnlyIntegrated_ReportsIntegrated()
    {
        var result = BenchmarkRunner.SelectReportedGpus(new List<GpuReadout>
        {
            Gpu("Intel(R) UHD Graphics 770", integrated: true),
        });
        Assert.Equal(new List<string> { "Intel(R) UHD Graphics 770" }, result);
    }

    [Fact]
    public void SelectReportedGpus_Empty_ReturnsEmpty()
    {
        Assert.Empty(BenchmarkRunner.SelectReportedGpus(new List<GpuReadout>()));
    }

    [Fact]
    public void SelectReportedGpus_MeasuredDeviceMatchesSecondDiscrete_ReportsTheMeasuredOne()
    {
        // Without a hint, the Integrated-flag heuristic would report the first
        // discrete GPU (RTX 5080) - the measured hint must override that when
        // clpeak actually scored the second card (RTX 4060).
        var result = BenchmarkRunner.SelectReportedGpus(
            new List<GpuReadout>
            {
                Gpu("NVIDIA GeForce RTX 5080", integrated: false),
                Gpu("NVIDIA GeForce RTX 4060", integrated: false),
            },
            measuredDevice: "NVIDIA GeForce RTX 4060");
        Assert.Equal(new List<string> { "NVIDIA GeForce RTX 4060" }, result);
    }

    [Fact]
    public void SelectReportedGpus_MeasuredDeviceNoMatch_FallsBackToIntegratedHeuristic()
    {
        // AMD can report a bare codename clpeak-side ("gfx1036") that shares no
        // substring with the WMI-friendly name; a miss must not crash or blank
        // the result, it must fall back to the existing heuristic.
        var result = BenchmarkRunner.SelectReportedGpus(
            new List<GpuReadout>
            {
                Gpu("AMD Radeon(TM) Graphics", integrated: true),
                Gpu("AMD Radeon RX 7600", integrated: false),
            },
            measuredDevice: "gfx1036");
        Assert.Equal(new List<string> { "AMD Radeon RX 7600" }, result);
    }

    [Fact]
    public void SelectReportedGpus_MeasuredDeviceHint_MatchesRegardlessOfOrder()
    {
        var result = BenchmarkRunner.SelectReportedGpus(
            new List<GpuReadout>
            {
                Gpu("Intel(R) UHD Graphics 770", integrated: true),
                Gpu("NVIDIA GeForce RTX 4060", integrated: false),
            },
            measuredDevice: "NVIDIA GeForce RTX 4060");
        Assert.Equal(new List<string> { "NVIDIA GeForce RTX 4060" }, result);
    }

    [Fact]
    public void SelectReportedGpus_NullMeasuredDevice_UnchangedBehavior()
    {
        var result = BenchmarkRunner.SelectReportedGpus(
            new List<GpuReadout>
            {
                Gpu("NVIDIA GeForce RTX 5080", integrated: false),
                Gpu("Intel(R) UHD Graphics 770", integrated: true),
            },
            measuredDevice: null);
        Assert.Equal(new List<string> { "NVIDIA GeForce RTX 5080" }, result);
    }

    [Fact]
    public void BenchmarkSubScore_TrialsAndSpread_Roundtrip()
    {
        var sub = new BenchmarkSubScore
        {
            Key = "ram",
            Label = "RAM",
            Score = 1000,
            Trials = new[] { 34.0, 35.0, 36.0 },
            Spread = 0.0588,
        };
        var json = System.Text.Json.JsonSerializer.Serialize(
            sub, Nexus.Service.Serialization.AppJsonContext.Default.BenchmarkSubScore);
        Assert.Contains("\"trials\":[34,35,36]", json);
        Assert.Contains("\"spread\":0.0588", json);
    }

    [Fact]
    public void BenchmarkSubScore_TrialsOmittedWhenNull()
    {
        // AppJsonContext serializes with DefaultIgnoreCondition.WhenWritingNull,
        // so a null Trials array drops the key entirely rather than emitting
        // "trials":null.
        var sub = new BenchmarkSubScore { Key = "cpu", Label = "CPU", Score = 1000 };
        var json = System.Text.Json.JsonSerializer.Serialize(
            sub, Nexus.Service.Serialization.AppJsonContext.Default.BenchmarkSubScore);
        Assert.DoesNotContain("\"trials\"", json);
    }

    [Fact]
    public void BenchmarkPhaseProgress_Roundtrip_HasNoDeadCurrentRawFields()
    {
        var phase = new BenchmarkPhaseProgress { Phase = "gpu", Percent = 0.5, Detail = "trial 2/3" };
        var json = System.Text.Json.JsonSerializer.Serialize(
            phase, Nexus.Service.Serialization.AppJsonContext.Default.BenchmarkPhaseProgress);
        Assert.DoesNotContain("currentRaw", json);
        Assert.DoesNotContain("currentUnit", json);
    }
}
