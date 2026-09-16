uniform float u_aHue; // hint_range(0.0, 1.0, 0.01) = 0.12
uniform float u_aSat; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_aVal; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_bHue; // hint_range(0.0, 1.0, 0.01) = 0.62
uniform float u_bSat; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_bVal; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_spacing; // hint_range(2.0, 10.0, 1.0) = 4.0  dots across the frame
uniform float u_size; // hint_range(0.1, 0.5, 0.01) = 0.36  dot radius within its cell
uniform float u_softness; // hint_range(0.0, 1.0, 0.01) = 0.1  0 = hard dot edge, 1 = feathered

// Dot grid over a flat background.
void main() {
    float ar = u_resolution.x / u_resolution.y;
    vec2 p = uv01();
    p.x *= ar;
    vec2 cell = fract(p * max(u_spacing, 1.0)) - 0.5;
    float d = length(cell);
    float w = max(u_softness, 0.001) * 0.3;
    float e = smoothstep(u_size - w, u_size + w, d);
    vec3 ca = hsv2rgb(vec3(u_aHue, u_aSat, u_aVal));
    vec3 cb = hsv2rgb(vec3(u_bHue, u_bSat, u_bVal));
    fragColor = vec4(mix(cb, ca, e), 1.0);
}
