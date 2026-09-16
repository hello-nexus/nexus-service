uniform float u_freq; // hint_range(2.0, 18.0, 0.5) = 6.0  horizontal frequency
void main() {
    vec2 uv = uv01();
    float t = u_time * u_speed * 0.4;
    float f = max(1.0, u_freq);
    float p = uv.x + 0.2 * sin(uv.y * f + t * 2.0)
                  + 0.1 * sin(uv.y * f * 2.2 - t);
    vec3 col = tintedPalette(p - t * 0.12);
    col *= 0.9 + 0.1 * sin(uv.y * 40.0 + t * 3.0);
    fragColor = vec4(finalize(col), 1.0);
}
