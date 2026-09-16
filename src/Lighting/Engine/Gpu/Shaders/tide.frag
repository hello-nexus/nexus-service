uniform float u_layers; // hint_range(2.0, 7.0, 1.0) = 5.0  wave layer count
uniform float u_amp; // hint_range(0.02, 0.18, 0.005) = 0.08  wave height
uniform float u_freq; // hint_range(1.0, 8.0, 0.5) = 4.0  wave frequency

// Stacked translucent wave layers scrolling with parallax: back layers
// sit high and drift slowly, front layers sit low and move fast. uv01 so
// the crests span the full width on any aspect (y=0 top, y=1 bottom).
// k spans 0..1 across the layers so the stack fills the full height.
void main() {
    vec2 uv = uv01();
    float t = u_time * u_speed;
    int layers = int(clamp(u_layers, 2.0, 7.0));
    float amp = clamp(u_amp, 0.01, 0.2);
    float freq = clamp(u_freq, 1.0, 8.0);

    vec3 col = tintedPalette(0.6) * 0.06; // deep base
    for (int i = 0; i < 7; i++) {
        if (i >= layers) break;
        float fi = float(i);
        float k = fi / float(max(layers - 1, 1));
        float baseY = mix(0.08, 0.99, k);
        float spd = mix(0.3, 1.2, k);
        float wave = baseY
            + amp * (1.0 - k * 0.5) * sin(uv.x * freq * (1.0 + k) + t * spd)
            + amp * 0.4 * sin(uv.x * freq * 2.3 - t * spd * 1.3 + fi);
        vec3 layerCol = tintedPalette(0.55 - k * 0.3 + t * 0.02);
        float fill = smoothstep(wave, wave + 0.012, uv.y);
        col = mix(col, layerCol, fill * 0.85);
        col += layerCol * exp(-pow((uv.y - wave) * 90.0, 2.0)) * 0.5; // crest line
    }
    fragColor = vec4(finalize(col), 1.0);
}
