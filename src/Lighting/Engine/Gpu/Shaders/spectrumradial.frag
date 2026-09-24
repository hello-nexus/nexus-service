uniform float u_spokes; // hint_range(16.0, 64.0, 1.0) = 32.0  spoke count (16..64, default 32)
uniform float u_radius; // hint_range(0.0, 0.4, 0.01) = 0.1  inner radius
uniform float u_glow; // hint_range(0.3, 2.0, 0.05) = 1.0  spoke glow
uniform float u_audioBoost; // hint_range(0.0, 2.0, 0.05) = 1.0

// 32 radial spokes from the centre. Idle: a rotating sine wave across
// spokes. Audio: each spoke's length = the matching spectrum band,
// coloured per band.
void main() {
    vec2 uv = uvCentered();
    int N = int(clamp(u_spokes, 6.0, 64.0));
    float inner = clamp(u_radius, 0.0, 0.45);
    float glow = clamp(u_glow, 0.1, 3.0);
    float t = u_time * u_speed * 0.6;
    float boost = clamp(u_audioBoost, 0.0, 2.0);
    float presence = audioPresence();

    float r = length(uv);
    float a = atan(uv.y, uv.x);
    float sp = (a / 6.28318 + 0.5) * float(N);
    int idx = int(floor(sp));
    idx = idx < 0 ? idx + N : idx;
    idx = idx % N;
    float within = fract(sp) * 2.0 - 1.0;

    // Map spoke index to a spectrum band (16 bands across N spokes).
    float fBand = clamp(float(idx) * 16.0 / float(N), 0.0, 15.0);
    int bLo = int(floor(fBand));
    int bHi = min(bLo + 1, 15);
    float bFrac = fract(fBand);
    float specVal = mix(u_spectrum[bLo], u_spectrum[bHi], bFrac);

    // Idle spoke length: rotating sine wave across spokes. Two waves at
    // different frequencies keep the motion interesting.
    float fi = float(idx) / float(N);
    float idleLen =
        0.45 + 0.32 * sin(t * 2.3 + fi * 6.28318 * 2.0)
             + 0.12 * cos(t * 1.1 - fi * 6.28318 * 4.0);
    idleLen = clamp(idleLen, 0.15, 0.95);

    float audioLen = 0.15 + specVal * 0.85;

    float len = mix(idleLen, audioLen, presence * boost);
    // Every spoke also gets a little bass punch so beats are felt
    // globally, not just in the low-band spokes.
    len *= 1.0 + u_audioBeat * 0.4 * boost;

    // Spoke radial profile: bright at the tip, fades toward centre.
    float spokeMask = smoothstep(1.0, 0.05, abs(within)) *
                      smoothstep(inner - 0.02, inner, r) *
                      smoothstep(len + 0.01, len - 0.05, r);
    float tipGlow = exp(-(r - len) * (r - len) * 800.0) *
                    smoothstep(1.0, 0.0, abs(within));

    // Angular glow falls off so adjacent spokes blur together.
    float angGlow = exp(-abs(within) * 2.5 / glow) *
                    smoothstep(inner, inner + 0.02, r) *
                    smoothstep(len + 0.1, len - 0.05, r);

    vec3 tint = tintedPalette(fi + t * 0.05);
    vec3 hot = vec3(1.0, 0.96, 0.92);

    vec3 col = tint * spokeMask * 1.1 + hot * tipGlow * 0.8 + tint * angGlow * 0.35;

    // Ambient centre disc: palette wash + beat halo so the hub always pulses.
    float centreGlow = exp(-r * r * 5.0);
    vec3 centreTint = tintedPalette(t * 0.1);
    col += centreTint * centreGlow * (0.2 + u_audioBass * 0.9 * boost);
    // Full-frame bass bloom.
    col += hot * u_audioBeat * boost * 0.3 * smoothstep(0.8, 0.0, r);

    // Faint outer ring so the edges aren't pitch black.
    float outerRing = smoothstep(0.95, 0.7, r) * 0.08;
    col += tintedPalette(t * 0.07) * outerRing;

    fragColor = vec4(finalize(col), 1.0);
}
