uniform float u_curtains; // hint_range(1.0, 8.0, 1.0) = 4.0  number of overlapping curtain bands
uniform float u_height; // hint_range(0.2, 1.0, 0.02) = 0.55  how high the aurora reaches
uniform float u_shimmer; // hint_range(0.0, 1.0, 0.02) = 0.5  high-freq flicker intensity

void main() {
    vec2 uv = uv01();
    float t = mod(u_time * u_speed * 0.4, 1000.0);
    int bands = int(clamp(u_curtains, 1.0, 8.0));
    float h = clamp(u_height, 0.1, 1.0);
    float shim = clamp(u_shimmer, 0.0, 1.0);

    vec3 sky = mix(vec3(0.0, 0.0, 0.02), vec3(0.0, 0.01, 0.06), uv.y);
    vec3 col = sky;
    // perf: bandWidth is loop-invariant -- hoist it and its reciprocal out.
    float invBandWidth = 1.0 / (h * 0.18 + 0.02);
    for (int i = 0; i < 8; i++) {
        if (i >= bands) break;
        float fi = float(i);
        // Each curtain: a vertically-narrow band whose horizontal position
        // is warped by layered sine waves so it ripples like fabric.
        float freq1 = 2.0 + fi * 0.7;
        float freq2 = 5.0 + fi * 1.3;
        float warp = sin(uv.x * freq1 + t * (0.5 + fi * 0.15) + fi * 0.9) * 0.12
                   + sin(uv.x * freq2 - t * (0.3 + fi * 0.1) + fi * 1.7) * 0.06;
        // Curtain center sits in the upper portion of the frame.
        float cy = 0.15 + fi * 0.06 + warp;
        // Vertical falloff: bright near the center, fading down toward u_height.
        // perf: x*x instead of pow(x,2.0) for the Gaussian argument.
        float dy = (uv.y - cy) * invBandWidth;
        float band = exp(-(dy * dy));
        // perf: band falls to ~0 a couple of widths from the curtain center, so
        // skip the per-band shimmer vnoise outside the lit zone -- where band is
        // negligible the flicker term scales to nothing anyway, so the look holds.
        float flicker = 1.0;
        if (band > 0.004) {
            // High-freq shimmer noise along the curtain.
            flicker = 1.0 + shim * (vnoise(vec2(uv.x * 30.0 + fi * 7.0, t * 4.0 + fi)) - 0.5) * 0.8;
        }
        vec3 tint = tintedPalette(fi * 0.14 + 0.25);
        col += tint * band * flicker * 0.55;
    }
    // Background star field: one star per grid cell, placed at a hashed point
    // inside it and drawn as a round core whose radius is set in pixels, so a
    // star stays a pinpoint on a panel and still covers a whole pixel on the
    // small canvas the LED path renders into, where a cell is about one pixel.
    // Jitter keeps stars off the cell edges, which is what lets the falloff be
    // read from its own cell alone instead of sampling the neighbours.
    float cellRows = 90.0;
    vec2 cells = vec2(cellRows * (u_resolution.x / max(u_resolution.y, 1.0)), cellRows);
    vec2 sp = uv * cells;
    vec2 cellId = floor(sp);
    float sh = hash21(cellId);
    // Rarity curve: a handful of cells hold a bright star, most hold a faint
    // one or nothing worth drawing.
    float bright = pow(sh, 18.0);
    if (bright > 0.002) {
        vec2 jitter = vec2(hash21(cellId + 7.3), hash21(cellId + 19.1)) * 0.6 + 0.2;
        float cellPx = max(u_resolution.y / cellRows, 1.0);
        float d = length(fract(sp) - jitter) * cellPx;
        float core = max(cellPx * 0.16, 0.7);
        float twinkle = 0.75 + 0.25 * sin(t * 6.0 + sh * 90.0);
        col += vec3(bright * exp(-(d * d) / (core * core)) * twinkle * 1.3);
    }
    fragColor = vec4(finalize(col), 1.0);
}
