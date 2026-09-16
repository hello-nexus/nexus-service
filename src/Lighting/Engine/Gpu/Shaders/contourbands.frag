uniform float u_bands; // hint_range(1.0, 12.0, 1.0) = 4.0  contour count
uniform float u_scale; // hint_range(0.6, 6.0, 0.05) = 2.2  blob size
uniform float u_flow; // hint_range(0.0, 3.0, 0.05) = 1.0  drift rate

// Two-tone topographic bands: one warped noise field sliced by a sine so the
// duty stays even and the seams never snap at a fract() wrap. Three-octave
// noise both for the warp and the field keeps this in the medium tier while
// still reading as organic. Ships desaturated by default for the ink look;
// the saturation slider is what makes it colour.
void main() {
    float ar = u_resolution.x / u_resolution.y;
    vec2 uv = uv01();
    float t = mod(u_time * u_speed * 0.09 * clamp(u_flow, 0.0, 3.0), 1000.0);
    vec2 p = vec2(uv.x * ar, uv.y) * clamp(u_scale, 0.6, 6.0);

    float w = fbm3(p * 0.7 + vec2(t * 0.6, -t * 0.35));
    vec2 warped = p + vec2(w * 1.7, w * 1.2) + vec2(0.0, t * 0.5);
    float n = fbm3(warped);

    float bands = clamp(u_bands, 1.0, 12.0);
    float s = sin(n * bands * 6.28318);
    float m = smoothstep(-0.22, 0.22, s);

    vec3 dark = vec3(0.03, 0.03, 0.04);
    vec3 lite = tintedPalette(n * 0.9 + t * 0.05) * 1.15;
    vec3 col = mix(dark, lite, m);
    // Thin rim on the band edge; keeps the shape legible once the bands are
    // tinted and the two tones sit close in luma.
    col += vec3(0.10) * smoothstep(0.30, 0.0, abs(s));

    fragColor = vec4(finalize(col), 1.0);
}
