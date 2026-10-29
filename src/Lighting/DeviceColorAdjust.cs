using System;
using System.Collections.Generic;
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
    // Index into OrderNames; 0 is RGB (no swizzle).
    private readonly byte _order;
    /// <summary>False for the default-constructed value, which is why the flag
    ///  is stored this way round: `default(DeviceColorAdjust)` must read as a
    ///  no-op, not as three zeroed gains that would drive the strip black.</summary>
    public readonly bool HasEffect;

    /// <summary>True when this correction is a no-op, so callers can keep their untouched fast path.</summary>
    public bool IsIdentity => !HasEffect;

    public static readonly DeviceColorAdjust Identity = default;

    /// <summary>Channel orders a device can expect on the wire, index 0 being identity.</summary>
    private static readonly string[] OrderNameArray = { "RGB", "RBG", "GRB", "GBR", "BRG", "BGR" };

    /// <summary>The accepted order names, read-only.</summary>
    public static IReadOnlyList<string> OrderNames { get; } = Array.AsReadOnly(OrderNameArray);

    /// <summary>Canonical form of a stored or requested order, or null when it is not one of <see cref="OrderNames"/>. Null and empty read as RGB.</summary>
    public static string? NormalizeOrder(string? order)
    {
        if (string.IsNullOrEmpty(order))
        {
            return OrderNameArray[0];
        }
        foreach (var name in OrderNameArray)
        {
            if (string.Equals(name, order, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }
        return null;
    }

    private static byte OrderIndex(string? order)
    {
        var canonical = NormalizeOrder(order);
        for (var i = 0; i < OrderNameArray.Length; i++)
        {
            if (OrderNameArray[i] == canonical)
            {
                return (byte)i;
            }
        }
        return 0;
    }

    private DeviceColorAdjust(float r, float g, float b, float saturation, byte order)
    {
        _order = order;
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
    public static DeviceColorAdjust Create(float red, float green, float blue, float temperature, float saturation, string? colorOrder = null)
    {
        var order = OrderIndex(colorOrder);
        var r = Math.Clamp(red, MinChannel, MaxChannel);
        var g = Math.Clamp(green, MinChannel, MaxChannel);
        var b = Math.Clamp(blue, MinChannel, MaxChannel);
        var t = Math.Clamp(temperature, -1f, 1f);
        var s = Math.Clamp(saturation, MinSaturation, MaxSaturation);
        r *= 1f + TemperatureSpan * t;
        b *= 1f - TemperatureSpan * t;
        if (r == 1f && g == 1f && b == 1f && s == 1f && order == 0)
        {
            return Identity;
        }
        return new DeviceColorAdjust(r, g, b, s, order);
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
                && pref.AdjustSaturation == 1f
                && OrderIndex(pref.ColorOrder) == 0))
        {
            return Identity;
        }
        return Create(pref.AdjustRed, pref.AdjustGreen, pref.AdjustBlue, pref.AdjustTemperature, pref.AdjustSaturation, pref.ColorOrder);
    }

    /// <summary>
    /// Apply saturation, then the channel gains, then <paramref name="mul"/>
    /// (the brightness multiplier the caller already computed), clamping to
    /// byte range. Saturation runs first so a channel trim shifts the colour
    /// the user actually sees on the strip. The color order runs last: output
    /// byte i is the input channel named by letter i of the order.
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
        var cr = ToByte(r * _r * m);
        var cg = ToByte(g * _g * m);
        var cb = ToByte(b * _b * m);
        switch (_order)
        {
            case 1: or = cr; og = cb; ob = cg; break;
            case 2: or = cg; og = cr; ob = cb; break;
            case 3: or = cg; og = cb; ob = cr; break;
            case 4: or = cb; og = cr; ob = cg; break;
            case 5: or = cb; og = cg; ob = cr; break;
            default: or = cr; og = cg; ob = cb; break;
        }
    }

    /// <summary>
    /// Channel order only, for paths that send a color to firmware without the
    /// trims (firmware effect palettes). Same permutation as <see cref="Apply"/>.
    /// </summary>
    public static void ReorderChannels(string? order, ref byte r, ref byte g, ref byte b)
    {
        byte cr = r, cg = g, cb = b;
        switch (OrderIndex(order))
        {
            case 1: g = cb; b = cg; break;
            case 2: r = cg; g = cr; break;
            case 3: r = cg; g = cb; b = cr; break;
            case 4: r = cb; g = cr; b = cg; break;
            case 5: r = cb; b = cr; break;
            default: break;
        }
    }

    /// <summary>The stored color order for one device id, or null for RGB or no entry.</summary>
    public static string? OrderOf(Dictionary<string, LightingDevicePreference> prefs, string id)
    {
        try
        {
            return prefs.TryGetValue(id, out var pref) && pref is not null ? pref.ColorOrder : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static byte ToByte(float v) => (byte)Math.Clamp(v, 0f, 255f);
}
