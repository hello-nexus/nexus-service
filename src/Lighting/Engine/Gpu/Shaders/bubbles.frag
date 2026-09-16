uniform float u_count; // hint_range(4.0, 24.0, 1.0) = 12.0  bubble count
uniform float u_rise; // hint_range(0.2, 2.5, 0.05) = 1.0  upward speed
uniform float u_irid; // hint_range(0.0, 1.5, 0.05) = 0.8  iridescent band strength

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
    vec3 bg = mix(bgTop * 0.35, bgMid * 0.22, smoothstep(0.0, 0.6, uv.y));
    bg = mix(bg, bgBot * 0.18, smoothstep(0.55, 1.0, uv.y));

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
    bg += tintedPalette(0.2) * smoothstep(0.45, 0.7, blob1) * 0.28;
    bg += tintedPalette(0.9) * smoothstep(0.5, 0.75, blob2) * 0.22;

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

        // Horizontal position + sinusoidal sway that scales with bubble size
        // (smaller bubbles jiggle more).
        float homeX = seed;
        float sway = sin(t * (0.6 + seed2 * 1.4) + fi * 2.7) * (0.025 + (1.0 - seed3) * 0.05);
        float cx = homeX + sway;

        // Distance from bubble center, aspect-corrected.
        vec2 d = uv - vec2(cx, cy);
        d.x *= u_resolution.x / u_resolution.y;
        float r = length(d);
        float radius = 0.025 + seed * 0.07;

        // Real soap bubbles are nearly transparent - the visible "body" is
        // entirely the thin-film interference on the soap film plus small
        // specular spots. No thick white outline. A very soft halo is kept
        // just to anchor each bubble against the background so it still
        // reads in motion.

        // Subtle glow halo - tones down, not a bright ring.
        vec3 haloHue = tintedPalette(seed2 + fi * 0.13);
        float halo = gauss(r, radius * 1.8);
        col += haloHue * halo * 0.22 * edgeFade;

        // Thin-film iridescence is the main colour of a real soap bubble.
        // Mask: a smooth dome peaking at the rim (where refraction angle is
        // shallowest on a sphere, so the visible film is brightest there)
        // and dropping toward the centre.
        float filmMask = smoothstep(radius * 1.05, radius * 0.35, r)
                       * smoothstep(radius * 0.2,  radius * 0.55, r);
        // Sample the palette with r + per-bubble phase drift so the swirl
        // shifts smoothly across each bubble's surface, not a static ring.
        vec3 film = vec3(0.0);
        float phase1 = seed * 6.28 + r * 14.0 + t * 0.35;
        float phase2 = seed * 6.28 + 2.1 + r * 9.0  - t * 0.25;
        film += tintedPalette(phase1 * 0.15 + 0.10) * 0.7;
        film += tintedPalette(phase2 * 0.15 + 0.55) * 0.5;
        col += film * filmMask * irid * edgeFade;

        // Fresnel-ish brightening along the very outer edge of the film -
        // a thin "line of light" that makes the bubble feel spherical
        // without being a solid white ring. Width scales with radius so
        // large and tiny bubbles both read correctly.
        float edgeLine = ring(r, radius * 0.985, radius * 0.045);
        col += tintedPalette(seed + 0.5 + t * 0.1) * edgeLine * 0.45 * edgeFade;

        // Primary specular highlight: bright dot offset toward upper-left,
        // slightly smaller than before so it reads as a tight reflection
        // pinpoint rather than a glob.
        vec2 specOff = vec2(-0.32, -0.32) * radius;
        float spec = exp(-length(d - specOff) / (radius * 0.08));
        col += vec3(1.0, 0.98, 0.92) * spec * 1.4 * edgeFade;

        // Secondary tiny specular: a second smaller reflection spot near
        // the first, the way a real bubble shows multiple light sources.
        vec2 specOff2 = vec2(-0.1, -0.42) * radius;
        float spec2 = exp(-length(d - specOff2) / (radius * 0.05));
        col += vec3(1.0) * spec2 * 0.8 * edgeFade;
    }

    fragColor = vec4(finalize(col), 1.0);
}
