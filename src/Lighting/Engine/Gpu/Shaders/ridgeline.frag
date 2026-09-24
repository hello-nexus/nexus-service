uniform float u_layers; // hint_range(2.0, 7.0, 1.0) = 5.0  ridge layer count
uniform float u_jag; // hint_range(1.0, 8.0, 0.1) = 4.0  peak frequency / sharpness
uniform float u_height; // hint_range(0.05, 0.4, 0.01) = 0.18  ridge height

// Layered angular mountain ridges with parallax. Opaque crisp silhouettes
// (hard fill edge + bright ridgeline), built from triangle waves so peaks
// are sharp, not rounded like tide. uv01; k spans the full height.
float tri(float x) { return abs(fract(x) * 2.0 - 1.0); }

void main() {
    vec2 uv = uv01();
    float t = u_time * u_speed;
    int layers = int(clamp(u_layers, 2.0, 7.0));
    float jag = clamp(u_jag, 1.0, 8.0);
    float h = clamp(u_height, 0.04, 0.45);

    vec3 col = tintedPalette(0.62) * 0.05;
    for (int i = 0; i < 7; i++) {
        if (i >= layers) break;
        float fi = float(i);
        float k = fi / float(max(layers - 1, 1));
        float baseY = mix(0.12, 0.99, k);
        float spd = mix(0.2, 1.0, k);
        float peaks = tri(uv.x * jag * (0.6 + k) + t * spd) * 0.7
                    + tri(uv.x * jag * 1.9 - t * spd * 0.6) * 0.3;
        float ridge = baseY - h * (1.0 - 0.4 * k) * (peaks - 0.5);
        vec3 layerCol = tintedPalette(0.55 - k * 0.32 + t * 0.02);
        col = mix(col, layerCol, smoothstep(ridge, ridge + 0.006, uv.y));
        col += layerCol * smoothstep(0.012, 0.0, abs(uv.y - ridge)) * 0.4;
    }
    fragColor = vec4(finalize(col), 1.0);
}
