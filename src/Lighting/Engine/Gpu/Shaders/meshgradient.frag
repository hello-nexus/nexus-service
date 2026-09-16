uniform float u_blobs; // hint_range(3.0, 6.0, 1.0) = 5.0  colour source count
uniform float u_spread; // hint_range(0.3, 1.2, 0.02) = 0.8  drift radius
uniform float u_softness; // hint_range(0.3, 1.5, 0.02) = 0.8  blend softness

// Soft drifting colour field: a few colour sources move on slow paths and
// blend by gaussian weight. No texture, no edges. Aspect-locked so the
// blend stays smooth; sources roam the wider axis via the aspect scale.
void main() {
    vec2 uv = uvCentered();
    float t = u_time * u_speed * 0.4;
    int blobs = int(clamp(u_blobs, 3.0, 6.0));
    float spread = clamp(u_spread, 0.2, 1.4);
    float soft = clamp(u_softness, 0.2, 1.6);
    float aspect = u_resolution.x / u_resolution.y;

    vec3 col = vec3(0.0);
    float wsum = 1e-4;
    for (int i = 0; i < 6; i++) {
        if (i >= blobs) break;
        float fi = float(i);
        vec2 c = spread * vec2(
            sin(t * (0.6 + fract(fi * 0.37) * 0.7) + fi * 1.7),
            cos(t * (0.5 + fract(fi * 0.53) * 0.6) + fi * 2.3)
        );
        c.x *= aspect;
        vec2 dxy = uv - c;
        float w = exp(-dot(dxy, dxy) / (soft * soft));
        col += tintedPalette(fi / float(blobs) + t * 0.03) * w;
        wsum += w;
    }
    fragColor = vec4(finalize(col / wsum), 1.0);
}
