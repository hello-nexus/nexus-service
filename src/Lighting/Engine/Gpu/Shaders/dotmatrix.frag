uniform float u_density; // hint_range(8.0, 30.0, 1.0) = 18.0  dot grid density
uniform float u_scrollRate; // hint_range(0.2, 3.0, 0.05) = 1.0  pattern scroll rate
uniform float u_complexity; // hint_range(0.0, 1.0, 0.05) = 0.5  pattern variety

// LED dot-matrix display look. A grid of round dots whose colors come
// from a scrolling 2D function (sin layers + fbm), giving the effect
// of a stadium scoreboard scrolling color blocks across the panel.
void main() {
    vec2 uv = uv01();
    float t = mod(u_time * u_speed * u_scrollRate * 0.4, 1000.0);
    float dens = clamp(u_density, 4.0, 40.0);
    float complexity = clamp(u_complexity, 0.0, 1.5);

    // Dot grid.
    vec2 grid = uv * dens;
    vec2 cell = floor(grid);
    vec2 inCell = fract(grid) - 0.5;

    // Per-cell color from scrolling pattern. Two sin layers crossed
    // with optional fbm complexity for richer animation.
    vec2 pattern = cell / dens;
    float p1 = sin(pattern.x * 6.0 + t * 1.4);
    float p2 = sin(pattern.y * 6.0 + t * 1.1);
    float p3 = sin((pattern.x + pattern.y) * 8.0 - t * 1.7);
    float pat = (p1 + p2 + p3) * 0.16;
    // perf: fbm3 (3 oct), *1.107 to keep it centered on 0.5 - it's a coarse per-cell wash.
    pat += (fbm3(pattern * 4.0 - t * 0.3) * 1.107 - 0.5) * complexity;

    // Dot mask: round dot inside each cell, with small inter-dot spacing.
    float dotR = length(inCell);
    float dotMask = smoothstep(0.42, 0.32, dotR);

    // Per-cell brightness: the pattern value drives intensity.
    float bright = 0.4 + 0.7 * (pat * 0.5 + 0.5);

    vec3 dotColor = tintedPalette(pat * 0.4 + 0.5);
    vec3 col = dotColor * dotMask * bright * 1.4;
    // Background panel tint between dots so the matrix isn't pure black.
    col += vec3(0.02, 0.022, 0.04) * (1.0 - dotMask);

    fragColor = vec4(finalize(col), 1.0);
}
