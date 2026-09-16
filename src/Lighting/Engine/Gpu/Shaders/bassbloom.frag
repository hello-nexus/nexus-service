uniform float u_petals; // hint_range(4.0, 12.0, 1.0) = 7.0  bloom petal count
uniform float u_shimmer; // hint_range(0.0, 2.0, 0.05) = 1.0  high-band shimmer
uniform float u_bloomSize; // hint_range(0.1, 0.8, 0.02) = 0.4  bloom base size
uniform float u_audioBoost; // hint_range(0.0, 2.0, 0.05) = 1.0

// Central bloom that pumps on bass with high-frequency shimmer specks.
// Idle: gentle slow-breathing bloom + drifting palette so the frame is
// always visibly animating. Audio: the bloom's radius follows
// u_audioBass, individual petals inflate on beats, and u_audioHigh
// drives a field of bright specks over the bloom and background.
void main() {
    vec2 uv = uvCentered();
    int petals = int(clamp(u_petals, 3.0, 14.0));
    float shimmer = clamp(u_shimmer, 0.0, 3.0);
    float bloomBase = clamp(u_bloomSize, 0.05, 1.0);
    float t = mod(u_time * u_speed * 0.6, 1000.0);
    float boost = clamp(u_audioBoost, 0.0, 2.0);

    float r = length(uv);
    float a = atan(uv.y, uv.x);

    // Idle bloom size: slow breathing between ~0.8x and ~1.1x.
    float idleSize = bloomBase * (0.9 + 0.2 * sin(t * 1.2));
    // Audio bloom size: bass drives bigger, beat pumps on top.
    float audioSize = bloomBase * (0.7 + u_audioBass * 2.2 * boost +
                                   u_audioBeat * boost * 0.6);
    float bloomR = mix(idleSize, audioSize, 0.35 + 0.65 * audioPresence());

    // Petal modulation: petals ride on the bloom's radius, each one
    // offset by angle. Each petal scales with a spectrum band so loud
    // bass flares a couple of fat petals.
    float petalAngle = a * float(petals) + t * 0.4;
    int petalIdx = int(mod(floor(petalAngle / 6.28318 + 0.5), float(petals)));
    int band = (petalIdx * 16) / petals;
    if (band < 0) band = 0;
    if (band > 15) band = 15;
    float petalLen = 1.0 + u_spectrum[band] * 0.6 * boost;
    float localR = r / max(bloomR * petalLen, 0.01);

    // Bloom body: smooth falloff from centre.
    float body = exp(-localR * localR * 2.0);
    // Edge petals: higher-frequency ripple on top of the bloom's soft core.
    float edgeRipple = 0.5 + 0.5 * sin(petalAngle + t * 1.6);
    body *= 1.0 + edgeRipple * 0.5;

    vec3 bloomTint = tintedPalette(a / 6.28318 + 0.5 + t * 0.06);
    vec3 bloomHot = vec3(1.0, 0.98, 0.94);

    vec3 col = bloomTint * body * (0.55 + u_audioBass * 0.9 * boost);
    col += bloomHot * pow(body, 3.0) * 0.55;

    // High-band shimmer: dense speckles over the frame, visible mostly
    // when u_audioHigh is strong. Idle: a soft sparse speck field still
    // twinkles so the effect isn't "dead" when silent.
    float specBase = u_audioHigh * boost * 1.5 + 0.04;
    vec2 cell = floor(uv * 30.0);
    float h = hash21(cell + floor(t * 6.0));
    float twinkle = 0.5 + 0.5 * sin(t * 12.0 + h * 20.0);
    if (h > (0.90 - specBase * 0.5)) {
        vec2 local = fract(uv * 30.0) - 0.5;
        float speck = exp(-dot(local, local) * 100.0);
        col += tintedPalette(h + t * 0.4) * speck * twinkle * (0.4 + specBase);
    }

    // Slow background wash so corners have colour, pumped by mids.
    float wash = fbm(uv * 1.2 + vec2(t * 0.2, -t * 0.15));
    col += tintedPalette(wash + t * 0.1) *
           (0.08 + u_audioMid * 0.35 * boost) * (1.0 - smoothstep(0.2, 1.1, r) * 0.4);

    // Bass-beat ring pushed out each time a beat fires.
    float beatR = 0.25 + u_audioBeat * boost * 0.9;
    float beatRing = exp(-pow((r - beatR) * 8.0, 2.0));
    col += bloomHot * beatRing * u_audioBeat * boost * 0.45;

    fragColor = vec4(finalize(col), 1.0);
}
