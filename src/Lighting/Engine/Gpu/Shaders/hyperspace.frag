uniform float u_streaks; // hint_range(8.0, 120.0, 1.0) = 40.0  angular streak bins
uniform float u_depth; // hint_range(0.2, 2.0, 0.05) = 1.0  travel rate spread
uniform float u_core; // hint_range(0.0, 3.0, 0.05) = 1.0  centre bloom

// Warp streaks pulled radially out of a hot core. Each angular bin carries one
// comet per depth layer; three layers is enough to read as depth and keeps the
// shader to nine hashes a pixel.
void main() {
    vec2 q = uvCentered();
    float t = mod(u_time * u_speed * 0.30, 1000.0);
    float r = max(length(q), 1e-3);
    float a = atan(q.y, q.x) / 6.28318 + 0.5;
    float bins = clamp(u_streaks, 8.0, 120.0);
    float dep = clamp(u_depth, 0.2, 2.0);
    float rr = pow(r, 0.65); // compress the far field into the frame

    vec3 col = vec3(0.02, 0.012, 0.05);
    for (int i = 0; i < 3; i++) {
        float lz = float(i);
        // Bin count per layer must stay integral or fract() tears one
        // streak into a wedge where the angle wraps.
        float b = a * floor(bins * (1.0 + lz * 0.35)) + lz * 13.7;
        float bi = floor(b);
        float bf = fract(b) - 0.5;
        float rnd = hash21(vec2(bi, lz));
        float rnd2 = hash21(vec2(bi, lz + 7.3));
        float rnd3 = hash21(vec2(bi, lz + 19.1));
        float sp = 0.30 + rnd * 0.55;
        float phase = fract(rr * 1.15 - t * sp * dep + rnd2);
        float len = 0.16 + rnd * 0.34;
        float trail = smoothstep(1.0 - len, 1.0, phase);
        // Wide brightness spread: most bins stay near-dark so the frame reads
        // as a few fast trails rather than a uniform starburst.
        float amp = rnd3 * rnd3 * 1.9;
        float ang = exp(-bf * bf * (40.0 + 140.0 * rnd3));
        float vis = smoothstep(0.05, 0.40, r) * mix(1.0, 0.45, lz / 3.0);
        col += tintedPalette(0.16 + rnd * 0.34 + t * 0.02) * trail * ang * vis * amp;
    }

    float core = clamp(u_core, 0.0, 3.0);
    col += tintedPalette(0.30 + t * 0.03) * exp(-r * r * 26.0) * 2.0 * core;
    col += tintedPalette(0.45) * exp(-r * r * 5.0) * 0.30 * core;

    fragColor = vec4(finalize(col), 1.0);
}
