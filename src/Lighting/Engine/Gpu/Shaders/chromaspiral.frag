uniform float u_tightness; // hint_range(1.0, 12.0, 0.1) = 5.0  spiral winding
uniform float u_spin; // hint_range(-3.0, 3.0, 0.05) = 1.0  rotation rate
uniform float u_bands; // hint_range(1.0, 12.0, 1.0) = 3.0  rainbow band count

// Log-polar rainbow spiral. Bands of the palette repeat along a spiral
// axis (angle + log(radius)) so the hue sweeps from the outside in. Very
// eye-catching, reads as a hypnotic whirlpool at higher tightness.
void main() {
    vec2 uv = uvCentered();
    float t = u_time * u_speed * 0.4;
    float tight = max(0.3, u_tightness);
    float spin = u_spin;
    float bands = max(1.0, u_bands);

    float r = length(uv);
    float a = atan(uv.y, uv.x) + t * spin;
    float phase = a + tight * log(max(r, 0.001)) * 0.8;
    float band = fract(phase / (6.28318530718 / bands) + t * 0.18);

    vec3 col = tintedPalette(band);
    col *= smoothstep(0.0, 0.18, r);
    col *= 1.0 - smoothstep(0.85, 1.25, r) * 0.45;
    col *= 1.1 + 0.35 * cos(phase * 2.0);

    fragColor = vec4(finalize(col), 1.0);
}
