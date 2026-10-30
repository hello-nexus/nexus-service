uniform float u_layers; // hint_range(2.0, 8.0, 1.0) = 5.0  nested shape count
uniform float u_edge; // hint_range(0.2, 1.2, 0.02) = 0.6  edge glow width
uniform float u_pulse; // hint_range(0.3, 3.0, 0.05) = 1.0  pulsation rate
uniform float u_mood; // hint_range(0.0, 5.0, 1.0) = 0.0  palette mood: 0 full spectrum, 1 warm, 2 cool, 3 neon, 4 sunset, 5 aurora
#define tintedPalette(t) moodPalette((t), u_mood)

// Filled mandala. The plane is folded into a mirrored wedge, then cut into
// concentric bands that drift outward; every band is a full color field with
// its own petal count and rotation direction, and a bright line marks each
// band edge. Bands continue past the frame corners, so nothing is left empty.
void main() {
    vec2 uv = uvCentered();
    float t = u_time * u_speed * 0.75;
    float L = clamp(u_layers, 2.0, 8.0);
    float edge = clamp(u_edge, 0.15, 1.2);
    float pulse = max(0.2, u_pulse);

    float r = length(uv);
    float a = atan(uv.y, uv.x);

    // Radial coordinate in band units; the drift moves bands outward and the pulse breathes them.
    float rb = r * (L / 0.95) - t * 0.35 + 0.35 * sin(t * pulse + r * 3.0);
    float idx = floor(rb);
    float f = fract(rb);

    // Per-band petals: count grows with the band index, direction alternates.
    float sides = 4.0 + mod(idx, 5.0) * 2.0;
    float dir = mod(idx, 2.0) < 0.5 ? 1.0 : -1.0;
    float sector = 6.28318530718 / sides;
    float pa = abs(mod(a + dir * t * (0.12 + 0.03 * mod(idx, 4.0)), sector) - sector * 0.5) / (sector * 0.5);
    // pa is 0 at the petal center and 1 at the wedge edge; each band holds a lens-shaped petal.
    float w = 0.9 * sin(3.14159265 * f) + 0.06;
    float lens = smoothstep(w, w * 0.55, pa);

    vec3 petalCol = tintedPalette(idx * 0.13 + pa * 0.2 + f * 0.15 + t * 0.06) * (0.85 + 0.6 * (1.0 - pa));
    vec3 gapCol = tintedPalette(idx * 0.13 + 0.5 + f * 0.1 + t * 0.06) * (0.6 + 0.4 * f);
    vec3 col = mix(gapCol, petalCol, lens);
    col += vec3(0.35) * smoothstep(0.25, 0.0, pa) * lens * sin(3.14159265 * f);

    // Band edge lines.
    float line = exp(-pow(min(f, 1.0 - f) * 10.0 / edge, 2.0));
    col += (tintedPalette(idx * 0.13 + 0.3) * 0.8 + vec3(0.3)) * line;

    // Central eye.
    col += tintedPalette(0.1 + t * 0.05) * exp(-r * r * 16.0) * 1.1;

    fragColor = vec4(finalize(col), 1.0);
}

// Output gain applied after the tonemap so the brightest pixel reaches full scale.
float outGain() { return 1.31; }
