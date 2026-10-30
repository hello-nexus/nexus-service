uniform float u_flow; // hint_range(0.2, 3.0, 0.05) = 1.0  surface flow rate
uniform float u_thickness; // hint_range(0.3, 2.0, 0.05) = 1.0  surface variation
uniform float u_ripple; // hint_range(0.3, 3.0, 0.05) = 1.0  ripple frequency
uniform float u_mood; // hint_range(0.0, 5.0, 1.0) = 0.0  palette mood: 0 full spectrum, 1 warm, 2 cool, 3 neon, 4 sunset, 5 aurora
#define tintedPalette(t) moodPalette((t), u_mood)

// Liquid chrome: a heightfield of interlocking sine swells that bend each
// other as they travel is lit like polished metal. The surface normal looks
// up a banded environment, so color sweeps through the palette in glossy
// bands, and a moving key light drags sharp white highlights across the
// ridges. No noise.
float chromeH(vec2 p, float t) {
    float a = sin(p.x + sin(p.y * 0.8 + t * 1.1) * 1.3 + t * 0.9);
    float b = sin(p.y * 1.15 + sin(p.x * 0.9 - t * 0.9) * 1.5 - t * 0.7);
    float c = sin((p.x + p.y) * 0.7 + sin(t * 0.8 + p.x * 0.5) * 1.1 + t);
    return a + b + c * 0.7;
}

void main() {
    vec2 uv = uvCentered();
    float t = u_time * u_speed * u_flow * 0.5;
    float thick = clamp(u_thickness, 0.1, 2.5);
    float rip = clamp(u_ripple, 0.2, 4.0);

    vec2 p = uv * (1.5 * rip + 0.6);
    float e = 0.06;
    float h = chromeH(p, t);
    float hx = chromeH(p + vec2(e, 0.0), t);
    float hy = chromeH(p + vec2(0.0, e), t);
    vec3 n = normalize(vec3((h - hx) / e * 0.3 * thick, (h - hy) / e * 0.3 * thick, 1.0));

    // Banded environment: bright sky above, dark horizon, bright ground, like a studio chrome map.
    vec3 r = reflect(vec3(0.0, 0.0, -1.0), n);
    float env = 0.5 + 0.5 * sin(r.y * 4.4 + r.x * 2.2 + 1.2);
    vec3 tint = tintedPalette(r.x * 0.45 + r.y * 0.35 + h * 0.07 + t * 0.06);
    vec3 col = tint * (0.40 + 1.15 * env);

    vec3 light = normalize(vec3(cos(t * 0.8) * 0.8, sin(t * 0.6) * 0.6, 0.7));
    float spec = pow(max(dot(reflect(-light, n), vec3(0.0, 0.0, 1.0)), 0.0), 20.0);
    col += vec3(1.0, 0.98, 0.95) * spec * 1.3;

    fragColor = vec4(finalize(col), 1.0);
}

// Output gain applied after the tonemap so the brightest pixel reaches full scale.
float outGain() { return 1.34; }
