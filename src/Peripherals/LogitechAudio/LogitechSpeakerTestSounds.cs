using System;

namespace Nexus.Service.Peripherals.LogitechAudio;

/// <summary>
/// The sounds behind a speaker test, as mono float samples at the output's rate: a short
/// burst of pink noise for the speakers (the sound speaker setups use, the easiest to place
/// by ear, also through virtual surround) and a low "dong" for the subwoofer, which only
/// plays low frequencies.
/// </summary>
public static class LogitechSpeakerTestSounds
{
    private const float Peak = 0.35f;

    /// <summary>A low 55 Hz strike for the subwoofer, 0.9 s.</summary>
    public static float[] Dong(int rate)
    {
        var samples = new float[rate * 9 / 10];
        for (var i = 0; i < samples.Length; i++)
        {
            var t = (double)i / rate;
            samples[i] = (float)(Math.Sin(2 * Math.PI * 55 * t) * Math.Exp(-t / 0.4));
        }
        return Shape(samples, rate, fadeMs: 20);
    }

    /// <summary>One second of pink noise (Paul Kellet's economy filter on white noise).</summary>
    public static float[] PinkNoise(int rate)
    {
        var samples = new float[rate];
        var random = new Random(560);
        double b0 = 0, b1 = 0, b2 = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var white = random.NextDouble() * 2 - 1;
            b0 = 0.99765 * b0 + white * 0.0990460;
            b1 = 0.96300 * b1 + white * 0.2965164;
            b2 = 0.57000 * b2 + white * 1.0526913;
            samples[i] = (float)(b0 + b1 + b2 + white * 0.1848);
        }
        return Shape(samples, rate, fadeMs: 30);
    }

    // Brings the peak to the test level and fades both ends so nothing clicks.
    private static float[] Shape(float[] samples, int rate, int fadeMs)
    {
        var peak = 0f;
        foreach (var s in samples)
        {
            peak = Math.Max(peak, Math.Abs(s));
        }
        var gain = peak > 0 ? Peak / peak : 0;
        var attack = Math.Max(1, rate / 200);
        var tail = Math.Max(1, rate * fadeMs / 1000);
        for (var i = 0; i < samples.Length; i++)
        {
            var envelope = Math.Min(1f, Math.Min((float)i / attack, (float)(samples.Length - 1 - i) / tail));
            samples[i] *= gain * envelope;
        }
        return samples;
    }
}
