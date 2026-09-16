uniform float u_shape; // hint_range(0.0, 2.0, 1.0) = 0.0  0=hex, 1=square, 2=triangle
uniform float u_morph; // hint_range(0.0, 1.3, 0.02) = 0.6  tile pulse depth
uniform float u_edge; // hint_range(0.02, 0.35, 0.01) = 0.15  edge line width

// Geometric tessellation: hex, square, or triangular tile grid whose cells
// pulse in size and shift hue based on their grid position. Edge bands
// glow in a complementary palette sample for an iridescent look.
void main() {
    vec2 uv = uvCentered();
    float t = u_time * u_speed * 1.1;
    int shape = int(clamp(u_shape + 0.5, 0.0, 2.0));
    float morph = clamp(u_morph, 0.0, 1.3);
    float edge = clamp(u_edge, 0.02, 0.35);

    vec2 p = uv * 5.0;
    float d;
    vec2 cell;
    if (shape == 0) {
        vec2 h = vec2(1.7320508, 1.0);
        vec2 a1 = mod(p, h) - h * 0.5;
        vec2 b1 = mod(p + h * 0.5, h) - h * 0.5;
        vec2 gv = dot(a1, a1) < dot(b1, b1) ? a1 : b1;
        cell = p - gv;
        vec2 ag = abs(gv);
        d = max(ag.x * 0.866 + ag.y * 0.5, ag.y);
    } else if (shape == 1) {
        cell = floor(p) + 0.5;
        vec2 gv = p - cell;
        d = max(abs(gv.x), abs(gv.y));
    } else {
        // Triangles approximated by squished squares for simplicity.
        cell = floor(p * vec2(1.0, 1.1547)) + 0.5;
        vec2 gv = p * vec2(1.0, 1.1547) - cell;
        d = max(abs(gv.x * 1.1), abs(gv.y));
    }

    // Tile size oscillates per cell.
    float size = 0.5 + 0.16 * morph * sin(t + dot(cell, vec2(0.3, 0.7)));
    float tile = smoothstep(size, size - 0.06, d);
    float edgeGlow = smoothstep(size + edge, size, d) * (1.0 - tile);

    float hue = dot(cell, vec2(0.05, 0.08)) + t * 0.1;
    vec3 face = tintedPalette(hue) * 0.65;
    vec3 line = tintedPalette(hue + 0.2) * 1.35;
    vec3 col = face * tile + line * edgeGlow;

    fragColor = vec4(finalize(col), 1.0);
}
