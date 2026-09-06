using System;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Per-device colour correction, resolved once per frame and applied by every
/// frame writer right where the per-device brightness multiplier already is.
/// Calibration only: two strips rendering the same canvas pixel can look
/// visibly different (LED bin, diffuser, sleeve), so the user trims each
/// device's channels until they match.
///
/// The struct is built from the stored preference and carries precomputed
/// channel gains so the hot loop does no dictionary or trig work. A device
/// with nothing set resolves to <see cref="IsIdentity"/>, and every writer
/// keeps its existing fast path for that case - unconfigured devices pay
/// nothing beyond one struct construction per frame.
/// </summary>
public readonly struct DeviceColorAdjust
{
    /// <summary>Per-channel gain bounds, matching the slider range the UI offers.</summary>
    public const float MinChannel = 0.3f;
    public const float MaxChannel = 1.7f;
    /// <summary>Saturation multiplier bounds, from greyscale to doubled.</summary>
    public const float MinSaturation = 0f;
    public const float MaxSaturation = 2f;
    /// <summary>How far a full-scale temperature shift moves the red/blue gains.</summary>
    private const float TemperatureSpan = 0.35f;

    // Effective gains: the channel sliders with the warm/cool shift folded in,
    // plus the saturation multiplier around Rec.709 luma. Private because they
    // read zero on Identity, where nothing consults them - a caller reaching
    // for them directly would drive an untuned device black.
    private readonly float _r;
    private readonly float _g;
    private readonly float _b;
    private readonly float _saturation;
    /// <summary>False for the default-constructed value, which is why the flag
    ///  is stored this way round: `default(DeviceColorAdjust)` must read as a
    ///  no-op, not as three zeroed gains that would drive the strip black.</summary>
    public readonly bool HasEffect;

    /// <summary>True when this correction is a no-op, so callers can keep their untouched fast path.</summary>
    public bool IsIdentity => !HasEffect;

    public static readonly DeviceColorAdjust Identity = default;

    private DeviceColorAdjust(float r, float g, float b, float saturation)
    {
        _r = r;
        _g = g;
        _b = b;
        _saturation = saturation;
        HasEffect = true;
    }

    /// <summary>
    /// Build the correction from raw slider values. Temperature is -1 (cool,
    /// blue-shifted) .. +1 (warm, amber-shifted) and folds into the red and
    /// blue gains rather than being a separate pass - a colour temperature
    /// trim IS a red/blue balance, and folding it keeps the hot loop to three
    /// multiplies.
    /// </summary>
    public static DeviceColorAdjust Create(float red, float green, float blue, float temperature, float saturation)
    {
        var r = Math.Clamp(red, MinChannel, MaxChannel);
        var g = Math.Clamp(green, MinChannel, MaxChannel);
        var b = Math.Clamp(blue, MinChannel, MaxChannel);
        var t = Math.Clamp(temperature, -1f, 1f);
        var s = Math.Clamp(saturation, MinSaturation, MaxSaturation);
        r *= 1f + TemperatureSpan * t;
        b *= 1f - TemperatureSpan * t;
        if (r == 1f && g == 1f && b == 1f && s == 1f)
        {
            return Identity;
        }
        return new DeviceColorAdjust(r, g, b, s);
    }

    /// <summary>
    /// Resolve one card's correction from the preference the caller already
    /// looked up for its brightness. Takes the object rather than the id on
    /// purpose: the frame writers do one dictionary lookup per device per
    /// frame, and colour tuning must not add a second one. An untouched
    /// device early-outs here, so the caller's per-LED loop stays on its
    /// original path.
    /// </summary>
    public static DeviceColorAdjust For(LightingDevicePreference? pref)
    {
        if (pref is null
            || (pref.AdjustRed == 1f
                && pref.AdjustGreen == 1f
                && pref.AdjustBlue == 1f
                && pref.AdjustTemperature == 0f
                && pref.AdjustSaturation == 1f))
        {
            return Identity;
        }
        return Create(pref.AdjustRed, pref.AdjustGreen, pref.AdjustBlue, pref.AdjustTemperature, pref.AdjustSaturation);
    }

    /// <summary>
    /// Apply saturation, then the channel gains, then <paramref name="mul"/>
    /// (the brightness multiplier the caller already computed), clamping to
    /// byte range. Saturation runs first so a channel trim shifts the colour
    /// the user actually sees on the strip.
    /// </summary>
    public void Apply(byte sr, byte sg, byte sb, double mul, out byte or, out byte og, out byte ob)
    {
        if (!HasEffect)
        {
            or = ToByte(sr * (float)mul);
            og = ToByte(sg * (float)mul);
            ob = ToByte(sb * (float)mul);
            return;
        }
        float r = sr;
        float g = sg;
        float b = sb;
        if (_saturation != 1f)
        {
            var luma = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            r = luma + (r - luma) * _saturation;
            g = luma + (g - luma) * _saturation;
            b = luma + (b - luma) * _saturation;
        }
        var m = (float)mul;
        or = ToByte(r * _r * m);
        og = ToByte(g * _g * m);
        ob = ToByte(b * _b * m);
    }

    private static byte ToByte(float v) => (byte)Math.Clamp(v, 0f, 255f);
}
