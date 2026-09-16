uniform float u_ringDensity; // hint_range(3.0, 12.0, 1.0) = 6.0  ring density
uniform float u_twist; // hint_range(0.0, 2.0, 0.05) = 0.8  per-ring twist
uniform float u_neon; // hint_range(0.3, 2.0, 0.05) = 1.0  neon intensity
uniform float u_audioBoost; // hint_range(0.0, 2.0, 0.05) = 1.0

// Perspective tunnel with concentric rings. Idle: rings zoom steadily
// toward the camera at a base rate so there's always motion. Audio: the
// zoom rate scales with u_audioLevel, ring colours come from the
// spectrum, and every beat kicks a bright flash ring near the camera.
void main() {
    vec2 uv = uvCentered();
    float density = clamp(u_ringDensity, 2.0, 14.0);
    float twist = clamp(u_twist, 0.0, 3.0);
    float neon = clamp(u_neon, 0.1, 3.0);
    float t = u_time * u_speed;
    float boost = clamp(u_audioBoost, 0.0, 2.0);
    float presence = audioPresence();

    float r = length(uv);
    float a = atan(uv.y, uv.x);

    // Tunnel "depth" coordinate: 1/r so rings spaced in depth appear
    // with perspective distortion. Scrolling it causes the zoom-in look.
    float depth = 1.0 / (r + 0.01);
    float zoomRate = 0.6 + u_audioLevel * 2.4 * boost;
    float scrolling = depth * density - t * zoomRate;
    float ringId = floor(scrolling);
    float within = fract(scrolling);

    // Twisted rings: angle offset per ring so the tunnel "spirals"
    // downrange as you look into it.
    float ringAng = a + ringId * twist * 0.3 + t * 0.15;

    // Each ring picks a spectrum band so loud lows stretch as rings
    // ahead of the camera while highs show up on the distant rings.
    int band = int(mod(ringId, 16.0));
    if (band < 0) band += 16;
    float specVal = u_spectrum[band];

    // Ring profile: sharp neon edge + soft inner halo.
    float ring = pow(1.0 - abs(within * 2.0 - 1.0), neon * 4.0);
    // Keep the ring visible regardless of audio by giving it an idle
    // floor brightness.
    float brightness = 0.45 + specVal * 1.1 * presence * boost;

    // Idle: colour cycles with ringId + time so rings always feel alive.
    // Audio: layer spectrum-driven tint over that.
    vec3 idleTint = tintedPalette(ringId * 0.11 + t * 0.1 + ringAng * 0.05);
    vec3 audioTint = tintedPalette(float(band) / 16.0 + t * 0.1);
    vec3 tint = mix(idleTint, audioTint, presence * boost);

    // Angular stripes within the ring: makes the twist readable and
    // provides visible frame-to-frame motion even when audio is still.
    float stripe = 0.6 + 0.4 * sin(ringAng * 16.0 + t * 2.0);

    vec3 col = tint * ring * brightness * stripe;

    // Distance fog: rings at the "back" of the tunnel darken so the
    // frame has depth even when the zoom rate is low.
    float fog = smoothstep(0.0, 1.8, r);
    col *= fog;

    // Centre vanishing point: bright-to-black core. Expands on bass.
    float core = exp(-r * r * (8.0 - u_audioBass * 4.0 * boost));
    col += tintedPalette(t * 0.2) * core * (0.3 + u_audioBass * 1.5 * boost);

    // Beat flash: a bright ring pulled right up to the camera.
    float beatR = 0.18 + u_audioBeat * 0.3 * boost;
    float beatRing = exp(-pow((r - beatR) * 12.0, 2.0));
    col += vec3(1.0, 0.96, 0.9) * beatRing * u_audioBeat * boost * 0.6;

    // Ambient rim tint so the outer frame isn't dead black.
    col += tintedPalette(t * 0.08) * 0.05;

    fragColor = vec4(finalize(col), 1.0);
}
