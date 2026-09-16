uniform float u_arms; // hint_range(1.0, 10.0, 1.0) = 5.0  number of spiral arms
uniform float u_tightness; // hint_range(2.0, 16.0, 0.5) = 8.0  radial frequency
void main() {
    vec2 uv = uvCentered();
    float r = length(uv);
    float a = atan(uv.y, uv.x);
    float t = u_time * u_speed * 0.6;
    float arms = max(1.0, u_arms);
    float tight = max(1.0, u_tightness);
    float v = sin(a * arms + r * tight - t * 4.0);
    v = smoothstep(-0.6, 0.6, v);
    vec3 col = tintedPalette(a / 6.28318 + t * 0.1);
    col *= v;
    col *= 1.0 - 0.4 * smoothstep(0.8, 1.4, r);
    fragColor = vec4(finalize(col * 1.3), 1.0);
}
