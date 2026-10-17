using System;
using System.Collections.Generic;
using Silk.NET.OpenGL;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>Uniform locations of one linked program for the values every effect receives.</summary>
internal sealed class UniformLocations
{
    public int Resolution = -1;
    public int Time = -1;
    public int Speed = -1;
    public int Intensity = -1;
    public int Hue = -1;
    public int Colorize = -1;
    public int Saturation = -1;
    public int Contrast = -1;
    public int AudioLevel = -1;
    public int AudioBass = -1;
    public int AudioMid = -1;
    public int AudioHigh = -1;
    public int AudioBeat = -1;
    public int AudioBoost = -1;
    public int Spectrum = -1;
    public int Spectrum64 = -1;
    public int SpecHist = -1;
    public int BassPeak = -1;
    public int MidPeak = -1;
    public int HighPeak = -1;

    public void Reset() =>
        Resolution =  Time =  Speed =  Intensity =  Hue =  Colorize =  Saturation =  Contrast =  AudioLevel =  AudioBass =  AudioMid =  AudioHigh =  AudioBeat =  AudioBoost =  Spectrum =  Spectrum64 =  SpecHist =  BassPeak =  MidPeak =  HighPeak = -1;

    public void Lookup(GL gl, uint prog)
    {
        Resolution = gl.GetUniformLocation(prog, "u_resolution");
        Time = gl.GetUniformLocation(prog, "u_time");
        Speed = gl.GetUniformLocation(prog, "u_speed");
        Intensity = gl.GetUniformLocation(prog, "u_intensity");
        Hue = gl.GetUniformLocation(prog, "u_hue");
        Colorize = gl.GetUniformLocation(prog, "u_colorize");
        Saturation = gl.GetUniformLocation(prog, "u_saturation");
        Contrast = gl.GetUniformLocation(prog, "u_contrast");
        AudioLevel = gl.GetUniformLocation(prog, "u_audioLevel");
        AudioBass = gl.GetUniformLocation(prog, "u_audioBass");
        AudioMid = gl.GetUniformLocation(prog, "u_audioMid");
        AudioHigh = gl.GetUniformLocation(prog, "u_audioHigh");
        AudioBeat = gl.GetUniformLocation(prog, "u_audioBeat");
        AudioBoost = gl.GetUniformLocation(prog, "u_audioBoost");
        Spectrum = gl.GetUniformLocation(prog, "u_spectrum[0]");
        if (Spectrum < 0)
        {
            Spectrum = gl.GetUniformLocation(prog, "u_spectrum");
        }
        Spectrum64 = gl.GetUniformLocation(prog, "u_spectrum64[0]");
        if (Spectrum64 < 0)
        {
            Spectrum64 = gl.GetUniformLocation(prog, "u_spectrum64");
        }
        SpecHist = gl.GetUniformLocation(prog, "u_specHist[0]");
        if (SpecHist < 0)
        {
            SpecHist = gl.GetUniformLocation(prog, "u_specHist");
        }
        BassPeak = gl.GetUniformLocation(prog, "u_bassPeak");
        MidPeak = gl.GetUniformLocation(prog, "u_midPeak");
        HighPeak = gl.GetUniformLocation(prog, "u_highPeak");
    }
}

public sealed partial class ShaderEffect
{
    /// <summary>The shader clock for a tick, as every render path pushes it.</summary>
    internal static float UTime(double tickMs) => (float)(tickMs % 86_400_000 / 1000.0);

