uniform float u_freq; // hint_range(2.0, 16.0, 0.5) = 8.0  wave frequency
uniform float u_amp; // hint_range(0.05, 0.4, 0.01) = 0.18  wave amplitude
void main() {
    vec2 uv = uv01();
    float t = u_time * u_speed * 0.9;
    float f = max(1.0, u_freq);
    float a = clamp(u_amp, 0.02, 0.45);
    float h = 0.5 + a * sin(uv.x * f - t * 2.5)
                  + a * 0.4 * sin(uv.x * f * 2.5 + t * 1.1);
    float d = abs(uv.y - h);
    float band = smoothstep(a + 0.05, 0.0, d);
    vec3 col = tintedPalette(uv.x - t * 0.1) * band;
    col += tintedPalette(uv.x - t * 0.1) * 0.15 * smoothstep(a * 2.0, 0.0, d);
    fragColor = vec4(finalize(col), 1.0);
}
