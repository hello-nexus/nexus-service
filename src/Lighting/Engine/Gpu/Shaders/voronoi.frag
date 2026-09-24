uniform float u_scale; // hint_range(1.0, 6.0, 0.1) = 3.0  cell density
uniform float u_edgeWidth; // hint_range(0.01, 0.15, 0.005) = 0.05  glowing border thickness
uniform float u_drift; // hint_range(0.0, 1.0, 0.02) = 0.6  how much cell centers drift

// Animated Voronoi returning (cell distance, second-closest distance, cell id hash).
vec3 voronoi2(vec2 p, float t, float drift) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    float d1 = 8.0, d2 = 8.0;
    float cellId = 0.0;
    for (int y = -1; y <= 1; y++) {
        for (int x = -1; x <= 1; x++) {
            vec2 n = vec2(float(x), float(y));
            vec2 seed = i + n;
            vec2 pt = vec2(hash21(seed), hash21(seed + 99.0));
            // Drift the cell centers over time.
            pt = 0.5 + drift * 0.45 * sin(t * 0.4 + pt * 6.28318);
            // perf: rank cells by squared distance (no sqrt); sqrt only the two winners below.
            vec2 dv = n + pt - f;
            float d = dot(dv, dv);
            if (d < d1) { d2 = d1; d1 = d; cellId = hash21(seed + 777.0); }
            else if (d < d2) { d2 = d; }
        }
    }
    return vec3(sqrt(d1), sqrt(d2), cellId);
}

void main() {
    vec2 uv = uvCentered();
    float t = u_time * u_speed * 0.5;
    float sc = max(0.5, u_scale);
    float ew = clamp(u_edgeWidth, 0.005, 0.2);
    float dr = clamp(u_drift, 0.0, 1.0);

    vec3 v = voronoi2(uv * sc, t, dr);
    float edge = smoothstep(ew, 0.0, v.y - v.x);
    // Cell body colour from the cell id.
    vec3 cellCol = tintedPalette(v.z);
    // Darken cells slightly, let edges glow.
    vec3 body = cellCol * 0.35;
    vec3 edgeCol = tintedPalette(v.z + 0.3) * edge * 1.8;
    vec3 col = body + edgeCol;
    fragColor = vec4(finalize(col), 1.0);
}
