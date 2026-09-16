uniform float u_lines; // hint_range(6.0, 90.0, 1.0) = 36.0  scanline count
uniform float u_amplitude; // hint_range(0.0, 0.5, 0.01) = 0.18  wave height
uniform float u_flow; // hint_range(0.0, 2.0, 0.05) = 1.0  cross-frame travel

// Dense scanline field bent by two stacked sines. Every line carries the
// palette position of its own height, so the sheet runs one hue at the top
// into another at the bottom the way the theme's ramp does.
void main() {
    float ar = u_resolution.x / u_resolution.y;
    vec2 uv = uv01();
    float t = u_time * u_speed * 0.20;
    float x = uv.x * ar;

    float amp = clamp(u_amplitude, 0.0, 0.5);
    float flow = clamp(u_flow, 0.0, 2.0);
    float bend = sin(x * 2.3 - t * 1.1 * flow) * 0.6
               + sin(x * 5.1 + t * 0.7 * flow) * 0.3
               + sin(x * 9.7 - t * 1.6 * flow) * 0.1;
    float y = uv.y + bend * amp;

    float lines = clamp(u_lines, 6.0, 90.0);
    float f = fract(y * lines);
    // Width tracks the count: at a fixed profile the field turns into a solid
    // wash once the lines crowd (u_lines runs to 90).
    float lwHalf = clamp(15.0 / lines, 0.16, 0.42);
    float line = smoothstep(lwHalf, lwHalf * 0.15, abs(f - 0.5));

    // Lines crowd together where the sheet tilts; damp brightness there so the
    // crowded region glows instead of aliasing.
    float crowd = 1.0 / (1.0 + abs(bend) * amp * lines * 0.10);
    float depth = mix(0.45, 1.0, smoothstep(0.0, 0.85, y));

    vec3 hue = tintedPalette(y * 0.55 + t * 0.03);
    vec3 col = vec3(0.02, 0.015, 0.035);
    col += hue * line * crowd * depth * 1.5;
    col += hue * 0.10 * depth;

    fragColor = vec4(finalize(col), 1.0);
}
