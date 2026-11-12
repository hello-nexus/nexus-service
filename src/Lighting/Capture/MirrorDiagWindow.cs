using System;
using System.Globalization;

namespace Nexus.Service.Lighting.Capture;

/// <summary>
/// One reporting window of Screen Mirror capture counters and timing samples.
/// Sample buffers are preallocated so recording never allocates; samples past
/// the capacity are dropped from the percentiles but never from the counters.
/// </summary>
internal sealed class MirrorDiagWindow
{
    private const int Capacity = 4096;
    private readonly double[] _acquire = new double[Capacity];
    private readonly double[] _copyMap = new double[Capacity];
    private readonly double[] _blit = new double[Capacity];
    private int _acquireN, _copyMapN, _blitN;

    public int AcquireCalls, Timeouts, Frames, PointerOnly, Copies, Pushed, Lost, Recreated;
    public long AccumulatedSum;

    public void RecordAcquire(double ms) { if (_acquireN < Capacity) _acquire[_acquireN++] = ms; }
    public void RecordCopyMap(double ms) { if (_copyMapN < Capacity) _copyMap[_copyMapN++] = ms; }
    public void RecordBlit(double ms) { if (_blitN < Capacity) _blit[_blitN++] = ms; }

    /// <summary>Nearest-rank percentile of an unsorted span; sorts it in place.</summary>
    public static double Percentile(Span<double> samples, double p)
    {
        if (samples.IsEmpty) return 0;
        samples.Sort();
        var rank = (int)Math.Ceiling(p / 100.0 * samples.Length);
        return samples[Math.Clamp(rank - 1, 0, samples.Length - 1)];
    }

    private static string Triple(double[] buf, int n)
    {
        var s = buf.AsSpan(0, n);
        var p50 = Percentile(s, 50);
        var p95 = Percentile(s, 95);
        var max = n == 0 ? 0 : s[n - 1];
        return string.Create(CultureInfo.InvariantCulture, $"{p50:F2}/{p95:F2}/{max:F2}");
    }

    public string FormatAndReset(double seconds)
    {
        var fps = seconds > 0 ? Pushed / seconds : 0;
        var line = string.Create(CultureInfo.InvariantCulture,
            $"[mirror-diag] {seconds:F1}s acquire={AcquireCalls} timeouts={Timeouts} frames={Frames} pointerOnly={PointerOnly} accumulated={AccumulatedSum} copies={Copies} pushed={Pushed} fps={fps:F1} lost={Lost} recreated={Recreated} acquireMs={Triple(_acquire, _acquireN)} copyMapMs={Triple(_copyMap, _copyMapN)} blitMs={Triple(_blit, _blitN)} (p50/p95/max)");
        AcquireCalls = Timeouts = Frames = PointerOnly = Copies = Pushed = Lost = Recreated = 0;
        AccumulatedSum = 0;
        _acquireN = _copyMapN = _blitN = 0;
        return line;
    }
}
