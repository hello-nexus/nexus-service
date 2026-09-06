using BenchmarkDotNet.Attributes;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.Hyte.Np50;

namespace Nexus.Service.Benchmarks;

/// <summary>
/// NP50 per-port frame assembly: the RGB-bytes -> RgbColor transform with
/// brightness scaling that runs for every strip on every lighting tick (~30 Hz,
/// 4-port cycle). Writes into a pre-allocated buffer, so it is zero-alloc by
/// design | this tracks the per-LED CPU cost and guards that invariant.
/// </summary>
[MemoryDiagnoser]
[InProcess]
public class Np50Benchmarks
{
    private const int LedCount = 120;
    private readonly RgbColor[] _dst = new RgbColor[LedCount];
    private readonly byte[] _src = new byte[LedCount * 3];

    [Benchmark]
    public void FillFullBrightness() =>
        Np50LightingFrameWriter.FillBufferSlice(_dst, 0, _src, LedCount, 1.0, default, false, 0, 0);

    [Benchmark]
    public void FillScaled() =>
        Np50LightingFrameWriter.FillBufferSlice(_dst, 0, _src, LedCount, 0.5, default, false, 0, 0);
}
