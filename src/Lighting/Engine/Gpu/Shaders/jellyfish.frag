uniform float u_count; // hint_range(1.0, 6.0, 1.0) = 3.0  number of jellyfish
uniform float u_glow; // hint_range(0.2, 2.5, 0.05) = 1.0  bioluminescence intensity
uniform float u_mood; // hint_range(0.0, 5.0, 1.0) = 0.0  palette mood: 0 full spectrum, 1 warm, 2 cool, 3 neon, 4 sunset, 5 aurora
#define tintedPalette(t) moodPalette((t), u_mood)

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

// Plankton specks on a scrolling hash grid. The grid coordinate must stay small
// (hash21 collapses at large cell indices), so the caller feeds a wrapped clock.
vec3 planktonLayer(vec2 uv, float tn) {
    vec2 pg = uv * 9.0 + vec2(tn * 0.25, -tn * 0.4);
    vec2 pc = floor(pg);
    float pr = hash21(pc);
    vec2 pd = fract(pg) - 0.5 - (vec2(hash21(pc + 3.1), hash21(pc + 8.7)) - 0.5) * 0.5;
    return tintedPalette(0.2 + pr) * step(0.7, pr) * exp(-dot(pd, pd) * 70.0) * 0.9;
}

const float NOISE_PERIOD = 300.0;
const float NOISE_HALF = 0.5 * NOISE_PERIOD;

void main() {
    vec2 uv = uvCentered();
    // Unwrapped clock for every sin/linear term: wrapping it would jump each phase.
    float t = u_time * u_speed * 0.55;
    // Noise needs a bounded clock, so two copies half a period apart are crossfaded:
    // each is hidden (weight 0) at exactly the moment it wraps.
    float nA = mod(t, NOISE_PERIOD);
    float nB = mod(t + NOISE_HALF, NOISE_PERIOD);
    float wA = 0.5 - 0.5 * cos(6.28318 * nA / NOISE_PERIOD);
    float wB = 1.0 - wA;
    int count = int(clamp(u_count, 1.0, 6.0));
    float glowStr = max(0.1, u_glow);

    // Lit deep water: a color gradient from a bright surface to a saturated
    // abyss, slow light shafts falling from the top, and drifting plankton.
    vec2 cq = uv * 2.2;
    float caustic = fbm3(cq + vec2(nA * 0.12, -nA * 0.18)) * wA + fbm3(cq + vec2(nB * 0.12, -nB * 0.18)) * wB;
    // Blending two independent fields shrinks contrast mid-fade; restore it around the fbm3 mean.
    caustic = 0.4375 + (caustic - 0.4375) * inversesqrt(wA * wA + wB * wB);
    vec3 water = tintedPalette(0.52 + uv.y * 0.4 + uv.x * 0.25 + caustic * 0.35 + t * 0.02) * (0.38 - uv.y * 0.12);
    float shafts = pow(0.5 + 0.5 * sin(uv.x * 5.0 + uv.y * 1.6 + sin(t * 0.4 + uv.x * 2.0) * 1.2), 3.0);
    water += tintedPalette(0.35 + uv.x * 0.1) * shafts * smoothstep(0.9, -1.0, uv.y) * 0.28;
    water += tintedPalette(0.7 + caustic * 0.3) * smoothstep(0.45, 0.8, caustic) * 0.25;
    water += planktonLayer(uv, nA) * wA + planktonLayer(uv, nB) * wB;
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
        float size   = 0.34 + fract(fi * 0.37) * 0.24;
        float attachY = 0.16 * size;
        float tLen    = size * (1.8 + fract(fi * 0.71) * 1.2);
        // Wrap just past the frame so the body leaves and enters unseen: tentacles hang below the
        // center, so the top bound clears their tips. Only the soft halo fades toward the seam.
        float xMax = u_resolution.x / u_resolution.y + size * 0.8 + 0.15;
        float yMin = -1.15 - attachY - tLen;
        float yMax = 1.5;
        drift.x = mod(drift.x + xMax, 2.0 * xMax) - xMax;
        drift.y = mod(drift.y - yMin, yMax - yMin) + yMin;
        float seam = smoothstep(0.0, 0.7, xMax - abs(drift.x)) * smoothstep(0.0, 0.7, min(drift.y - yMin, yMax - drift.y));
        // Organic wobble on top of the linear path.
        drift += vec2(
            sin(t * 0.7 + fi * 2.1) * 0.10,
            cos(t * 0.55 + fi * 1.4) * 0.08
        );

        float bellRate = 1.1 + fract(fi * 0.41) * 0.9;
        float phase    = t * bellRate + fi * 1.5;
        vec2  center   = drift;
        vec2  local    = uv - center;

        // Bell and tentacles only exist in a band around the creature; everywhere else only its halo counts.
        vec3 bodyCol = vec3(0.0);
        if (abs(local.x) < size * 0.8 && local.y > -size * 0.9 && local.y < attachY + tLen + 0.1) {
        // Bell.
        float bd       = bellDist(local, phase, size);
        float bellMask = smoothstep(0.05, -0.1, bd);
        // Inner dome lit brighter near the top of the cap.
        float inner    = smoothstep(0.0, -0.55 * size, local.y);
        vec3  bellBase = tintedPalette(fi * 0.14 + 0.18);
        float ringBands = 0.5 + 0.5 * sin(local.x / size * 9.0 + phase * 0.5);
        vec3  bellCol  = bellBase * (0.75 + inner * 1.4 + ringBands * 0.35 * smoothstep(0.0, -0.8 * size, bd));
        // Rim glow around the silhouette.
        float rim      = smoothstep(0.08, 0.0, abs(bd + 0.04)) * 0.75;
        bellCol       += tintedPalette(fi * 0.14 + 0.45) * rim * glowStr * 1.4;

        // Tentacles hang below the bell: attach along the rim (positive
        // y = downward in uvCentered) and extend deeper. Attach band
        // narrowed to ~62% of the bell width so tentacles emerge well
        // inside the silhouette rather than bulging past the rim.
        float tentacles = 0.0;
        int   tCount    = 5 + int(fract(fi * 0.53) * 4.0); // 5..8
        float tThick    = size * 0.05;
        for (int j = 0; j < 8; j++) {
            if (j >= tCount) break;
            float fj   = float(j);
            float tX   = (fj / float(max(tCount - 1, 1)) - 0.5) * size * 0.62;
            vec2  tp   = local - vec2(tX, attachY);
            tentacles += tentacleMask(tp, fi * 7.0 + fj * 3.0, t, tLen, tThick);
        }
        vec3 tentCol = tintedPalette(fi * 0.14 + 0.32) * min(tentacles, 1.0) * 1.1;
        bodyCol = bellCol * bellMask + tentCol;
        }

        // Soft bioluminescent halo around the whole creature.
        float halo    = exp(-length(local) / (size * 2.6)) * 0.5 * glowStr;
        vec3  haloCol = tintedPalette(fi * 0.14 + 0.35) * halo * seam;

        float depth = 1.0 - fi * 0.1;
        col += (bodyCol + haloCol) * depth;
    }
    fragColor = vec4(finalize(col), 1.0);
}

float outGain() { return 1.18; }
