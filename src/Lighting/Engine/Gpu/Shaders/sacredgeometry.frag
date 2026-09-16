uniform float u_layers; // hint_range(2.0, 8.0, 1.0) = 5.0  nested shape count
uniform float u_edge; // hint_range(0.2, 1.2, 0.02) = 0.6  edge glow width
uniform float u_pulse; // hint_range(0.3, 3.0, 0.05) = 1.0  pulsation rate

// Mandala of nested rings and regular polygons. Each layer alternates
// between a circle and an N-gon (sides = 3 + layer index), drifting
// through different rotation phases so the composition breathes.
void main() {
    vec2 uv = uvCentered();
    float t = u_time * u_speed * 0.75;
    int L = int(clamp(u_layers, 2.0, 8.0));
    float edge = clamp(u_edge, 0.15, 1.2);
    float pulse = max(0.2, u_pulse);

    float r = length(uv);
    float a = atan(uv.y, uv.x);
    vec3 col = vec3(0.0);
    float edgeSharp = 40.0 / edge;

    for (int i = 0; i < 8; i++) {
        if (i >= L) break;
        float idx = float(i);
        float layer = (idx + 1.0) / float(L);
        float ringR = layer * 0.88 + 0.035 * sin(t * pulse + idx * 1.6);

        // perf: only one of ring/poly is ever selected per layer, so compute
        // just that shape -- halves the exp() count in the loop. x*x for the
        // Gaussian arg in place of pow(x,2.0). Look is unchanged.
        float shape;
        if (mod(idx, 2.0) < 0.5) {
            float dr = (r - ringR) * edgeSharp;
            shape = exp(-(dr * dr));
        } else {
            // Polygon SDF (regular N-gon).
            float sides = 3.0 + idx;
            float sectorAng = 6.28318530718 / sides;
            float polyA = mod(a + t * (idx * 0.1 + 0.12), sectorAng) - sectorAng * 0.5;
            float polyR = ringR * cos(sectorAng * 0.5) / max(cos(polyA), 0.001);
            float dp = (r - polyR) * edgeSharp;
            shape = exp(-(dp * dp));
        }
        vec3 tint = tintedPalette(layer * 0.28 + t * 0.08);
        col += tint * shape * 1.35;
    }
    // Central eye.
    col += tintedPalette(0.1) * exp(-r * r * 55.0) * 0.85;

    fragColor = vec4(finalize(col * 1.05), 1.0);
}
