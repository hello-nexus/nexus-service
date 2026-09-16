uniform float u_aHue; // hint_range(0.0, 1.0, 0.01) = 0.55
uniform float u_aSat; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_aVal; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_bHue; // hint_range(0.0, 1.0, 0.01) = 0.88
uniform float u_bSat; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_bVal; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_count; // hint_range(1.0, 8.0, 1.0) = 3.0  ring pairs from the centre out
uniform float u_softness; // hint_range(0.0, 1.0, 0.01) = 0.05  0 = hard rings, 1 = blended

// Concentric alternating rings.
void main() {
    float ar = u_resolution.x / u_resolution.y;
    vec2 p = uv01() - 0.5;
    p.x *= ar;
    float d = length(p) / (0.5 * sqrt(ar * ar + 1.0));
    float t = fract(d * max(u_count, 0.5));
    float w = max(u_softness, 0.001) * 0.5;
    float e = smoothstep(0.5 - w, 0.5 + w, t);
    vec3 ca = hsv2rgb(vec3(u_aHue, u_aSat, u_aVal));
    vec3 cb = hsv2rgb(vec3(u_bHue, u_bSat, u_bVal));
    fragColor = vec4(mix(ca, cb, e), 1.0);
}
