uniform float u_count; // hint_range(1.0, 6.0, 1.0) = 3.0  number of jellyfish
uniform float u_glow; // hint_range(0.2, 2.5, 0.05) = 1.0  bioluminescence intensity

// Half-dome bell: full ellipse with the bottom clipped so the shape
// reads as a cap with a flat-ish underside. In our uvCentered frame
// y = -1 is TOP and y = +1 is BOTTOM, so the bell faces -y (up) and
// the tentacles attach along its +y (bottom) rim.
float bellDist(vec2 p, float phase, float size) {
    float pulse = 0.85 + 0.15 * sin(phase);
    vec2 q = p;
    q.x /= (0.5 * pulse * size);
    q.y /= (0.55 * size);
    float d = length(q) - 1.0;
    // Clip off the lower lip so the bottom reads flat.
    d = max(d, (p.y - 0.18 * size) / size);
    return d;
}

// Tentacle: sin-displaced vertical line hanging BELOW its attach point.
// p is in local bell-frame coords where p.y > 0 is downward (toward
// screen bottom in uvCentered). Returns 0..1 mask. Wobble amplitude
// damped near the attach point so the top of the tentacle stays inside
// the bell silhouette and only swings out as it hangs lower.
float tentacleMask(vec2 p, float seed, float t, float len, float thickness) {
    if (p.y < 0.0 || p.y > len) return 0.0;
    float wobbleFreq1 = 7.0 + fract(seed * 0.37) * 4.0;
    // Ramp wobble in over the first 25% of the tentacle so the attach
    // point sits flush against the bell edge.
    float wobbleAmp = smoothstep(0.0, len * 0.25, p.y);
    // perf: drop the secondary high-freq sin; single wobble reads the same
    float waveX = sin(p.y * wobbleFreq1 + t * 2.2 + seed * 3.1) * 0.030 * wobbleAmp;
    float dx = p.x - waveX;
    float taper = 1.0 - smoothstep(0.0, len * 0.95, p.y);
    float thick = thickness * (taper * 0.85 + 0.15);
    float core = smoothstep(thick, 0.0, abs(dx));
    // Fade right at the tip so tentacles taper off.
    float tipFade = 1.0 - smoothstep(len * 0.6, len, p.y) * 0.4;
    return core * tipFade;
}

void main() {
    vec2 uv = uvCentered();
    float t = mod(u_time * u_speed * 0.55, 1000.0);
    int count = int(clamp(u_count, 1.0, 6.0));
    float glowStr = max(0.1, u_glow);

    // Deep water background with caustic shimmer that also drifts.
    // perf: caustic is a soft low-amplitude tint -> fbm3 (3 oct), look unchanged
    float caustic = fbm3(uv * 2.6 + vec2(t * 0.12, -t * 0.18)) * 0.14;
    vec3 water = tintedPalette(0.6) * 0.05 + vec3(caustic * 0.25, caustic * 0.45, caustic);
    vec3 col = water;

    for (int i = 0; i < 6; i++) {
        if (i >= count) break;
        float fi = float(i);
        // Per-jellyfish random direction + speed + start so they truly
        // swim across the frame from different directions instead of
        // all drifting along parallel sine paths.
        float angle     = fract(fi * 0.4231 + 0.17) * 6.28318;
        float swimSpeed = 0.18 + fract(fi * 0.317) * 0.22;
        vec2 dir        = vec2(cos(angle), sin(angle));
        vec2 startPos   = vec2(sin(fi * 7.31 + 1.0), cos(fi * 4.11 + 2.3)) * 1.3;
        vec2 drift      = startPos + dir * t * swimSpeed;
        // Wrap into a region just slightly larger than the visible frame
        // (visible is roughly +-1.78 in x for 16:9, +-1 in y) so jellies
        // are almost always on screen and only briefly slip off an edge
        // before re-appearing on the opposite side. Previous +-3.6 / +-2.4
        // bounds meant most of the population was off-screen at any time.
        drift.x = mod(drift.x + 2.0, 4.0) - 2.0;
        drift.y = mod(drift.y + 1.25, 2.5) - 1.25;
        // Organic wobble on top of the linear path.
        drift += vec2(
            sin(t * 0.7 + fi * 2.1) * 0.10,
            cos(t * 0.55 + fi * 1.4) * 0.08
        );

        float size     = 0.26 + fract(fi * 0.37) * 0.22;
        float bellRate = 1.1 + fract(fi * 0.41) * 0.9;
        float phase    = t * bellRate + fi * 1.5;
        vec2  center   = drift;
        vec2  local    = uv - center;

        // Bell.
        float bd       = bellDist(local, phase, size);
        float bellMask = smoothstep(0.05, -0.1, bd);
        // Inner dome lit brighter near the top of the cap.
        float inner    = smoothstep(0.0, -0.55 * size, local.y);
        vec3  bellBase = tintedPalette(fi * 0.14 + 0.18);
        vec3  bellCol  = bellBase * (0.28 + inner * 0.9);
        // Rim glow around the silhouette.
        float rim      = smoothstep(0.08, 0.0, abs(bd + 0.04)) * 0.75;
        bellCol       += bellBase * rim * glowStr;

        // Tentacles hang below the bell: attach along the rim (positive
        // y = downward in uvCentered) and extend deeper. Attach band
        // narrowed to ~62% of the bell width so tentacles emerge well
        // inside the silhouette rather than bulging past the rim.
        float tentacles = 0.0;
        int   tCount    = 5 + int(fract(fi * 0.53) * 4.0); // 5..8
        float attachY   = 0.16 * size;
        float tLen      = size * (1.8 + fract(fi * 0.71) * 1.2);
        float tThick    = size * 0.05;
        for (int j = 0; j < 8; j++) {
            if (j >= tCount) break;
            float fj   = float(j);
            float tX   = (fj / float(max(tCount - 1, 1)) - 0.5) * size * 0.62;
            vec2  tp   = local - vec2(tX, attachY);
            tentacles += tentacleMask(tp, fi * 7.0 + fj * 3.0, t, tLen, tThick);
        }
        vec3 tentCol = bellBase * min(tentacles, 1.0) * 0.55;

        // Soft bioluminescent halo around the whole creature.
        float halo    = exp(-length(local) / (size * 2.0)) * 0.22 * glowStr;
        vec3  haloCol = tintedPalette(fi * 0.14 + 0.35) * halo;

        float depth = 1.0 - fi * 0.1;
        col += (bellCol * bellMask + tentCol + haloCol) * depth;
    }
    fragColor = vec4(finalize(col), 1.0);
}
