uniform float u_bars; // hint_range(8.0, 16.0, 1.0) = 16.0  bar count (8..16, defaults to 16)
uniform float u_gap; // hint_range(0.0, 0.3, 0.01) = 0.12  gap between bars
uniform float u_glow; // hint_range(0.3, 2.0, 0.05) = 1.0  bar glow spread
uniform float u_audioBoost; // hint_range(0.0, 2.0, 0.05) = 1.0

// 16 vertical bars filling the frame. Idle: each bar is a sine-wave
// breathing height, phase-offset so a travelling wave runs across the
// frame. Audio: bar heights lock to u_spectrum[i]; colour per band.
void main() {
    vec2 uv = uv01();
    int nBars = int(clamp(u_bars, 4.0, 16.0));
    float gap = clamp(u_gap, 0.0, 0.3);
    float glow = clamp(u_glow, 0.1, 3.0);

    // Work in aspect-independent x.
    float t = u_time * u_speed * 0.7;

    // Cell = current bar index, and local x within the bar (0 at bar centre,
    // +/-1 at bar edge).
    float fIdx = uv.x * float(nBars);
    int idx = int(floor(fIdx));
    if (idx < 0) idx = 0;
    if (idx >= nBars) idx = nBars - 1;
    float within = fract(fIdx) * 2.0 - 1.0;

    // Idle height: sine wave across bars, with each bar offset so the whole
    // thing ripples. Always moving, so the screen is never static.
    float idleHeight = 0.35 + 0.35 *
        sin(t * 2.2 + float(idx) * 0.45) *
        cos(t * 0.9 + float(idx) * 0.18);

    // Audio height from the spectrum uniform for this bar index.
    float audioHeight = u_spectrum[idx] * 0.92 + 0.06;

    // Blend idle -> audio based on audioPresence (silent music = full idle).
    float presence = audioPresence();
    float boost = clamp(u_audioBoost, 0.0, 2.0);
    float barHeight = mix(idleHeight, audioHeight, presence * boost);
    // Global level also pumps the idle heights so levels feel "musical"
    // even for bars that read mostly silent.
    barHeight *= 1.0 + u_audioLevel * 0.5 * boost;
    barHeight = clamp(barHeight, 0.02, 0.98);

    // Bar presence mask: 1 where we're inside the bar width (accounting for
    // gap) and below its height.
    // Bar width mask: 1 inside the bar, 0 in the gap.
    float edge = 1.0 - gap;
    float barWidthMask = 1.0 - smoothstep(edge - 0.08, edge, abs(within));
    float barTopY = 1.0 - barHeight;
    // Bar body: 1 when uv.y is below the bar top (inside the bar), 0 above.
    float vertProfile = smoothstep(barTopY - 0.02, barTopY + 0.02, uv.y);
    // Gradient from base (bottom) to tip (top).
    float gradient = mix(0.55, 1.15, (uv.y - barTopY) / max(barHeight, 0.001));
    gradient = clamp(gradient, 0.0, 1.3);
    float bar = vertProfile * barWidthMask;

    // Horizontal glow outside the bar for a neon feel.
    float glowBar = exp(-abs(within) * 2.5 / glow) * vertProfile * 0.25;

    // Colour per band: walk the palette so adjacent bars are distinct.
    float palT = float(idx) / float(nBars - 1);
    vec3 tint = tintedPalette(palT + t * 0.05);

    // Slight top cap shimmer.
    float topCap = exp(-abs(uv.y - barTopY) * 40.0);
    vec3 cap = mix(tint, vec3(1.0), 0.4);

    vec3 col = tint * bar * gradient + tint * glowBar + cap * topCap * 0.6;

    // Base floor: a dim palette sweep so the lower half is never pure
    // black, especially before audio arrives.
    vec3 floorTint = tintedPalette(uv.x + t * 0.1) * (0.05 + u_audioLevel * 0.08 * boost);
    col += floorTint * (1.0 - uv.y) * 0.4;

    fragColor = vec4(finalize(col), 1.0);
}
