uniform float u_count; // hint_range(4.0, 24.0, 1.0) = 12.0  bubble count
uniform float u_rise; // hint_range(0.2, 2.5, 0.05) = 1.0  upward speed
uniform float u_irid; // hint_range(0.0, 1.5, 0.05) = 0.8  iridescent band strength
#define tintedPalette(t) wheelColor(t)

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
    // Unwrapped clock for every trig and drift term: wrapping it would jump each phase.
    float t = u_time * u_speed * 0.4;
    int count = int(clamp(u_count, 2.0, 28.0));
    float rise = clamp(u_rise, 0.1, 3.0);
    float irid = clamp(u_irid, 0.0, 2.0);
    // Rise clock for the linear cycles. It wraps at a whole number of cycles for every
    // bubble (rates are multiples of 1/128, alpha repeats every 8 respawns), so the wrap
    // is invisible and the phase keeps its precision on a large u_time.
    float riseClock = mod(t * rise, 1024.0);

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
    // The blotches wander on bounded sin loops instead of scrolling, so the noise
    // coordinate never grows with the clock.
    vec2 cq = p * 1.2 + vec2(sin(t * 0.07), cos(t * 0.05)) * 0.6;
    // perf: caustic fbm->fbm3 (low-freq blotches, fine octaves invisible)
    float blob1 = fbm3(cq + fbm3(cq * 1.3 + sin(t * 0.09) * 0.3));
    float blob2 = fbm3(cq * 0.7 + vec2(3.1, 7.9) - vec2(cos(t * 0.06), sin(t * 0.08)) * 0.4);
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
        float speed = floor((0.18 + seed2 * 0.35) * 128.0 + 0.5) / 128.0;
        float cyc = riseClock * speed + seed;
        float cyclePos = fract(cyc);
        float cy = spawnY - cyclePos * travel;

        // Fade-out when drifting above the frame instead of hard-clipping at
        // the top.
        float edgeFade = smoothstep(-0.25, 0.02, cy);
        // Opacity is rolled once when the bubble spawns and held for its whole rise:
        // some fully opaque, some down to about 60 percent.
        edgeFade *= 0.6 + 0.4 * hash21(vec2(fi * 3.7 + 1.9, mod(floor(cyc), 8.0) * 1.31 + 0.7));

        // Horizontal position + sinusoidal sway that scales with bubble size
        // (smaller bubbles jiggle more).
        float homeX = seed;
        float sway = sin(t * (0.6 + seed2 * 1.4) + fi * 2.7) * (0.025 + (1.0 - seed3) * 0.05);
        float cx = homeX + sway;

        // Bubbles swell slightly as the pressure drops on the way up, and wobble out of round with their own phase.
        float grow = 1.0 + 0.25 * cyclePos;
        float wob = 0.05 * sin(t * (2.0 + seed3 * 2.0) + fi * 1.7);
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

    // Fine particles rising through the water: one hashed cell per layer, no loop; most cells stay empty and the rest are jittered so no grid shows.
    // Each column rises at its own rate, a multiple of 1/64 row per clock unit, and the
    // row id repeats every 16 rows, so the riseClock wrap lands on identical rows.
    float aspect = u_resolution.x / u_resolution.y;
    for (int li = 0; li < 2; li++) {
        float fl = float(li);
        float cols = 26.0 + fl * 20.0;
        float rows = cols * 0.62;
        float gx = uv.x * cols;
        float colId = floor(gx);
        float ch = hash21(vec2(colId + fl * 17.0, 5.3));
        float rate = floor((0.25 + ch * 0.4 + fl * 0.08) * rows * 64.0 + 0.5) / 64.0;
        float py = uv.y * rows + riseClock * rate + ch * 16.0;
        float rowId = mod(floor(py), 16.0);
        float h1 = hash21(vec2(colId + fl * 31.0, rowId + 0.5));
        float h2 = hash21(vec2(rowId * 1.7 + 3.0, colId + fl * 11.0));
        float h3 = hash21(vec2(colId * 0.9 + 7.0, rowId * 2.3 + fl));
        // Drifts a little sideways inside its cell; size and opacity vary per particle.
        float cx = 0.5 + (h2 - 0.5) * 0.5 + 0.12 * sin(t * (0.5 + h3) + h1 * 6.28);
        vec2 d = vec2((fract(gx) - cx) * aspect * rows / cols, fract(py) - 0.5 - (h3 - 0.5) * 0.5);
        float r = 0.07 + 0.07 * h3;
        float dot2 = dot(d, d);
        if (h1 > 0.82 && dot2 < r * r * 6.0) {
            float glow = exp(-dot2 / (r * r)) * (0.3 + 0.7 * fract(h1 * 37.0 + h3));
            float vis = smoothstep(0.0, 0.1, uv.y) * smoothstep(1.0, 0.9, uv.y);
            col += (tintedPalette(0.3 + h1 * 0.4) * 0.55 + vec3(0.45)) * glow * vis * 0.55;
        }
    }

    fragColor = vec4(finish(col), 1.0);
}

float outGain() { return 1.29; }
