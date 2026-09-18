uniform float u_bars; // hint_range(2.0, 14.0, 1.0) = 4.0  bars across the frame
uniform float u_fill; // hint_range(0.2, 1.0, 0.01) = 0.45  bar width against the gap

// Hard-edged red bars racing across an unlit frame, every other one dimmed so
// the ladder reads even where two bars meet a device's ends. The fastest of
// the sharp-edged tiles.
void main() {
    float f = uv01().x * u_bars - u_time * u_speed * 1.6;
    float cell = floor(f);
    // frac past the fill point is the gap, so u_fill trades bar against gap.
    float lit = 1.0 - step(clamp(u_fill, 0.2, 1.0), f - cell);
    float shade = mod(cell, 2.0) < 1.0 ? 1.0 : 0.5;
    vec3 col = hsv2rgb(vec3(u_hue, 1.0, shade)) * lit;
    fragColor = vec4(finalize(col), 1.0);
}
