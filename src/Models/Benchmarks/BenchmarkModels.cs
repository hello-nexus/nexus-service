using System.Collections.Generic;

namespace Nexus.Service.Models.Benchmarks;

public sealed class HardwareIdentity
{
    public string CpuModel { get; set; } = "";
    public List<string> GpuModels { get; set; } = new();
    public string RamModel { get; set; } = "";
    public long RamBytes { get; set; }
    public string StorageModel { get; set; } = "";
    public int LogicalCores { get; set; }
    public string Os { get; set; } = "";
    public string Architecture { get; set; } = "";
}

public sealed class BenchmarkSubScore
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public double Score { get; set; }
    public double RawValue { get; set; }
    public string RawUnit { get; set; } = "";
    public string Detail { get; set; } = "";

    /// <summary>Per-trial scored values (the same unit as RawValue) for axes that run multiple trials. Null when the axis runs a single measurement.</summary>
    public double[]? Trials { get; set; }

    /// <summary>(max - min) / median across Trials. Zero when Trials has fewer than two entries.</summary>
    public double Spread { get; set; }

    /// <summary>CPU only: the single-thread primesieve rate, alongside the all-core RawValue.</summary>
    public double SingleCoreRawValue { get; set; }
    public string SingleCoreRawUnit { get; set; } = "";

    /// <summary>Storage only: DiskSpd's 4K random-access IOPS and average latency, alongside the sequential RawValue.</summary>
    public double RandomIops { get; set; }
    public double LatencyMs { get; set; }

    /// <summary>GPU only: the device name clpeak/vkpeak actually measured, used to align HardwareIdentity.GpuModels with the scored card.</summary>
    public string MeasuredDevice { get; set; } = "";
}

public sealed class BenchmarkPhaseProgress
{
    public string Phase { get; set; } = "";
    public double Percent { get; set; }
    public string Detail { get; set; } = "";
}

public sealed class BenchmarkProgressFrame
{
    public string RunId { get; set; } = "";
    public string State { get; set; } = "";
    public double OverallPercent { get; set; }
    public BenchmarkPhaseProgress Phase { get; set; } = new();
    public List<BenchmarkSubScore> CompletedSubScores { get; set; } = new();
}

public sealed class BenchmarkResult
{
    public string RunId { get; set; } = "";
    public string State { get; set; } = "";
    public long StartedAt { get; set; }
    public long FinishedAt { get; set; }
    public HardwareIdentity Hardware { get; set; } = new();
    public double Composite { get; set; }
    public BenchmarkSubScore Cpu { get; set; } = new();
    public BenchmarkSubScore Gpu { get; set; } = new();
    public BenchmarkSubScore Ram { get; set; } = new();
    public BenchmarkSubScore Storage { get; set; } = new();
    public string Error { get; set; } = "";
    public string ScoringVersion { get; set; } = "";
    public BenchmarkBaselines Baselines { get; set; } = new();
    public Dictionary<string, string> Tools { get; set; } = new();
}

public sealed class BenchmarkBaselines
{
    public double Cpu { get; set; }
    public double Gpu { get; set; }
    public double Ram { get; set; }
    public double Storage { get; set; }
}

// DTOs for clpeak --json-file flat output (source-gen JSON, AOT-safe)
public sealed class ClpeakResult
{
    [System.Text.Json.Serialization.JsonPropertyName("clpeak_version")]
    public string? ClpeakVersion { get; set; }
    public ClpeakEntry[]? Entries { get; set; }
}

public sealed class ClpeakEntry
{
    public string? Backend { get; set; }
    public string? Platform { get; set; }
    public string? Device { get; set; }
    public string? Category { get; set; }
    public string? Test { get; set; }
    public string? Metric { get; set; }
    public string? Unit { get; set; }
    public string? Status { get; set; }
    public double Value { get; set; }
}

public sealed class StartBenchmarkResponse
{
    public string RunId { get; set; } = "";
    public bool Started { get; set; }
    public string Error { get; set; } = "";
}

public sealed class StartBenchmarkBody
{
    public bool IncludeGpu { get; set; } = true;
}
