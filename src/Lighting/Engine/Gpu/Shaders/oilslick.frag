uniform float u_flow; // hint_range(0.1, 3.0, 0.05) = 1.5  domain warp strength
uniform float u_iridescence; // hint_range(0.3, 4.0, 0.05) = 2.5  color band density
uniform float u_scale; // hint_range(0.3, 4.0, 0.05) = 1.5  pattern scale

// Iridescent oil-on-water: two layers of fbm-based domain warp feed into
// a color band mapping that cycles through tintedPalette many times.
// Result is smooth, flowing, saturated bands of color like thin-film
// interference.
void main() {
    vec2 uv = uvCentered();
    float t = mod(u_time * u_speed * 0.2, 1000.0);
    float flow = clamp(u_flow, 0.1, 3.0);
    float irid = clamp(u_iridescence, 0.3, 4.0);
    float scale = clamp(u_scale, 0.3, 4.0);

    vec2 p = uv * scale;
    // perf: q is a pure warp offset (top octaves washed out by the warp) -> fbm3.
    vec2 q = vec2(fbm3(p + vec2(t * 0.3, 0.0)),
                  fbm3(p + vec2(0.0, t * 0.25)));
    // perf: r feeds the bands but reads after the warp; fbm3 keeps the interference
    vec2 r = vec2(fbm3(p + q * flow + vec2(t * 0.2, 1.7)),
                  fbm3(p + q * flow + vec2(9.2, t * 0.22)));

    // Thickness drives palette position; many cycles of the rainbow pass
    // through as thickness changes, mimicking thin-film interference.
    float thickness = r.x * irid + r.y * 0.3;
    // Smooth rotating directional bias instead of atan(y,x), which has a
    // branch-cut discontinuity along the negative x axis and caused a
    // visible seam / notch on the left side of the frame.
    thickness += 0.09 * (uv.x * cos(t * 0.4) + uv.y * sin(t * 0.4)) + t * 0.05;

    vec3 col = tintedPalette(thickness);
    // Bright secondary band where fbm peaks, for contrast.
    float band = smoothstep(0.55, 0.85, r.y);
    col += tintedPalette(thickness + 0.15) * band * 0.7;
    // Darker streaks where fbm troughs, giving depth.
    float streak = smoothstep(0.15, 0.0, r.x);
    col *= (1.0 - streak * 0.55);

    fragColor = vec4(finalize(col * 1.35), 1.0);
}
