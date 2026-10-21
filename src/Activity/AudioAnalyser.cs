using System;
using Nexus.Service.Lighting.Engine;

namespace Nexus.Service.Activity;

/// <summary>
/// Shared beat detection + FFT-based spectrum analysis used by every audio
/// capture source (<see cref="BeatsProvider"/> on macOS / Linux via ffmpeg,
/// <see cref="WasapiLoopbackBeatsProvider"/> on Windows via WASAPI loopback).
///
/// Call <see cref="Analyse"/> with a mono float32 window of exactly
/// <see cref="WindowSize"/> samples at <see cref="SampleRate"/>. It publishes
/// audio features (level / bass / mid / high / beat / 16-band spectrum) to
/// <see cref="AudioState"/> so shaders can read them as uniforms.
///
/// State (rolling averages, smoothed spectrum, beat envelope) is kept on this
/// instance. One analyser per capture source - they should not be shared
/// across threads without external locking.
/// </summary>
public sealed class AudioAnalyser
{
    public const int SampleRate = 44100;
    public const int WindowSize = 2048;
    private const float BeatThreshold = 1.4f;

    private const int FftSize = 512;
    private const int FftBits = 9;
    private const int FftBins = FftSize / 2;

    private static readonly int[] _bandEdges =
    {
        1, 3, 5, 7, 10, 14, 19, 26, 34, 45, 59, 76, 97, 125, 156, 196, FftBins,
    };

    private static readonly int[] _bandEdges64 = new int[65];

    private static readonly float[] _cosTable = new float[FftSize];
    private static readonly float[] _sinTable = new float[FftSize];
    private static readonly int[] _bitReverse = new int[FftSize];
    private static readonly float[] _hannWindow = new float[FftSize];

    static AudioAnalyser()
    {
        for (int i = 0; i < FftSize; i++)
        {
            float ang = -2f * MathF.PI * i / FftSize;
            _cosTable[i] = MathF.Cos(ang);
            _sinTable[i] = MathF.Sin(ang);
            int j = 0, k = i;
            for (int b = 0; b < FftBits; b++)
            {
                j = (j << 1) | (k & 1);
                k >>= 1;
            }
            _bitReverse[i] = j;
            _hannWindow[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (FftSize - 1)));
        }

