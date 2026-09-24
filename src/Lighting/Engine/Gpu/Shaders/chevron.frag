uniform float u_bands; // hint_range(4.0, 40.0, 1.0) = 14.0  stripe count
uniform float u_angle; // hint_range(0.0, 2.0, 0.05) = 1.0  chevron sharpness
uniform float u_width; // hint_range(0.05, 0.5, 0.01) = 0.18  groove width

// Scrolling chevron stripes: solid palette bands separated by a thin dark
// groove for crisp edges. The V shape comes from offsetting x by |y-0.5|.
void main() {
    vec2 uv = uv01();
    float t = u_time * u_speed * 0.5;
    float bands = clamp(u_bands, 4.0, 48.0);
    float ang = clamp(u_angle, 0.0, 2.0);
    float w = clamp(u_width, 0.04, 0.5);

    float c = uv.x + abs(uv.y - 0.5) * ang;
    float phase = c * bands - t;
    vec3 col = tintedPalette(floor(phase) * 0.07 + t * 0.03);
    // thin dark groove at each band boundary -> hard, clean separation
    float groove = smoothstep(0.0, w, fract(phase)) * smoothstep(1.0, 1.0 - w, fract(phase));
    col *= 0.3 + 0.7 * groove;
    fragColor = vec4(finalize(col), 1.0);
}
