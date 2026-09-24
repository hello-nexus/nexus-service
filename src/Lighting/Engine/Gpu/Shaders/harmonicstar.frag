uniform float u_points; // hint_range(6.0, 16.0, 1.0) = 12.0  spike count
uniform float u_core; // hint_range(0.05, 0.3, 0.01) = 0.1  core radius
uniform float u_flare; // hint_range(0.2, 2.0, 0.05) = 1.0  spike flare sharpness
uniform float u_audioBoost; // hint_range(0.0, 2.0, 0.05) = 1.0

// Radial star whose spike lengths reflect the spectrum. Idle: star rotates
// slowly and breathes, so it's always visually alive. Audio: each of the
// N points locks to a spectrum band - the loudest bands spike furthest,
// giving the star a "danced" silhouette.
void main() {
    vec2 uv = uvCentered();
    int N = int(clamp(u_points, 4.0, 20.0));
    float core = clamp(u_core, 0.03, 0.4);
    float flare = clamp(u_flare, 0.1, 3.0);
    float t = u_time * u_speed * 0.5;
    float boost = clamp(u_audioBoost, 0.0, 2.0);
    float presence = audioPresence();

    float r = length(uv);
    float a = atan(uv.y, uv.x);

    // Rotate the star over time + audio-driven spin.
    float rotate = t * 0.3 + u_audioHigh * boost * 1.2;
    float spokeAng = a + rotate;

    // Which spike are we nearest to?
    float fIdx = (spokeAng / 6.28318 + 0.5) * float(N);
    int idx = int(floor(fIdx));
    idx = (idx % N + N) % N;
    float within = fract(fIdx) * 2.0 - 1.0; // -1..+1 across the spike arc

    // Spike length: idle breathing + audio spectrum.
    int band = int(clamp(float(idx) * 16.0 / float(N), 0.0, 15.0));
    float idleLen = core + 0.45 + 0.2 * sin(t * 1.8 + float(idx) * 0.9);
    float audioLen = core + 0.1 + u_spectrum[band] * 0.95;
    float len = mix(idleLen, audioLen, presence * boost);
    // Every spike pumps on bass for a full-frame unified pulse.
    len += u_audioBass * 0.25 * boost;

    // Spike profile: a long glowing ray with sharp falloff near the edges.
    float spikeWidth = pow(max(1.0 - abs(within), 0.0), flare + 0.6);
    float radialFalloff = smoothstep(core - 0.02, core + 0.02, r) *
                          smoothstep(len + 0.02, len - 0.05, r);
    float spike = spikeWidth * radialFalloff;

    // Bright tip at the very end of each spike.
    float tipGlow = exp(-pow((r - len) * 10.0, 2.0)) * max(1.0 - abs(within), 0.0);

    // Core disc: always present, pulsed by bass.
    float coreGlow = exp(-r * r * (14.0 - u_audioBass * 6.0 * boost));

    vec3 tint = tintedPalette(float(idx) / float(N) + t * 0.04);
    vec3 coreTint = tintedPalette(t * 0.12);
    vec3 hot = vec3(1.0, 0.97, 0.92);

    vec3 col = tint * spike * 1.3;
    col += hot * tipGlow * 0.7;
    col += coreTint * coreGlow * (0.8 + u_audioBass * 1.5 * boost);

    // Sparkle ring just outside the core, scales with mids.
    float sparkRing = exp(-pow(r - (core + 0.08), 2.0) * 200.0);
    col += tintedPalette(t * 0.3) * sparkRing * (0.1 + u_audioMid * 0.9 * boost);

    // Edge tint so extreme corners aren't pitch black.
    float vignette = smoothstep(1.3, 0.4, r);
    col += tintedPalette(0.5 + t * 0.05) * vignette * 0.04;

    fragColor = vec4(finalize(col), 1.0);
}