        int prev = 1;
        for (int i = 0; i <= 64; i++)
        {
            int e = (int)MathF.Round(MathF.Pow(FftBins, i / 64f));
            if (e <= prev)
            {
                e = prev + (i == 0 ? 0 : 1);
            }
            if (e > FftBins)
            {
                e = FftBins;
            }
            _bandEdges64[i] = e;
            prev = e;
        }
        _bandEdges64[0] = 1;
        _bandEdges64[64] = FftBins;
    }

    // Rolling exponential average of bass energy; drives the time-domain
    // bass-beat detector that feeds AudioState.Beat.
    private float _avgBass;

    private readonly float[] _fftRe = new float[FftSize];
    private readonly float[] _fftIm = new float[FftSize];
    private readonly float[] _smoothSpectrum = new float[16];
    private readonly float[] _smoothSpectrum64 = new float[64];
    private readonly float[] _smoothLeft64 = new float[64];
    private readonly float[] _smoothRight64 = new float[64];
    private float _smoothLevel;
    private float _smoothBass;
    private float _smoothMid;
    private float _smoothHigh;
    private float _beatEnvelope;

    /// <summary>
    /// Analyse one window of mono float32 samples.
    /// </summary>
    /// <param name="samples">Mono float32 PCM, length must equal <see cref="WindowSize"/>.</param>
    /// <param name="externalPeak">
    /// Optional post-system-volume peak in [0, 1]. Windows passes the WASAPI
    /// IAudioMeterInformation peak so volume tracks the same calibrated meter
    /// the OS shows the user (and survives loopback going silent). When null,
    /// volume falls back to the in-window peak of <paramref name="samples"/>.
    /// </param>
    /// <param name="left">
    /// Optional left-channel window, same length as <paramref name="samples"/>.
    /// Analysed only while <see cref="AudioState.StereoWanted"/>.
    /// </param>
    /// <param name="right">Optional right-channel window, paired with <paramref name="left"/>.</param>
    public void Analyse(float[] samples, float? externalPeak = null, float[]? left = null, float[]? right = null)
    {
        float bassEnergy = 0;
        float internalPeak = 0;

        for (int i = 1; i < samples.Length; i++)
        {
            float s = samples[i];
            float prev = samples[i - 1];
            float abs = MathF.Abs(s);
            float delta = MathF.Abs(s - prev);
            if (abs > internalPeak) internalPeak = abs;
            if (abs > 0.05f && delta < abs * 0.5f)
            {
                bassEnergy += abs;
            }
        }

        float n = samples.Length;
        bassEnergy /= n;

        const float alpha = 0.1f;
        _avgBass = _avgBass * (1 - alpha) + bassEnergy * alpha;

        bool bassBeat = _avgBass > 0.0001f && bassEnergy / _avgBass > BeatThreshold;

        // Calibrated peak, then perceptual curve so quiet content still moves
        // shaders and loud content doesn't pin at 1.0. The 1.003 multiplier
        // makes the curve saturate to 1 when peak == 1.
        float peak = externalPeak ?? internalPeak;
        if (peak < 0f) peak = 0f;
        else if (peak > 1f) peak = 1f;
        float volume = peak <= 0f ? 0f : MathF.Min(MathF.Pow(peak, 0.15f) * 1.003f, 1f);

        PublishAudioState(samples, volume, bassBeat);

        if (left is not null && right is not null && AudioState.StereoWanted)
        {
            PublishSpectrum64(left, _smoothLeft64, AudioState.SpectrumLeft64);
            PublishSpectrum64(right, _smoothRight64, AudioState.SpectrumRight64);
        }
    }

    public void Reset()
    {
        _avgBass = 0;
        _smoothLevel = _smoothBass = _smoothMid = _smoothHigh = _beatEnvelope = 0;
        Array.Clear(_smoothSpectrum);
        Array.Clear(_smoothSpectrum64);
        Array.Clear(_smoothLeft64);
        Array.Clear(_smoothRight64);
        AudioState.Reset();
    }

    private void PublishAudioState(float[] samples, float volume, bool bassBeat)
    {
        for (int i = 0; i < FftSize; i++)
        {
            _fftRe[i] = samples[i] * _hannWindow[i];
            _fftIm[i] = 0f;
        }
        Fft(_fftRe, _fftIm);

        Span<float> raw = stackalloc float[16];
        for (int b = 0; b < 16; b++)
        {
            int start = _bandEdges[b];
            int end = _bandEdges[b + 1];
            float sum = 0f;
            for (int k = start; k < end; k++)
            {
                float re = _fftRe[k];
                float im = _fftIm[k];
                sum += MathF.Sqrt(re * re + im * im);
            }
            float avg = sum / (end - start);
            // log1p compression knees around avg=5, matching the FFT-magnitude
            // range of normal-volume music. The x3 gain lifts bands into the
            // 0.3+ range the spectrum shaders respond to.
            float norm = MathF.Log10(1f + avg * 3f);
            raw[b] = MathF.Min(norm, 1f);
        }

        for (int b = 0; b < 16; b++)
        {
            float cur = _smoothSpectrum[b];
            float next = raw[b] > cur
                ? cur + (raw[b] - cur) * 0.55f
                : cur + (raw[b] - cur) * 0.18f;
            _smoothSpectrum[b] = next;
            AudioState.Spectrum[b] = next;
        }

        for (int b = 0; b < 64; b++)
        {
            int start = _bandEdges64[b];
            int end = _bandEdges64[b + 1];
            int width = Math.Max(end - start, 1);
            float sum64 = 0f;
            for (int k = start; k < start + width; k++)
            {
                float re = _fftRe[k];
                float im = _fftIm[k];
                sum64 += MathF.Sqrt(re * re + im * im);
            }
            float avg64 = sum64 / width;
            float norm64 = MathF.Min(MathF.Log10(1f + avg64 * 3f), 1f);
            float cur64 = _smoothSpectrum64[b];
            _smoothSpectrum64[b] = norm64 > cur64
                ? cur64 + (norm64 - cur64) * 0.55f
                : cur64 + (norm64 - cur64) * 0.18f;
            AudioState.Spectrum64[b] = _smoothSpectrum64[b];
        }

        float bassAgg = (_smoothSpectrum[0] + _smoothSpectrum[1] + _smoothSpectrum[2]) / 3f;
        float midAgg = (_smoothSpectrum[3] + _smoothSpectrum[4] + _smoothSpectrum[5] +
                        _smoothSpectrum[6] + _smoothSpectrum[7] + _smoothSpectrum[8]) / 6f;
        float highAgg = (_smoothSpectrum[9] + _smoothSpectrum[10] + _smoothSpectrum[11] +
                         _smoothSpectrum[12] + _smoothSpectrum[13] + _smoothSpectrum[14] +
                         _smoothSpectrum[15]) / 7f;

        _smoothLevel = _smoothLevel + (volume - _smoothLevel) * 0.25f;
        _smoothBass = _smoothBass + (bassAgg - _smoothBass) * 0.35f;
        _smoothMid = _smoothMid + (midAgg - _smoothMid) * 0.3f;
        _smoothHigh = _smoothHigh + (highAgg - _smoothHigh) * 0.3f;

        _beatEnvelope *= 0.78f;
        if (bassBeat)
        {
            _beatEnvelope = 1f;
        }

        AudioState.Level = _smoothLevel;
        AudioState.Bass = _smoothBass;
        AudioState.Mid = _smoothMid;
        AudioState.High = _smoothHigh;
        AudioState.Beat = _beatEnvelope;

        AudioState.BassPeak = MathF.Max(AudioState.BassPeak * 0.90f, AudioState.Bass);
        AudioState.MidPeak = MathF.Max(AudioState.MidPeak * 0.90f, AudioState.Mid);
        AudioState.HighPeak = MathF.Max(AudioState.HighPeak * 0.90f, AudioState.High);

        Array.Copy(AudioState.SpecHist, 0, AudioState.SpecHist, AudioState.HistBands,
                   (AudioState.HistFrames - 1) * AudioState.HistBands);
        for (int b = 0; b < 16; b++)
        {
            AudioState.SpecHist[b] = _smoothSpectrum[b];
        }
    }

    private void PublishSpectrum64(float[] samples, float[] smooth, float[] output)
    {
        for (int i = 0; i < FftSize; i++)
        {
            _fftRe[i] = samples[i] * _hannWindow[i];
            _fftIm[i] = 0f;
        }
        Fft(_fftRe, _fftIm);

        for (int b = 0; b < 64; b++)
        {
            int start = _bandEdges64[b];
            int width = Math.Max(_bandEdges64[b + 1] - start, 1);
            float sum = 0f;
            for (int k = start; k < start + width; k++)
            {
                float re = _fftRe[k];
                float im = _fftIm[k];
                sum += MathF.Sqrt(re * re + im * im);
            }
            float norm = MathF.Min(MathF.Log10(1f + sum / width * 3f), 1f);
            float cur = smooth[b];
            smooth[b] = norm > cur
                ? cur + (norm - cur) * 0.55f
                : cur + (norm - cur) * 0.18f;
            output[b] = smooth[b];
        }
    }

    private static void Fft(float[] re, float[] im)
    {
        for (int i = 0; i < FftSize; i++)
        {
            int j = _bitReverse[i];
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= FftSize; len <<= 1)
        {
            int half = len >> 1;
            int step = FftSize / len;
            for (int i = 0; i < FftSize; i += len)
            {
                int twiddleIdx = 0;
                for (int k = 0; k < half; k++)
                {
                    int idx = i + k;
                    int idx2 = idx + half;
                    float wRe = _cosTable[twiddleIdx];
                    float wIm = _sinTable[twiddleIdx];
                    float tRe = re[idx2] * wRe - im[idx2] * wIm;
                    float tIm = re[idx2] * wIm + im[idx2] * wRe;
                    re[idx2] = re[idx] - tRe;
                    im[idx2] = im[idx] - tIm;
                    re[idx] += tRe;
                    im[idx] += tIm;
                    twiddleIdx += step;
                }
            }
        }
    }
}
