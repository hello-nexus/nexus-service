uniform float u_size; // hint_range(0.03, 0.25, 0.005) = 0.08  ball radius
uniform float u_count; // hint_range(1.0, 24.0, 1.0) = 8.0  ball count

// Glossy balls bouncing over a flowing color wash. The backdrop is a slow
// two-point gradient that always fills the frame; every ball has a lit core,
// a wide colored glow that lights the wash around it, and a soft reflection
// on the floor. Radius scales the whole ball, so a larger value also widens
// the glow. In uvCentered() y = +1 is the BOTTOM, so the floor is at the
// positive end of y.
void main() {
    vec2 uv = uvCentered();
    float t = u_time * u_speed;
    float s = clamp(u_size, 0.02, 0.4) * 2.3;
    int count = int(clamp(u_count, 1.0, 24.0));

    vec3 col = tintedPalette(0.55 + uv.y * 0.18 + sin(t * 0.15) * 0.1) * 0.46;
    col += tintedPalette(0.05 + uv.x * 0.14 - t * 0.02) * (0.14 + 0.16 * sin(uv.x * 1.7 + t * 0.4));

    for (int i = 0; i < 24; i++) {
        if (i >= count) break;
        float fi = float(i);
        float xFreq   = 0.35 + fract(fi * 0.173) * 0.9;
        float x       = 1.55 * sin(t * xFreq + fi * 1.37);
        float yFreq   = 0.9  + fract(fi * 0.241) * 1.1;
        float hop     = pow(abs(sin(t * yFreq + fi * 0.83)), 0.55);
        float y       = mix(0.85, -0.9, hop);
        float squash  = mix(0.5, 1.0, smoothstep(0.0, 0.2, hop));
        vec2 ballUv   = (uv - vec2(x, y)) / vec2(1.0, squash);
        float d       = length(ballUv);
        vec3 tint     = tintedPalette(fi * 0.137 + t * 0.03);
        float core    = smoothstep(s, s * 0.75, d);
        float lit     = 1.0 - smoothstep(0.0, s, length(ballUv - vec2(-0.3, -0.35) * s));
        float glow    = exp(-d * d / (s * s * 4.0));
        col += tint * glow * 0.9;
        col = mix(col, tint * (0.7 + 0.9 * lit) + vec3(lit * lit * 0.5), core);
        // Reflection on the floor, stretched along y and fading with height.
        vec2 rUv = uv - vec2(x, 1.7 - y);
        float rd = length(rUv / vec2(1.0, 0.45));
        col += tint * exp(-rd * rd / (s * s * 3.0)) * 0.30 * smoothstep(0.4, 0.95, uv.y) * (1.0 - hop * 0.6);
    }
    float floorLine = smoothstep(0.03, 0.0, abs(uv.y - 0.92)) * 0.25;
    col += tintedPalette(0.7) * floorLine;
    fragColor = vec4(finalize(col * 1.25), 1.0);
}
