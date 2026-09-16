uniform float u_cells; // hint_range(3.0, 18.0, 1.0) = 9.0  tile density
uniform float u_wave; // hint_range(0.5, 4.0, 0.05) = 1.5  colour-wave frequency
uniform float u_pop; // hint_range(0.0, 1.5, 0.05) = 0.7  wave brightness pop

// Triangular mosaic: a 60deg rhombus lattice split into two triangles per
// cell, each flat-shaded with a hard edge, and a brightness wave travelling
// diagonally across the tiles. Regular lattice, not random cells.
void main() {
    vec2 uv = uv01();
    float t = u_time * u_speed * 0.5;
    float cells = clamp(u_cells, 3.0, 18.0);
    float wave = clamp(u_wave, 0.4, 4.0);
    float pop = clamp(u_pop, 0.0, 1.5);

    vec2 g = vec2(uv.x + uv.y * 0.5, uv.y) * cells;
    vec2 cell = floor(g);
    vec2 f = fract(g);
    float upper = step(f.x, f.y);              // which triangle of the rhombus
    float id = dot(cell, vec2(1.0, 1.7)) + upper * 0.5;

    float ph = (cell.x * 0.6 + cell.y * 0.9) * wave * 0.15 - t;
    vec3 col = tintedPalette(id * 0.13 + t * 0.02);
    col *= 1.0 + pop * (0.5 + 0.5 * sin(ph * 6.28318530));
    fragColor = vec4(finalize(col), 1.0);
}
