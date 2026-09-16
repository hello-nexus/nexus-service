uniform float u_rings; // hint_range(1.0, 8.0, 1.0) = 5.0  concentric ring count
uniform float u_ringSpeed; // hint_range(0.3, 3.0, 0.05) = 1.0  outward ring velocity
uniform float u_halo; // hint_range(0.3, 2.0, 0.05) = 1.0  centre halo intensity
uniform float u_audioBoost; // hint_range(0.0, 2.0, 0.05) = 1.0

// Concentric ring pulses radiating from the centre. Idle: a slow
// heartbeat-like pulse every ~1.5s (always moving). Audio: every bass
// beat injects a strong new outward ripple; the centre brightness tracks
// u_audioBass so sustained bass "inflates" the hub.
void main() {
    vec2 uv = uvCentered();
    int N = int(clamp(u_rings, 1.0, 10.0));
    float vel = clamp(u_ringSpeed, 0.2, 4.0);
    float haloI = clamp(u_halo, 0.1, 3.0);
    float t = mod(u_time * u_speed, 1000.0);
    float boost = clamp(u_audioBoost, 0.0, 2.0);
    float presence = audioPresence();

    float r = length(uv);

    // Idle heartbeat: 1.5 s period, very slight expand-contract. Always
    // present so the frame is never static.
    float idlePhase = fract(t * 0.7);
    float idlePulse = smoothstep(0.0, 0.12, idlePhase) *
                      smoothstep(0.4, 0.12, idlePhase);

    // Outgoing rings: every ring has its own phase offset; each phase
    // cycles r from 0 to 1.4. The bass beat kicks a brand-new ring by
    // snapping ring 0's phase back to 0 on every onset.
    float bassBeat = u_audioBeat;

    vec3 col = vec3(0.0);

    // Centre halo with palette colour, expanded by bass.
    float centreGlow = exp(-r * r * (6.0 - u_audioBass * 3.0 * boost));
    vec3 hubTint = tintedPalette(t * 0.07);
    col += hubTint * centreGlow * haloI * (0.45 + u_audioBass * 0.9 * boost);

    // Heartbeat ring (idle) - a soft expanding ring pulse visible even in silence.
    float heartR = idlePhase * 1.4;
    float heartRing = exp(-pow((r - heartR) * 5.0, 2.0));
    col += hubTint * heartRing * (1.0 - idlePhase * 1.5) * 0.45;

    // Audio-driven rings.
    for (int i = 0; i < 10; i++) {
        if (i >= N) break;
        float fi = float(i);
        // Ring phase: offset by fi so rings stagger. Bass beats reset
        // ring 0, bass level speeds all rings along.
        float ringTime = t * vel * (0.8 + fi * 0.15) +
                         fi * 0.37 -
                         bassBeat * 0.25 * boost;
        float ringPhase = fract(ringTime);
        float ringR = ringPhase * 1.5;

        float thickness = 0.04 + u_audioLevel * 0.06 * boost;
        float ring = exp(-pow((r - ringR) / thickness, 2.0));
        // Fade as ring expands.
        float fade = 1.0 - ringPhase;

        // Ring colour drifts so consecutive rings aren't identical.
        vec3 ringTint = tintedPalette(fi * 0.17 + t * 0.05 + ringR * 0.1);
        col += ringTint * ring * fade *
               (0.25 + u_audioBass * 1.4 * boost + presence * 0.3);
    }

    // Flat backdrop that drifts so the frame always has something moving.
    float drift = fbm(uv * 1.8 + vec2(t * 0.2, -t * 0.15));
    col += tintedPalette(drift) * (0.08 + u_audioMid * 0.2 * boost) * (1.0 - r * 0.8);

    // Whole-frame flash on beat.
    col += vec3(1.0, 0.97, 0.95) * bassBeat * boost * 0.18;

    fragColor = vec4(finalize(col), 1.0);
}
