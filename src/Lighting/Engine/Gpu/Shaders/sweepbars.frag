uniform float u_bars; // hint_range(1.0, 14.0, 1.0) = 1.0  bars across the frame
uniform float u_fill; // hint_range(0.2, 1.0, 0.01) = 0.5  bar width against the gap

// Sharp Lines' default look for simple mode: flat hue bars racing across an
// unlit frame, each bar one step further round the wheel. The hue steps per
// bar, not per frame width, so the palette reads the same at any count.
void main() {
    float f = uv01().x * u_bars - u_time * u_speed * 0.5;
    float cell = floor(f);
    // frac past the fill point is the gap, so u_fill trades bar against gap.
    float lit = 1.0 - step(clamp(u_fill, 0.2, 1.0), f - cell);
    vec3 col = hsv2rgb(vec3(cell * 0.2 + u_hue, 1.0, 1.0)) * lit;
    fragColor = vec4(finalize(col), 1.0);
}
