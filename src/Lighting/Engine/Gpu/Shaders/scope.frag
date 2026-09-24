uniform float u_thickness; // hint_range(0.002, 0.04, 0.001) = 0.012  trace thickness
uniform float u_harmonics; // hint_range(1.0, 5.0, 1.0) = 3.0  number of harmonic layers
uniform float u_glow; // hint_range(0.3, 2.0, 0.05) = 1.0  trace glow
uniform float u_audioBoost; // hint_range(0.0, 2.0, 0.05) = 1.0

// Full-frame oscilloscope. Idle: layered traveling sine waves at several
// frequencies, tinted across the palette. Audio: each harmonic layer is
// scaled by the matching spectrum band so the scope "draws" the music.
void main() {
    vec2 uv = uvCentered();
    float thick = clamp(u_thickness, 0.002, 0.06);
    int harm = int(clamp(u_harmonics, 1.0, 6.0));
    float glow = clamp(u_glow, 0.1, 3.0);
    float t = u_time * u_speed * 0.6;
    float boost = clamp(u_audioBoost, 0.0, 2.0);
    float presence = audioPresence();

    // Ambient grid wash so the scope has a faint background "screen" look.
    float gridX = abs(sin(uv.x * 12.0));
    float gridY = abs(sin(uv.y * 6.0));
    float grid = pow(max(gridX, 0.0), 18.0) + pow(max(gridY, 0.0), 18.0);
    vec3 col = tintedPalette(0.6 + uv.y * 0.1) * 0.05 + vec3(grid) * 0.04;

    // Each harmonic layer: sine wave at a specific frequency, phase
    // driven by u_time, amplitude scaled by the matching spectrum band.
    // Idle fallback: constant amplitude * sin(t) so the scope always shows
    // activity without audio.
    for (int i = 0; i < 6; i++) {
        if (i >= harm) break;
        float fi = float(i);
        float freq = 2.0 + fi * 3.0;                 // higher harm = tighter wiggles
        int band = int(clamp(fi * 2.5, 0.0, 15.0));
        float spec = u_spectrum[band];
        // Idle amplitude modulated by time so it breathes instead of
        // sitting still.
        float idleAmp = 0.28 + 0.12 * sin(t * (0.6 + fi * 0.4));
        float audioAmp = 0.1 + spec * 0.8;
        float amp = mix(idleAmp, audioAmp, presence * boost);
        // Wave direction: even layers drift right, odd layers drift left
        // so the scope gets chaotic motion.
        float dir = mod(fi, 2.0) < 0.5 ? 1.0 : -1.0;
        float phase = t * (0.8 + fi * 0.3) * dir;
        float y = amp * sin(uv.x * freq + phase);

        float d = abs(uv.y - y);
        float line = exp(-d * d / (thick * thick));
        float halo = exp(-d * d / (thick * thick * 20.0 / glow)) * 0.3;

        vec3 tint = tintedPalette(fi / float(harm) + t * 0.05);
        col += tint * (line * 0.85 + halo * 0.5);
    }

    // Volume-driven sky glow across the whole frame - makes louder audio
    // visibly brighter even on the grid backdrop.
    col += tintedPalette(t * 0.1) * (u_audioLevel * 0.2 * boost);
    // Bass hit flash the whole frame.
    col += vec3(1.0, 0.96, 0.92) * u_audioBeat * boost * 0.25;

    fragColor = vec4(finalize(col), 1.0);
}
