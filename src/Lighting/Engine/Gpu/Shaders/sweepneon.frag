uniform float u_lines; // hint_range(2.0, 16.0, 1.0) = 7.0  lines across the frame
uniform float u_glow;  // hint_range(0.02, 0.5, 0.01) = 0.26  halo width

// Cyan and magenta neon rules sliding across an unlit frame, each with a halo.
// Fast, to read as a strobe of lines rather than moving bands. The halo is
// wider than it looks on screen on purpose: an LED integrates its whole pitch
// of the canvas, so a hairline averages down to a dim wash on a coarse strip.
void main() {
    float f = uv01().x * u_lines - u_time * u_speed * 1.4;
    float cell = floor(f);
    // Distance from the line at the cell's centre, 0 at the core.
    float d = abs((f - cell) - 0.5) * 2.0;
    float w = clamp(u_glow, 0.02, 0.5);
    float core = exp(-(d * d) / (w * w));
    float hue = (mod(cell, 2.0) < 1.0 ? 0.5 : 0.85) + u_hue;
    vec3 col = hsv2rgb(vec3(hue, 0.85, 1.0)) * core * 1.4;
    fragColor = vec4(finalize(col), 1.0);
}