    /// <summary>Pushes every per-render uniform this effect owns to the bound program, then the caller's hook runs. Shared by the canvas render and the LED-window render so both see the same values.</summary>
    private void PushUniforms(GL gl, UniformLocations u, Dictionary<string, int> cache, uint program, int w, int h, double tickMs)
    {
        if (u.Resolution >= 0)
        {
            gl.Uniform2(u.Resolution, (float)w, (float)h);
        }
        if (u.Time >= 0)
        {
            gl.Uniform1(u.Time, UTime(tickMs));
        }
        if (u.Speed >= 0)
        {
            gl.Uniform1(u.Speed, ClampBase("u_speed", Speed));
        }
        if (u.Intensity >= 0)
        {
            gl.Uniform1(u.Intensity, ClampBase("u_intensity", Intensity));
        }
        if (u.Hue >= 0)
        {
            gl.Uniform1(u.Hue, ClampBase("u_hue", Hue));
        }
        if (u.Colorize >= 0)
        {
            gl.Uniform1(u.Colorize, ClampBase("u_colorize", Colorize));
        }
        if (u.Saturation >= 0)
        {
            gl.Uniform1(u.Saturation, ClampBase("u_saturation", Saturation));
        }
        if (u.Contrast >= 0)
        {
            gl.Uniform1(u.Contrast, ClampBase("u_contrast", Contrast));
        }
        if (u.AudioLevel >= 0)
        {
            gl.Uniform1(u.AudioLevel, Nexus.Service.Lighting.Engine.AudioState.Level);
        }
        if (u.AudioBass >= 0)
        {
            gl.Uniform1(u.AudioBass, Nexus.Service.Lighting.Engine.AudioState.Bass);
        }
        if (u.AudioMid >= 0)
        {
            gl.Uniform1(u.AudioMid, Nexus.Service.Lighting.Engine.AudioState.Mid);
        }
        if (u.AudioHigh >= 0)
        {
            gl.Uniform1(u.AudioHigh, Nexus.Service.Lighting.Engine.AudioState.High);
        }
        if (u.AudioBeat >= 0)
        {
            gl.Uniform1(u.AudioBeat, Nexus.Service.Lighting.Engine.AudioState.Beat);
        }
        if (u.AudioBoost >= 0)
        {
            gl.Uniform1(u.AudioBoost, AudioBoost);
        }
        if (u.Spectrum >= 0)
        {
            unsafe
            {
                fixed (float* p = Nexus.Service.Lighting.Engine.AudioState.Spectrum)
                {
                    gl.Uniform1(u.Spectrum, (uint)Nexus.Service.Lighting.Engine.AudioState.SpectrumLength, p);
                }
            }
        }
        if (u.Spectrum64 >= 0)
        {
            unsafe
            {
                fixed (float* p = Nexus.Service.Lighting.Engine.AudioState.Spectrum64)
                {
                    gl.Uniform1(u.Spectrum64, (uint)Nexus.Service.Lighting.Engine.AudioState.Spectrum64Length, p);
                }
            }
        }
        if (u.SpecHist >= 0)
        {
            unsafe
            {
                fixed (float* p = Nexus.Service.Lighting.Engine.AudioState.SpecHist)
                {
                    gl.Uniform4(u.SpecHist, (uint)Nexus.Service.Lighting.Engine.AudioState.SpecHistVec4Count, p);
                }
            }
        }
        if (u.BassPeak >= 0)
        {
            gl.Uniform1(u.BassPeak, Nexus.Service.Lighting.Engine.AudioState.BassPeak);
        }
        if (u.MidPeak >= 0)
        {
            gl.Uniform1(u.MidPeak, Nexus.Service.Lighting.Engine.AudioState.MidPeak);
        }
        if (u.HighPeak >= 0)
        {
            gl.Uniform1(u.HighPeak, Nexus.Service.Lighting.Engine.AudioState.HighPeak);
        }
        if (ExtraParams is not null)
        {
            foreach (var kv in ExtraParams)
            {
                int loc;
                if (!cache.TryGetValue(kv.Key, out loc))
                {
                    loc = gl.GetUniformLocation(program, kv.Key);
                    cache[kv.Key] = loc;
                }
                if (loc >= 0)
                {
                    var value = Specs.TryGetValue(kv.Key, out var spec) ? ShaderParamSpec.Clamp(spec, kv.Value) : kv.Value;
                    gl.Uniform1(loc, value);
                }
            }
        }
        // A declared, annotated uniform the caller never sent (a preset
        // predating the param) would otherwise stay at GL's post-link 0,
        // not the shader's authored default.
        foreach (var kv in Specs)
        {
            if (!kv.Value.Declared || BaseParamNames.Contains(kv.Key)
                || (ExtraParams is not null && ExtraParams.ContainsKey(kv.Key)))
            {
                continue;
            }
            int loc;
            if (!cache.TryGetValue(kv.Key, out loc))
            {
                loc = gl.GetUniformLocation(program, kv.Key);
                cache[kv.Key] = loc;
            }
            if (loc >= 0)
            {
                gl.Uniform1(loc, kv.Value.Default);
            }
        }
    }
}
