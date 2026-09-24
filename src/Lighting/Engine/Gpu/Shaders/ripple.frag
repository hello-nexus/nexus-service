uniform float u_freq; // hint_range(3.0, 30.0, 0.5) = 12.0  ripple frequency
void main() {
    vec2 uv = uvCentered();
    float r = length(uv);
    float t = u_time * u_speed * 0.8;
    float f = max(1.0, u_freq);
    float w1 = sin(r * f - t * 3.0);
    float w2 = sin(r * f * 0.5  - t * 2.0 + 1.3);
    float v = (w1 + w2) * 0.5;
    v = smoothstep(-0.3, 0.6, v);
    vec3 col = tintedPalette(r * 0.5 + t * 0.08);
    col *= v;
    col *= 1.0 - 0.3 * smoothstep(0.9, 1.5, r);
    fragColor = vec4(finalize(col * 1.2), 1.0);
}
