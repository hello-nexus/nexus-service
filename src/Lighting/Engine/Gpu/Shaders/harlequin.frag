uniform float u_cells; // hint_range(3.0, 20.0, 1.0) = 8.0  diamond density
uniform float u_skew; // hint_range(0.3, 2.0, 0.05) = 1.0  diamond aspect
uniform float u_shift; // hint_range(0.0, 2.0, 0.05) = 1.0  colour travel

// Argyle diamond lattice: uv rotated 45deg into a diamond grid, two-tone by
// cell parity, colour travelling diagonally. Thin groove on the cell border
// keeps the diamonds crisp.
void main() {
    vec2 uv = uv01();
    float t = u_time * u_speed * 0.5;
    float cells = clamp(u_cells, 3.0, 20.0);
    float skew = clamp(u_skew, 0.3, 2.0);
    float shift = clamp(u_shift, 0.0, 2.0);

    vec2 d = vec2(uv.x * cells + uv.y * cells * skew,
                  uv.x * cells - uv.y * cells * skew);
    vec2 cell = floor(d);
    float parity = mod(cell.x + cell.y, 2.0);
    float idx = cell.x - cell.y;
    vec3 col = tintedPalette(idx * 0.05 + t * shift * 0.1 + parity * 0.12);

    vec2 fr = abs(fract(d) - 0.5) * 2.0;
    float border = step(max(fr.x, fr.y), 0.92); // 0 in the thin border
    col *= 0.55 + 0.45 * border;
    fragColor = vec4(finalize(col), 1.0);
}
