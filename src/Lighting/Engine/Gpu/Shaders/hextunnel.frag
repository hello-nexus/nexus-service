uniform float u_cellSize; // hint_range(0.05, 0.4, 0.01) = 0.15  hex cell size
uniform float u_zoomRate; // hint_range(0.3, 3.0, 0.05) = 1.0  forward zoom
uniform float u_neon; // hint_range(0.3, 2.0, 0.05) = 1.0  neon edge intensity

// Hexagonal-prism tunnel zooming forward. Polar UVs converted into a
// hex grid with depth scrolling toward the camera. Each cell glows on
// its hex edges; depth is encoded in palette position so rings of
// distance read as bands of color.
//
// Hex distance metric: max of three axes 60deg apart - cheap and gives
// the right shape. Tunnel is implemented in screen-space rather than a
// real raymarch, which makes it dramatically cheaper for the same look.
float hexDist(vec2 p) {
    p = abs(p);
    return max(p.x * 0.866 + p.y * 0.5, p.y);
}

void main() {
    vec2 uv = uvCentered();
    float t = u_time * u_speed * u_zoomRate * 0.5;
    float cell = clamp(u_cellSize, 0.02, 0.6);
    float neon = clamp(u_neon, 0.1, 3.0);

    float r = length(uv) + 0.001;
    float a = atan(uv.y, uv.x);

    // Tunnel coordinates: depth = 1/r so the centre is far away.
    float depth = 0.4 / r + t;
    // Wrap angle into a periodic axis so the hex grid wraps around.
    float circ = a * 0.318;  // a / pi * (some scale)

    // Hex grid sampling.
    vec2 hexUv = vec2(circ * 6.0, depth) / cell;
    // Hex tiling: offset alternate rows.
    float hRow = floor(hexUv.y);
    hexUv.x += mod(hRow, 2.0) * 0.5;
    vec2 hCell = floor(hexUv);
    vec2 hLocal = fract(hexUv) - 0.5;
    float hd = hexDist(hLocal);

    // Edge glow: bright at hd close to 0.5 (cell border).
    float edge = exp(-(0.5 - hd) * 18.0);
    // Color from cell ID + depth.
    float palT = hash21(hCell) * 0.5 + depth * 0.06;
    vec3 cellColor = tintedPalette(palT);
    // Filled cell tint (dim).
    vec3 fillColor = tintedPalette(palT + 0.15) * 0.18;

    vec3 col = fillColor + cellColor * edge * neon * 1.6;
    // Brighter near the centre (close end of the tunnel).
    // perf: rational falloff replaces exp() for the soft centre glow - visually equivalent, no transcendental.
    float centerBoost = 0.4 / (1.0 + r * r * 4.0);
    col += cellColor * centerBoost;
    // Vignette fade at the very edges so the corners aren't black.
    col += tintedPalette(0.65) * 0.04;

    fragColor = vec4(finalize(col), 1.0);
}
