uniform float u_smears; // hint_range(2.0, 10.0, 1.0) = 4.0  passes across the frame
uniform float u_ragged; // hint_range(0.0, 0.6, 0.01) = 0.25  how torn each edge reads

// Violet ink smeared across the frame: travelling passes with noise tearing
// their edges and mottling the body. The slow tile of the set.
void main() {
    vec2 uv = uv01();
    float t = u_time * u_speed * 0.6;
    float n = fbm(vec2(uv.x * 2.0 - t * 2.0, uv.y * 3.0));
    float f = (uv.x + (n - 0.5) * u_ragged) * u_smears - t;
    float cell = floor(f);
    float d = f - cell;
    float body = smoothstep(0.0, 0.18, d) * smoothstep(1.0, 0.82, d);
    float hue = (mod(cell, 2.0) < 1.0 ? 0.76 : 0.86) + u_hue;
    vec3 col = hsv2rgb(vec3(hue, 0.85, 1.0)) * body * (0.7 + 0.5 * n);
    fragColor = vec4(finalize(col), 1.0);
}
