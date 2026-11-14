using System;
using System.Collections.Generic;

namespace Nexus.Service.Lighting.Engine;

/// <summary>sRGB to linear light and back, as lookup tables.</summary>
internal static class AreaLight
{
    public const int LinMax = 65535;
    public static readonly ushort[] ToLinear = BuildToLinear();
    public static readonly byte[] ToSrgb = BuildToSrgb();

    private static ushort[] BuildToLinear()
    {
        var t = new ushort[256];
        for (var i = 0; i < 256; i++)
        {
            var c = i / 255.0;
            var lin = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            t[i] = (ushort)Math.Round(lin * LinMax);
        }
        return t;
    }

    private static byte[] BuildToSrgb()
    {
        var t = new byte[LinMax + 1];
        for (var i = 0; i <= LinMax; i++)
        {
            var lin = i / (double)LinMax;
            var c = lin <= 0.0031308 ? lin * 12.92 : 1.055 * Math.Pow(lin, 1.0 / 2.4) - 0.055;
            t[i] = (byte)Math.Clamp((int)Math.Round(c * 255.0), 0, 255);
        }
        return t;
    }
}

/// <summary>How a card's LED combines the canvas pixels it reads into one color, independent of <see cref="SamplingMode"/>.</summary>
public enum ColorSampling
{
    /// <summary>Plain truncating sRGB byte mean.</summary>
    Standard = 0,
    /// <summary>Linear-light mean converted back to sRGB.</summary>
    Vivid = 1,
    /// <summary>sRGB mean over the pixels brighter than <see cref="ColorSamplings.NearBlackMax"/>.</summary>
    IgnoreBlack = 2,
}

/// <summary>The wire names of <see cref="ColorSampling"/>.</summary>
public static class ColorSamplings
{
    public const string Standard = "standard";
    public const string Vivid = "vivid";
    public const string IgnoreBlack = "ignore-black";

    /// <summary>Highest max channel a pixel can have and still count as near-black.</summary>
    public const int NearBlackMax = 8;

    public static bool TryParse(string? name, out ColorSampling mode)
    {
        switch (name)
        {
            case Standard: mode = ColorSampling.Standard; return true;
            case Vivid: mode = ColorSampling.Vivid; return true;
            case IgnoreBlack: mode = ColorSampling.IgnoreBlack; return true;
            default: mode = ColorSampling.Standard; return false;
        }
    }

    public static string Name(ColorSampling mode) => mode switch
    {
        ColorSampling.Vivid => Vivid,
        ColorSampling.IgnoreBlack => IgnoreBlack,
        _ => Standard,
    };

    /// <summary>The mode stored for a card; <c>standard</c> when none is, or the stored name is unknown.</summary>
    public static ColorSampling Of(IReadOnlyDictionary<string, string>? modes, string id) =>
        modes is not null && modes.TryGetValue(id, out var name) && TryParse(name, out var m) ? m : ColorSampling.Standard;
}

/// <summary>The one combiner every canvas read (spot, box, area cell) feeds, so the modes cannot drift between reads. Allocation-free.</summary>
internal struct ColorAccumulator
{
    private readonly ColorSampling _mode;
    private long _n, _r, _g, _b;
    private long _kn, _kr, _kg, _kb;

    public ColorAccumulator(ColorSampling mode)
    {
        _mode = mode;
        _n = _r = _g = _b = 0;
        _kn = _kr = _kg = _kb = 0;
    }

    public void Add(byte r, byte g, byte b)
    {
        _n++;
        switch (_mode)
        {
            case ColorSampling.Vivid:
                var lin = AreaLight.ToLinear;
                _r += lin[r];
                _g += lin[g];
                _b += lin[b];
                break;
            case ColorSampling.IgnoreBlack:
                _r += r;
                _g += g;
                _b += b;
                if (Math.Max(r, Math.Max(g, b)) > ColorSamplings.NearBlackMax)
                {
                    _kn++;
                    _kr += r;
                    _kg += g;
                    _kb += b;
                }
                break;
            default:
                _r += r;
                _g += g;
                _b += b;
                break;
        }
    }

    /// <summary>The combined color; black when nothing was added.</summary>
    public readonly (byte r, byte g, byte b) Result()
    {
        if (_n == 0)
        {
            return (0, 0, 0);
        }
        switch (_mode)
        {
            case ColorSampling.Vivid:
                var srgb = AreaLight.ToSrgb;
                var half = _n / 2;
                return (
                    srgb[(int)Math.Min((_r + half) / _n, AreaLight.LinMax)],
                    srgb[(int)Math.Min((_g + half) / _n, AreaLight.LinMax)],
                    srgb[(int)Math.Min((_b + half) / _n, AreaLight.LinMax)]);
            case ColorSampling.IgnoreBlack when _kn > 0:
                return ((byte)(_kr / _kn), (byte)(_kg / _kn), (byte)(_kb / _kn));
            default:
                return ((byte)(_r / _n), (byte)(_g / _n), (byte)(_b / _n));
        }
    }
}
