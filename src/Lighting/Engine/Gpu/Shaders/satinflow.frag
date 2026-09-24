uniform float u_folds; // hint_range(2.0, 8.0, 1.0) = 5.0  ribbon count
uniform float u_flow; // hint_range(0.2, 2.0, 0.05) = 1.0  flow rate
uniform float u_sheen; // hint_range(0.2, 2.0, 0.05) = 1.0  highlight intensity

// Flowing satin: overlapping low-frequency ribbons advected across the
// frame, with a specular sheen band that sweeps over the fabric.
// Analytic height field, no noise.
void main() {
    vec2 uv = uvCentered();
    float t = u_time * u_speed * 0.5;
    int folds = int(clamp(u_folds, 2.0, 8.0));
    float flow = clamp(u_flow, 0.1, 2.5);
    float sheen = clamp(u_sheen, 0.1, 2.5);

    float h = 0.0;
    for (int i = 0; i < 8; i++) {
        if (i >= folds) break;
        float fi = float(i);
        vec2 dir = vec2(cos(fi * 1.3 + 0.6), sin(fi * 0.9 + 0.4));
        float freq = 1.5 + fi * 0.7;
        h += sin(dot(uv, dir) * freq + t * flow * (0.7 + fi * 0.15) + fi) / freq;
    }
    vec3 col = tintedPalette(h * 0.5 + t * 0.05);
    float sweep = 0.5 + 0.5 * sin(h * 3.14159265 + t * flow);
    col += vec3(1.0) * pow(sweep, 6.0) * sheen * 0.5;
    col *= 0.5 + 0.5 * (h * 0.5 + 0.5); // shade the troughs
    fragColor = vec4(finalize(col), 1.0);
}
