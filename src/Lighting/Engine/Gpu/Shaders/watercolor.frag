uniform float u_blobs; // hint_range(2.0, 6.0, 1.0) = 3.0  how many colour layers
uniform float u_softness; // hint_range(0.1, 1.0, 0.02) = 0.5  edge blurriness

void main() {
    vec2 uv = uvCentered();
    float t = mod(u_time * u_speed * 0.7, 1000.0);
    int layers = int(clamp(u_blobs, 2.0, 6.0));
    float soft = clamp(u_softness, 0.05, 1.5);
    vec3 col = vec3(0.0);
    float totalAlpha = 0.0;
    // Stack translucent colour washes. Each layer is a smooth domain-warped
    // fbm field thresholded into soft blobs. fbm is continuous everywhere, so
    // there are no cell boundaries -- the washes blend with no hard edges.
    // Each layer keeps a fixed palette tint so speed moves the paint without
    // shifting its colour.
    for (int i = 0; i < 6; i++) {
        if (i >= layers) break;
        float fi = float(i);
        float scale = 0.9 + fi * 0.3;
        float phase = fi * 0.71;
        vec2 q = uv * scale + vec2(
            sin(t * 0.7 + phase) * 0.7,
            cos(t * 0.6 + phase * 1.3) * 0.6);
        // Smooth organic domain warp (one fbm, applied on both axes).
        float w = fbm3(q + t * 0.25);
        q += (w - 0.5) * vec2(1.3, 1.1);
        // Smooth low-frequency blob field -> very soft-thresholded wash.
        float field = fbm3(q * 0.85 + 7.0);
        float mask = smoothstep(0.30, 0.58 + soft * 0.5, field);
        float alpha = mask * (0.46 + 0.15 * sin(fi * 2.0));
        vec3 tint = tintedPalette(fi * 0.11);
        col = mix(col, tint, alpha);
        totalAlpha += alpha * 0.3;
    }
    // Paper background: pale warm tone darkened very slightly by paint density.
    vec3 paper = tintedPalette(0.15) * 0.12;
    col = col + paper * (1.0 - min(totalAlpha, 1.0));
    fragColor = vec4(finalize(col * 1.6), 1.0);
}
