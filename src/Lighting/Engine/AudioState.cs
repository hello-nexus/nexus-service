using System;
using System.Diagnostics;
using System.Threading;

namespace Nexus.Service.Lighting.Engine;

/// <summary>
/// Shared latest-frame snapshot of audio features that every shader can read
/// as a uniform. Producer: <see cref="Nexus.Service.Activity.BeatsProvider"/>.
/// Consumers: <see cref="Gpu.ShaderEffect"/>.
///
/// Tiny and allocation-free on both paths: the audio thread writes individual
/// floats, the GL thread reads them. Torn reads are acceptable - these values
/// are noisy and are smoothed again in the shader via per-effect audioBoost
/// scaling.
///
/// When <see cref="Reset"/> is called (music-reactive toggled off, capture
/// stopped, or no device available), every field drops to zero, so every shader
/// sees zero uniforms and its idle animation runs unmodified.
/// </summary>
public static class AudioState
{
    private const int SpectrumBins = 16;

    public static float Level;
    public static float Bass;
    public static float Mid;
    public static float High;
    /// <summary>1.0 when a beat fires; decays exponentially toward 0 over ~0.5s.</summary>
    public static float Beat;
    /// <summary>Log-spaced coarse bands. Length <see cref="SpectrumBins"/>, values in [0, 1].</summary>
    public static readonly float[] Spectrum = new float[SpectrumBins];

    public static int SpectrumLength => SpectrumBins;

    public const int Spectrum64Bins = 64;
    public static readonly float[] Spectrum64 = new float[Spectrum64Bins];
    public static int Spectrum64Length => Spectrum64Bins;

    /// <summary>Per-channel 64-band spectra; only advanced while <see cref="StereoWanted"/>.</summary>
    public static readonly float[] SpectrumLeft64 = new float[Spectrum64Bins];
    public static readonly float[] SpectrumRight64 = new float[Spectrum64Bins];

    public const int Spectrum64Vec4Count = Spectrum64Bins / 4;

    private const long StereoHoldMs = 1000;
    private static long _stereoRequestedAt = long.MinValue;

    /// <summary>Called every frame by a renderer that reads the per-channel spectra.</summary>
    public static void RequestStereo() => RequestStereo(Stopwatch.GetTimestamp());

    public static bool StereoWanted => StereoWantedAt(Stopwatch.GetTimestamp());

    internal static void RequestStereo(long timestamp) =>
        Volatile.Write(ref _stereoRequestedAt, timestamp);

    internal static bool StereoWantedAt(long now)
    {
        long at = Volatile.Read(ref _stereoRequestedAt);
        return at != long.MinValue && Stopwatch.GetElapsedTime(at, now).TotalMilliseconds < StereoHoldMs;
    }

    /// <summary>Per-band history; SpecHist[frame*HistBands + band], frame 0 newest.</summary>
    public const int HistFrames = 16;
    public const int HistBands = 16;
    public static readonly float[] SpecHist = new float[HistFrames * HistBands];
    // u_specHist is uploaded as a vec4[]; 4 floats pack per vec4, so
    // HistFrames*HistBands must stay a multiple of 4 or the upload truncates.
    public const int SpecHistVec4Count = HistFrames * HistBands / 4;

    public static float BassPeak;
    public static float MidPeak;
    public static float HighPeak;

    public static void Reset()
    {
        Level = 0;
        Bass = 0;
        Mid = 0;
        High = 0;
        Beat = 0;
        Array.Clear(Spectrum);
        Array.Clear(Spectrum64);
        Array.Clear(SpectrumLeft64);
        Array.Clear(SpectrumRight64);
        Volatile.Write(ref _stereoRequestedAt, long.MinValue);
        Array.Clear(SpecHist);
        BassPeak = 0;
        MidPeak = 0;
        HighPeak = 0;
    }
}
