uniform float u_intensity; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_size; // hint_range(0.4, 1.8, 0.02) = 1.2  pulse ring reach

// Five overlapping radial emitters fire on their own phase so the frame is
// never silent. Each cycle is an expanding ring that thickens as it grows,
// with a bright ignition flash at the moment of detonation. Position of
// each emitter is fixed per-slot so the beat pattern is stable.
void main() {
    vec2 uv = uvCentered();
    float t = u_time * u_speed * 1.0;
    float sz = clamp(u_size, 0.3, 2.2);
    vec3 col = vec3(0.02, 0.01, 0.035);

    for (int i = 0; i < 5; i++) {
        float fi = float(i);
        vec2 pos = vec2(
            cos(fi * 1.71) * 0.75,
            sin(fi * 2.37) * 0.55
        );
        float phase = fract(t * 0.55 + fi * 0.193);
        float radius = phase * sz * 1.3;
        float d = length(uv - pos);
        float thickness = 0.06 + phase * 0.18;
        float r = (d - radius) / thickness;
        float ring = exp(-r * r);
        float ringFade = (1.0 - phase) * (1.0 - phase * 0.4);
        vec3 tint = tintedPalette(fi * 0.23 + phase * 0.35);
        col += tint * ring * u_intensity * ringFade * 3.5;

        // Ignition flash at the start of each cycle.
        float flash = smoothstep(0.1, 0.0, phase) * exp(-d * 7.0);
        col += mix(vec3(1.0), tint, 0.35) * flash * 3.0;
    }

    fragColor = vec4(finalize(col), 1.0);
}
