uniform float u_streaks; // hint_range(8.0, 120.0, 1.0) = 40.0  angular streak bins
uniform float u_depth; // hint_range(0.2, 2.0, 0.05) = 1.0  travel rate spread
uniform float u_core; // hint_range(0.0, 3.0, 0.05) = 1.0  centre bloom
uniform float u_mood; // hint_range(0.0, 5.0, 1.0) = 0.0  palette mood: 0 full spectrum, 1 warm, 2 cool, 3 neon, 4 sunset, 5 aurora
#define tintedPalette(t) moodPalette((t), u_mood)

// Warp tunnel: a colored vortex fills the whole frame (angle and depth map to
// palette, with spiral swirl bands travelling outward), and bright comet streaks
// are pulled radially out of a hot core on top of it. Streak layers
// stay few to keep the per-pixel hash count low.
void main() {
    vec2 q = uvCentered();
    float t = mod(u_time * u_speed * 0.30, 1000.0);
    float r = max(length(q), 1e-3);
    float a = atan(q.y, q.x) / 6.28318 + 0.5;
    float bins = clamp(u_streaks, 8.0, 120.0);
    float dep = clamp(u_depth, 0.2, 2.0);
    float rr = pow(r, 0.65);

    // Tunnel wall: palette by angle, banded by depth, swirling as it travels.
    float swirl = sin(a * 18.8496 + rr * 9.0 - t * 5.0 * dep) * 0.5 + 0.5;
    float rings = sin(rr * 14.0 - t * 6.0 * dep) * 0.5 + 0.5;
    float wall = smoothstep(0.0, 0.7, rr) * (0.35 + 0.5 * swirl + 0.35 * rings);
    vec3 col = tintedPalette(a * 1.0 + rr * 0.6 - t * 0.05) * wall * 0.9;
    col += tintedPalette(0.5 + a * 2.0 + t * 0.04) * (1.0 - smoothstep(0.0, 0.5, rr)) * 0.35;

    for (int i = 0; i < 3; i++) {
        float lz = float(i);
        // Bin count per layer must stay integral or fract() tears a streak where the angle wraps.
        float b = a * floor(bins * (1.0 + lz * 0.35)) + lz * 13.7;
        float bi = floor(b);
        float bf = fract(b) - 0.5;
        float rnd = hash21(vec2(bi, lz));
        float rnd2 = hash21(vec2(bi, lz + 7.3));
        float rnd3 = hash21(vec2(bi, lz + 19.1));
        float sp = 0.30 + rnd * 0.55;
        float phase = fract(rr * 1.15 - t * sp * dep + rnd2);
        float len = 0.22 + rnd * 0.40;
        float trail = smoothstep(1.0 - len, 1.0, phase);
        float amp = 0.5 + rnd3 * 2.2;
        float ang = exp(-bf * bf * (28.0 + 80.0 * rnd3));
        float vis = smoothstep(0.04, 0.30, r) * mix(1.0, 0.6, lz / 3.0);
        vec3 tint = tintedPalette(0.1 + rnd * 0.8 + t * 0.03);
        col += (tint + vec3(0.35)) * trail * ang * vis * amp;
    }

    float core = clamp(u_core, 0.0, 3.0);
    col += tintedPalette(0.30 + t * 0.03) * exp(-r * r * 26.0) * 2.4 * core;
    col += vec3(1.0) * exp(-r * r * 90.0) * 1.2 * core;
    col += tintedPalette(0.45) * exp(-r * r * 5.0) * 0.40 * core;

    fragColor = vec4(finalize(col), 1.0);
}

// Output gain applied after the tonemap so the brightest pixel reaches full scale.
float outGain() { return 1.15; }
