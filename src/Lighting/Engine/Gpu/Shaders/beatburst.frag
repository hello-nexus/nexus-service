uniform float u_streaks; // hint_range(8.0, 64.0, 1.0) = 40.0  streak count around the circle
uniform float u_trail; // hint_range(0.2, 1.8, 0.05) = 1.15  streak reach from the core
uniform float u_spread; // hint_range(0.05, 1.2, 0.05) = 0.45  angular width of each streak

// Hi-res spectrum, declared here only (see beatbuilder.frag): with 48+
// streaks the 16-band uniform repeats every three spokes and the ring reads
// as a coarse rosette.
uniform float u_spectrum64[64];
uniform float u_audioBoost; // hint_range(0.0, 2.0, 0.05) = 1.0

// A burst of light streaks radiating from a hot core, one streak per band,
// with a shockwave ring on every bass onset. Idle: the streaks counter-rotate
// and breathe. Audio: each streak's reach is its band, so the silhouette is
// a radial spectrum with motion blur rather than a bar chart bent in a circle.
void main() {
    vec2 uv = uvCentered();
    float t = mod(u_time * u_speed * 0.6, 1000.0);
    float boost = clamp(u_audioBoost, 0.0, 2.0);
    float presence = audioPresence();
    int spokes = int(clamp(u_streaks, 8.0, 64.0));
    float reach = clamp(u_trail, 0.15, 1.8);
    float spread = clamp(u_spread, 0.05, 1.2);

    float r = length(uv);
    float a = atan(uv.y, uv.x) / 6.28318 + 0.5;   // 0..1 around the circle

    // Slow rotation, reversing direction on the slower of two sines so the
    // burst never reads as a fan spinning one way forever.
    float spin = t * 0.05 + sin(t * 0.11) * 0.15;
    float ang = fract(a + spin);

    float fIdx = ang * float(spokes);
    int idx = int(floor(fIdx));
    float within = fract(fIdx) * 2.0 - 1.0;       // -1..1 across the streak

    // Mirror the band index across the top so left and right sides answer to
    // the same frequencies - an asymmetric burst reads as a glitch.
    float half01 = abs(ang * 2.0 - 1.0);
    int band = int(clamp(half01 * 63.0, 0.0, 63.0));
    float energy = u_spectrum64[band];

    float idle = 0.55 + 0.20 * sin(t * 1.7 + float(idx) * 0.9)
                      * cos(t * 0.8 + float(idx) * 0.31);
    float len = mix(idle, energy * 1.35 + 0.12, presence * boost);
    len *= 1.0 + u_audioLevel * 0.5 * boost;
    // Per-streak jitter: without it every spoke resolves to the same length and
    // the burst reads as a drawn sunburst rather than a spectrum.
    len *= 0.75 + 0.50 * hash21(vec2(float(idx), floor(t * 0.7)));
    len = clamp(len * reach, 0.05, 1.9);

    // Streak body: angular gaussian x radial taper, so it is a spike of light
    // that fades out rather than a hard wedge.
    float angMask = exp(-pow(within / spread, 2.0) * 2.5);
    float radial = smoothstep(len, len * 0.25, r) * smoothstep(0.0, 0.06, r);
    float streak = angMask * radial;

    vec3 tint = tintedPalette(half01 * 0.45 + t * 0.06);
    vec3 col = tint * streak * (0.85 + energy * 1.2);
    // Hot inner half of each streak.
    col += mix(tint, vec3(1.0), 0.55) * pow(streak, 3.0) * 0.8;

    // Core: a bright pulsing centre the streaks emanate from.
    float coreR = 0.085 + u_audioBass * boost * 0.07 + u_audioBeat * boost * 0.04;
    float core = exp(-pow(r / coreR, 2.0));
    col += mix(tintedPalette(t * 0.1), vec3(1.0), 0.65) * core * (1.2 + u_audioBeat * boost);
    // Tight halo around the core; a wide one washes the whole frame.
    col += tintedPalette(t * 0.1) * exp(-r * 9.0) * (0.22 + u_audioBass * boost * 0.30);

    // Shockwave: u_audioBeat decays 1 -> 0 after an onset, so 1 - beat is a
    // radius sweeping outward. Two rings, the second trailing, for weight.
    float wave = 1.0 - u_audioBeat;
    float ring1 = exp(-pow((r - wave * 1.5) * 34.0, 2.0));
    float ring2 = exp(-pow((r - wave * 1.5 + 0.06) * 52.0, 2.0));
    float ringAmt = u_audioBeat * boost;
    col += mix(tint, vec3(1.0), 0.85) * ring1 * ringAmt * 0.9;
    col += mix(tint, vec3(1.0), 0.4) * ring2 * ringAmt * 0.3;

    // High-band sparks scattered along the streaks.
    vec2 cell = floor(vec2(ang * float(spokes) * 3.0, r * 26.0));
    float h = hash21(cell + floor(t * 3.0));
    if (h > 0.93) {
        vec2 local = fract(vec2(ang * float(spokes) * 3.0, r * 26.0)) - 0.5;
        float spark = exp(-dot(local, local) * 60.0);
        float alive = step(r, len * 1.15);
        col += mix(tint, vec3(1.0), 0.5) * spark * alive
               * (0.25 + u_audioHigh * boost * 0.9);
    }

    // Vignette so the corners stay dark and the burst is the subject.
    col *= 1.0 - smoothstep(0.85, 1.8, r) * 0.7;

    fragColor = vec4(finalize(col), 1.0);
}
