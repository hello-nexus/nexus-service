uniform float u_levels; // hint_range(3.0, 16.0, 1.0) = 8.0  contour levels
uniform float u_scale; // hint_range(0.5, 3.0, 0.05) = 1.4  field scale
uniform float u_line; // hint_range(0.0, 1.0, 0.05) = 0.5  contour line strength

// Topographic contour bands: a smooth analytic field (summed sines, no
// noise) posterized into flat colour steps, with a bright line at each
// level boundary. Crisp nested shapes, like a moving contour map.
void main() {
    vec2 uv = uvCentered();
    float t = u_time * u_speed * 0.4;
    float levels = clamp(u_levels, 3.0, 16.0);
    float scale = clamp(u_scale, 0.5, 3.0);
    float line = clamp(u_line, 0.0, 1.0);

    float f = sin(uv.x * 1.6 * scale + t)
            + sin(uv.y * 1.4 * scale - t * 0.8)
            + sin((uv.x + uv.y) * 1.1 * scale + t * 0.6)
            + sin(length(uv) * 3.0 * scale - t);
    f = f * 0.25 + 0.5;

    float q = f * levels;
    vec3 col = tintedPalette(floor(q) / levels + t * 0.03);
    float edge = abs(fract(q) - 0.5) * 2.0;        // 1 at a level boundary
    col = mix(col, vec3(1.0), smoothstep(0.85, 1.0, edge) * line * 0.6);
    fragColor = vec4(finalize(col), 1.0);
}
