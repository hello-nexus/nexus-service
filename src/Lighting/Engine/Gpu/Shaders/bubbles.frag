uniform float u_count; // hint_range(4.0, 24.0, 1.0) = 12.0  bubble count
uniform float u_rise; // hint_range(0.2, 2.5, 0.05) = 1.0  upward speed
uniform float u_irid; // hint_range(0.0, 1.5, 0.05) = 0.8  iridescent band strength
uniform float u_mood; // hint_range(0.0, 5.0, 1.0) = 0.0  palette mood: 0 full spectrum, 1 warm, 2 cool, 3 neon, 4 sunset, 5 aurora
#define tintedPalette(t) moodPalette((t), u_mood)

// Soft radial gaussian mask - used for halos and glow falloffs.
float gauss(float r, float sigma) {
    return exp(-(r * r) / (sigma * sigma));
}

// Ring mask for a bubble outline: 1 at the specified radius, falling to 0
// inside and outside according to width.
float ring(float r, float radius, float width) {
    return exp(-pow((r - radius) / width, 2.0));
}

void main() {
    vec2 uv = uv01();
    vec2 p = uvCentered();
    float t = mod(u_time * u_speed * 0.4, 1000.0);
    int count = int(clamp(u_count, 2.0, 28.0));
    float rise = clamp(u_rise, 0.1, 3.0);
    float irid = clamp(u_irid, 0.0, 2.0);

    // Rich, layered underwater backdrop. Three ingredients stacked:
    // 1. Vertical aqua gradient (top lighter, bottom deeper).
    // 2. Slow god-rays shimmering down from upper portion of the scene.
    // 3. Large-scale low-freq fbm for caustic-like color blotches (teal,
    //    purple, warm highlights) so the frame is never flat blue.
    vec3 bgTop = tintedPalette(0.55);
    vec3 bgMid = tintedPalette(0.72);
    vec3 bgBot = tintedPalette(0.85);
    vec3 bg = mix(bgTop * 0.55, bgMid * 0.38, smoothstep(0.0, 0.6, uv.y));
    bg = mix(bg, bgBot * 0.3, smoothstep(0.55, 1.0, uv.y));

    // Light rays streaming down. Each ray tilts slightly and pulses.
    for (int ri = 0; ri < 3; ri++) {
        float fr = float(ri);
        float rx = 0.2 + fr * 0.3 + sin(t * 0.4 + fr) * 0.08;
        float tilt = 0.12 + fr * 0.04;
        float axial = (uv.x - rx) + (uv.y - 0.0) * tilt;
        float ray = exp(-axial * axial / 0.003) * smoothstep(1.1, 0.2, uv.y);
        ray *= 0.6 + 0.4 * sin(t * (0.8 + fr * 0.3) + fr * 2.0);
        bg += tintedPalette(0.5 + fr * 0.08) * ray * 0.18;
    }

    // Caustic-like color blobs so the backdrop has real color variety.
    vec2 cq = p * 1.2 + vec2(t * 0.06, t * 0.04);
    // perf: caustic fbm->fbm3 (low-freq blotches, fine octaves invisible)
    float blob1 = fbm3(cq + fbm3(cq * 1.3 + t * 0.05));
    float blob2 = fbm3(cq * 0.7 + vec2(3.1, 7.9) - t * 0.04);
    bg += tintedPalette(0.2) * smoothstep(0.4, 0.7, blob1) * 0.45;
    bg += tintedPalette(0.9) * smoothstep(0.45, 0.75, blob2) * 0.38;

    vec3 col = bg;

    // Spawn band below the canvas. Each bubble's vertical journey runs from
    // ~+1.15 (below frame) to ~-0.2 (above frame) linearly - so they slide
    // into view from the bottom edge and exit the top, never popping in
    // mid-screen.
    float travel = 1.4;   // total vertical distance travelled per cycle
    float spawnY = 1.15;  // starting y (below frame since y=1 is bottom)

    for (int i = 0; i < 28; i++) {
        if (i >= count) break;
        float fi = float(i);
        float seed = hash21(vec2(fi * 0.71, 3.14));
        float seed2 = hash21(vec2(fi * 1.37, 6.28));
        float seed3 = hash21(vec2(fi * 2.17, 9.42));

        // Per-bubble cycle position in [0,1). Offset by seed so bubbles are
        // out of phase.
        float cyclePos = fract(t * rise * (0.18 + seed2 * 0.35) + seed);
        float cy = spawnY - cyclePos * travel;

        // Fade-out when drifting above the frame instead of hard-clipping at
        // the top.
        float edgeFade = smoothstep(-0.25, 0.02, cy);
        // Each bubble has its own slight opacity.
        edgeFade *= 0.75 + 0.25 * hash21(vec2(fi * 3.7, 1.9));

        // Horizontal position + sinusoidal sway that scales with bubble size
        // (smaller bubbles jiggle more).
        float homeX = seed;
        float sway = sin(t * (0.6 + seed2 * 1.4) + fi * 2.7) * (0.025 + (1.0 - seed3) * 0.05);
        float cx = homeX + sway;

        // Bubbles swell slightly as the pressure drops on the way up, and wobble out of round with their own phase.
        float grow = 1.0 + 0.25 * cyclePos;
        float wob = 0.05 * sin(t * (2.0 + seed3 * 2.0) + fi * 1.7);
        float speed = 0.18 + seed2 * 0.35;
        float squash = 0.04 * speed * rise;
        vec2 sq = vec2(1.0 + wob + squash, 1.0 - wob - squash * 0.5);

        // Distance from bubble center, aspect-corrected.
        vec2 d = uv - vec2(cx, cy);
        d.x *= u_resolution.x / u_resolution.y;
        float radius = (0.03 + seed * 0.09) * grow;
        // Beyond the soft glow this bubble contributes nothing.
        if (dot(d, d) > radius * radius * 6.0) {
            continue;
        }
        vec2 q = d / (radius * sq);
        float rn = length(q);

        // Faint glow outside the film, only to anchor the bubble against the backdrop.
        vec3 haloHue = tintedPalette(seed2 + fi * 0.13);
        col += haloHue * exp(-rn * rn * 0.9) * 0.1 * edgeFade;

        // The body is almost clear: the background shows through, very slightly darkened and bent.
        float body = 1.0 - smoothstep(0.85, 1.0, rn);
        col *= 1.0 - 0.1 * body * edgeFade;

        // Thin-film color lives only near the rim and swirls over the surface.
        float film = smoothstep(0.55, 0.95, rn) * (1.0 - smoothstep(0.97, 1.08, rn));
        float swirl = dot(q, vec2(1.1, 0.7)) * 1.4 + rn * 2.0 + seed * 6.28 + t * 0.4;
        vec3 filmCol = tintedPalette(swirl * 0.16 + 0.1) * 0.75 + tintedPalette(swirl * 0.11 + 0.6) * 0.45;
        col += filmCol * film * irid * 0.9 * edgeFade;

        // Thin bright rim.
        float rim = exp(-pow((rn - 0.98) / 0.045, 2.0));
        col += (tintedPalette(seed + 0.5 + t * 0.1) * 0.5 + vec3(0.4)) * rim * 0.75 * edgeFade;

        // Window reflection: a soft curved arc near the upper-left edge, plus a small one opposite.
        float ang = atan(q.y, q.x);
        float arc1 = exp(-pow((rn - 0.72) / 0.07, 2.0)) * exp(-pow((ang + 2.35) / 0.34, 2.0));
        float arc2 = exp(-pow((rn - 0.8) / 0.05, 2.0)) * exp(-pow((ang - 0.8) / 0.18, 2.0));
        col += vec3(1.0, 0.98, 0.94) * (arc1 * 1.3 + arc2 * 0.5) * edgeFade;
    }

    fragColor = vec4(finalize(col), 1.0);
}

// Output gain applied after the tonemap so the brightest pixel reaches full scale.
float outGain() { return 1.29; }
