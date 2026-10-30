using System;

namespace Nexus.Service.Lighting.Engine;

/// <summary>
/// Shared, mutable holder for the four canvas post-process params used by
/// Screen Mirror and Media modes. The effect keeps a reference; the endpoint
/// mutates the fields directly. Float reads are atomic on the target platforms
/// so the hot render loop can sample the latest values without locking.
/// Defaults are identity (no shift, full saturation, linear contrast) so an
/// unconfigured effect renders the source frame unchanged.
/// </summary>
public sealed class PostProcessState
{
    public float Hue;
    public float Colorize;
    public float Saturation = 1f;
    public float Contrast = 1f;
    public bool FlipX;
    public bool FlipY;
    public bool Reactive;
    public float Reactivity = 0.5f;
    public float Intensity = 0.5f;

    public void Set(float hue, float colorize, float saturation, float contrast, bool flipX = false, bool flipY = false, bool reactive = false, float reactivity = 0.5f, float intensity = 0.5f)
    {
        Hue = hue;
        Colorize = colorize;
        Saturation = saturation;
        Contrast = contrast;
        FlipX = flipX;
        FlipY = flipY;
        Reactive = reactive;
        Reactivity = reactivity;
        Intensity = intensity;
    }

    public bool IsIdentity()
        => Hue == 0f && Colorize == 0f && Saturation == 1f && Contrast == 1f;

    public bool IsFlipIdentity() => !FlipX && !FlipY;
}

/// <summary>
/// CPU port of the colour-manipulation slice of the shader `finalize()` in
/// _prelude.frag. Applies hue rotation, colorize (blend toward luma-tinted
/// accent), saturation, and contrast to an RGB888 byte buffer in-place.
///
/// NOTE: deliberately omits the soft-knee tonemap (each channel divided by one
/// plus the brightest channel) and per-effect output gain that the shader uses. Animate shaders emit HDR-ish values (e.g. `finalize(col * 2.2)` in
/// starfield) where the tonemap is the mapping step back into display range.
/// Screen Mirror and Media frames arrive already in sRGB [0, 1]; running them
/// through the same tonemap would crush everything - pure white (1.0) becomes
/// 0.5, visibly darkening the frame the instant any non-identity param is set.
/// We instead clamp at the byte conversion so out-of-range values from
/// saturation/contrast boosts saturate correctly without losing the identity
/// round-trip.
/// </summary>
public static class RgbPostProcess
{
    public static void Apply(Span<byte> rgb, PostProcessState s)
    {
        if (s.IsIdentity())
            return;
        var hue = s.Hue;
        var colorize = Math.Clamp(s.Colorize, 0f, 1f);
        var saturation = Math.Clamp(s.Saturation, 0f, 4f);
        var contrast = Math.Clamp(s.Contrast, 0f, 4f);
        HsvToRgb(hue, 1f, 1f, out var tintR, out var tintG, out var tintB);
        for (int i = 0; i + 2 < rgb.Length; i += 3)
        {
            float r = rgb[i] / 255f;
            float g = rgb[i + 1] / 255f;
            float b = rgb[i + 2] / 255f;

            float luma = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            float cr = luma * tintR * 1.4f;
            float cg = luma * tintG * 1.4f;
            float cb = luma * tintB * 1.4f;
            r = Lerp(r, cr, colorize);
            g = Lerp(g, cg, colorize);
            b = Lerp(b, cb, colorize);
            float bl = 0.2126f * Math.Max(r, 0) + 0.7152f * Math.Max(g, 0) + 0.0722f * Math.Max(b, 0);
            r = Lerp(bl, r, saturation);
            g = Lerp(bl, g, saturation);
            b = Lerp(bl, b, saturation);
            r = (r - 0.5f) * contrast + 0.5f;
            g = (g - 0.5f) * contrast + 0.5f;
            b = (b - 0.5f) * contrast + 0.5f;

            rgb[i] = (byte)Math.Clamp(r * 255f + 0.5f, 0f, 255f);
            rgb[i + 1] = (byte)Math.Clamp(g * 255f + 0.5f, 0f, 255f);
            rgb[i + 2] = (byte)Math.Clamp(b * 255f + 0.5f, 0f, 255f);
        }
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static void HsvToRgb(float h, float s, float v, out float r, out float g, out float b)
    {
        h = h - (float)Math.Floor(h);
        float c = v * s;
        float hp = h * 6f;
        float x = c * (1f - Math.Abs((hp % 2f) - 1f));
        float m = v - c;
        float r1 = 0f, g1 = 0f, b1 = 0f;
        if (hp < 1f)
        { r1 = c; g1 = x; }
        else if (hp < 2f)
        { r1 = x; g1 = c; }
        else if (hp < 3f)
        { g1 = c; b1 = x; }
        else if (hp < 4f)
        { g1 = x; b1 = c; }
        else if (hp < 5f)
        { r1 = x; b1 = c; }
        else
        { r1 = c; b1 = x; }
        r = r1 + m;
        g = g1 + m;
        b = b1 + m;
    }
}
