uniform float u_count; // hint_range(2.0, 10.0, 1.0) = 5.0  number of blobs
uniform float u_viscosity; // hint_range(0.1, 1.0, 0.02) = 0.45  merge smoothness
uniform float u_size; // hint_range(0.08, 0.5, 0.01) = 0.22  blob radius

// Smooth-minimum: merges two distance fields with organic blending.
float smin(float a, float b, float k) {
    float h = clamp(0.5 + 0.5 * (b - a) / max(k, 0.001), 0.0, 1.0);
    return mix(b, a, h) - k * h * (1.0 - h);
}

void main() {
    vec2 uv = uvCentered();
    float t = u_time * u_speed * 0.35;
    int blobs = int(clamp(u_count, 2.0, 10.0));
    float visc = clamp(u_viscosity, 0.05, 1.2);
    // Size multiplier: slider range stays unchanged but blobs render ~1.4x
    // bigger so the default feels substantial without requiring the user
    // to crank the slider every time.
    float sz = clamp(u_size * 1.4, 0.08, 0.75);

    // Metaball field: compute smooth-min distance across all blobs.
    float field = 100.0;
    for (int i = 0; i < 10; i++) {
        if (i >= blobs) break;
        float fi = float(i);
        // Each blob oscillates on its own sine path.
        float xf = 0.3 + fract(fi * 0.317) * 0.5;
        float yf = 0.2 + fract(fi * 0.431) * 0.4;
        float blobR = sz * (0.7 + fract(fi * 0.53) * 0.6);
        vec2 center = vec2(
            sin(t * xf + fi * 2.1) * 0.8,
            sin(t * yf + fi * 1.7) * 0.75
        );
        float d = length(uv - center) - blobR;
        field = smin(field, d, visc * 0.5);
    }

    // Colour: map the field distance to a smooth gradient.
    // Inside the blobs (field < 0) is bright; outside fades to the bg.
    float inside = smoothstep(0.05, -0.15, field);
    float edge   = smoothstep(0.08, 0.0, abs(field)) * 0.6;
    // Pick colour from palette based on vertical position (warm bottom, cool top).
    float palIdx = (uv.y + 1.0) * 0.25;
    vec3 body = tintedPalette(palIdx) * inside;
    vec3 glow = tintedPalette(palIdx + 0.15) * edge;
    // Subtle background warmth.
    vec3 bg = tintedPalette(0.05) * 0.04;
    vec3 col = body + glow + bg * (1.0 - inside);
    fragColor = vec4(finalize(col * 1.5), 1.0);
}
